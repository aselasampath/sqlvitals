using System.Text.RegularExpressions;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Owns the lifecycle of the live stored-procedure trace: picks a capture mode, creates
/// and drops the Extended Events session, and polls whichever source is active.
/// </summary>
/// <remarks>
/// Stateful by design — it holds the ring-buffer de-duplication window and the previous
/// procedure-stats snapshot across polls. One instance lives for the app session, created
/// by <see cref="WaitStatsRepository"/>.
/// </remarks>
public partial class SpTraceRepository(IConfiguration configuration)
    : BaseRepository(configuration), ISpTraceRepository
{
    private const string DefaultSessionName = "SqlVitals_SpTrace";

    /// <summary>
    /// Upper bound on remembered ring-buffer event keys. The buffer holds at most
    /// max_events_limit events, so a window this size covers many polls' worth of overlap.
    /// </summary>
    private const int DedupWindowSize = 20_000;

    private readonly string _sessionName = SanitiseIdentifier(
        configuration.GetValue<string>("TraceSettings:SessionName") ?? DefaultSessionName);

    // Start/Stop can race a poll tick from the UI timer; serialise all server interaction.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly HashSet<string> _seenEventKeys = [];
    private readonly Queue<string>   _seenEventOrder = new();

    private Dictionary<ProcStatsKey, ProcStatsSnapshot> _previousStats = [];

    private bool      _isRunning;
    private bool      _sessionCreated;   // did we create it? only then may we drop it
    private bool      _filterModulesClientSide;  // server rejected the object_type predicate
    private bool      _isAzureSqlDb;
    private TraceMode _mode = TraceMode.DmvFallback;
    private string?   _modeReason;
    private long      _eventsCaptured;
    private long      _eventsDropped;
    private bool      _targetTruncated;

    // ── Lifecycle ────────────────────────────────────────────────────────

    public async Task<TraceSessionStatus> StartTraceAsync(SpTraceOptions options)
    {
        await _gate.WaitAsync();
        try
        {
            ResetState();

            using var conn = CreateConnection();

            var editionRow = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
            _isAzureSqlDb = editionRow is not null && Convert.ToInt32(editionRow.Edition) == 5;

            if (!await CanCreateEventSessionAsync(conn))
            {
                _mode = TraceMode.DmvFallback;
                _modeReason = _isAzureSqlDb
                    ? "Login lacks ALTER ANY DATABASE EVENT SESSION — showing per-procedure aggregates from DMV counters instead of individual calls."
                    : "Login lacks ALTER ANY EVENT SESSION — showing per-procedure aggregates from DMV counters instead of individual calls.";
            }
            else
            {
                try
                {
                    await CreateAndStartSessionAsync(conn, options);
                    _mode = TraceMode.ExtendedEvents;
                    _modeReason = null;
                    _sessionCreated = true;
                }
                catch (WaitStatsException ex)
                {
                    // Permission probes can pass and the DDL still fail — a policy, a
                    // read-only replica, or a build that rejects one of the events.
                    _mode = TraceMode.DmvFallback;
                    _modeReason = $"Could not create the event session ({ex.InnerException?.Message ?? ex.Message}) — falling back to DMV counters.";
                }
            }

            // Seed the DMV baseline in both modes so the aggregate view has something to
            // measure against on the next poll, and works as a cross-check alongside XE.
            _previousStats = await ReadProcedureStatsAsync(conn, options.CurrentDatabaseOnly);

            _isRunning = true;
            return BuildStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TraceSessionStatus> StopTraceAsync()
    {
        // Bounded wait rather than an open-ended one: a poll in flight holds the gate for a
        // full server round trip, and on app shutdown there is a hard deadline. Dropping
        // opens its own connection, so going ahead without the gate is safe — the worst
        // case is a poll reading a session that is about to disappear, which it tolerates.
        var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            if (_sessionCreated)
            {
                try
                {
                    using var conn = CreateConnection();
                    await DropSessionAsync(conn);
                }
                catch (WaitStatsException)
                {
                    // Best effort. The next start drops any leftover session by name, so a
                    // failure here cannot strand one permanently.
                }
                _sessionCreated = false;
            }

            _isRunning = false;
            return BuildStatus();
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    public async Task<TraceSessionStatus> GetTraceStatusAsync()
    {
        if (!_isRunning || _mode != TraceMode.ExtendedEvents)
            return BuildStatus();

        await _gate.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var snapshot = SpTraceXmlParser.Parse(await ReadRingBufferXmlAsync(conn));
            _eventsDropped   = snapshot.DroppedCount;
            _targetTruncated = snapshot.Truncated;
            return BuildStatus();
        }
        catch (WaitStatsException)
        {
            return BuildStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Polling ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SpTraceEvent>> PollTraceEventsAsync()
    {
        if (!_isRunning || _mode != TraceMode.ExtendedEvents) return [];

        await _gate.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var snapshot = SpTraceXmlParser.Parse(await ReadRingBufferXmlAsync(conn));

            _eventsDropped   = snapshot.DroppedCount;
            _targetTruncated = snapshot.Truncated;

            // The ring buffer is a rolling snapshot, so every poll re-returns events the
            // previous polls already delivered. Emit only keys not seen before.
            var fresh = new List<SpTraceEvent>();
            foreach (var ev in snapshot.Events)
            {
                // When the server would not take the object_type predicate, module_end is
                // also carrying functions and triggers. Drop them here.
                if (_filterModulesClientSide && !ev.IsStoredProcedure) continue;
                if (!MarkSeen(ev.DedupKey)) continue;
                fresh.Add(ev);
            }

            _eventsCaptured += fresh.Count;
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SpAggregateRow>> PollProcedureStatsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var conn = CreateConnection();
            var current = await ReadProcedureStatsAsync(conn, currentDatabaseOnly: true);
            var rows    = ProcedureStatsDelta.Compute(_previousStats, current);
            _previousStats = current;
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SpAggregateRow>> GetProcedureStatsTotalsAsync(int topN = 25)
    {
        using var conn = CreateConnection();
        var snapshot = await ReadProcedureStatsAsync(conn, currentDatabaseOnly: true);

        return snapshot.Values
            .Where(s => s.ExecutionCount > 0)
            .Select(s => new SpAggregateRow(
                DatabaseName:      s.DatabaseName,
                SchemaName:        s.SchemaName,
                ProcedureName:     s.ProcedureName,
                ExecCount:         s.ExecutionCount,
                AvgDurationMs:     s.TotalElapsedTimeUs / 1000.0 / s.ExecutionCount,
                MaxDurationMs:     s.MaxElapsedTimeUs / 1000.0,
                TotalDurationMs:   s.TotalElapsedTimeUs / 1000.0,
                AvgCpuMs:          s.TotalWorkerTimeUs / 1000.0 / s.ExecutionCount,
                AvgLogicalReads:   s.TotalLogicalReads / s.ExecutionCount,
                TotalLogicalReads: s.TotalLogicalReads,
                LastExecutionTime: s.LastExecutionTime))
            .OrderByDescending(r => r.TotalDurationMs)
            .Take(Math.Max(1, topN))
            .ToList();
    }

    // ── Permission probe ─────────────────────────────────────────────────

    private async Task<bool> CanCreateEventSessionAsync(System.Data.IDbConnection conn)
    {
        var sql = _isAzureSqlDb
            ? "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY DATABASE EVENT SESSION') AS HasPerm"
            : "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION') AS HasPerm";

        try
        {
            var row = await QFirst(conn, sql);
            return row is not null && Convert.ToInt32(row.HasPerm) == 1;
        }
        catch (WaitStatsException)
        {
            return false;
        }
    }

    // ── Event session DDL ────────────────────────────────────────────────

    private async Task CreateAndStartSessionAsync(System.Data.IDbConnection conn, SpTraceOptions options)
    {
        // A previous run killed mid-trace leaves the session behind; clear it first so the
        // CREATE below cannot fail on a name collision.
        await DropSessionAsync(conn);

        var scope        = _isAzureSqlDb ? "DATABASE" : "SERVER";
        var minDurationUs = Math.Max(0, options.MinDurationMs) * 1000L;
        var maxMemoryKb   = Math.Clamp(options.MaxRingBufferKb, 512, 32_768);
        var maxEvents     = Math.Clamp(options.MaxEventsLimit, 100, 10_000);

        // A database-scoped session is already confined to one database, so the predicate
        // is only needed for the server-scoped case.
        var dbPredicate = !_isAzureSqlDb && options.CurrentDatabaseOnly
            ? " AND sqlserver.database_id = DB_ID()"
            : string.Empty;

        // DB_ID() cannot appear inside an event predicate, so resolve it first.
        if (dbPredicate.Length > 0)
        {
            var dbIdRow = await QFirst(conn, "SELECT DB_ID() AS DbId");
            var dbId = dbIdRow is null ? 0 : Convert.ToInt32(dbIdRow.DbId);
            dbPredicate = dbId > 0 ? $" AND sqlserver.database_id = {dbId}" : string.Empty;
        }

        const string actions =
            "sqlserver.session_id, sqlserver.database_name, sqlserver.username, " +
            "sqlserver.client_app_name, sqlserver.client_hostname";

        // module_end fires for every module type — functions and triggers on a busy OLTP
        // server would swamp the buffer, so restrict it to stored procedures where the
        // server allows it. 8272 is the object_type map value for a stored procedure, but
        // not every edition and build accepts that predicate (Azure SQL Database rejects
        // it outright), so fall back to a session without it and filter client-side
        // instead. Losing the predicate costs bandwidth, not correctness.
        string BuildDdl(bool withObjectTypePredicate)
        {
            var modulePredicate = withObjectTypePredicate
                ? $"duration >= {minDurationUs} AND object_type = 8272{dbPredicate}"
                : $"duration >= {minDurationUs}{dbPredicate}";

            return $"""
                CREATE EVENT SESSION [{_sessionName}] ON {scope}
                ADD EVENT sqlserver.rpc_completed (
                    ACTION ({actions})
                    WHERE (duration >= {minDurationUs}{dbPredicate})),
                ADD EVENT sqlserver.module_end (
                    SET collect_statement = 1
                    ACTION ({actions})
                    WHERE ({modulePredicate}))
                ADD TARGET package0.ring_buffer (
                    SET max_memory = {maxMemoryKb}, max_events_limit = {maxEvents})
                WITH (MAX_DISPATCH_LATENCY = 2 SECONDS, STARTUP_STATE = OFF, TRACK_CAUSALITY = ON);
                """;
        }

        try
        {
            await Exec(conn, BuildDdl(withObjectTypePredicate: true));
            _filterModulesClientSide = false;
        }
        catch (WaitStatsException)
        {
            // A failed CREATE leaves nothing behind, but drop defensively in case the
            // statement partially applied before erroring.
            await DropSessionAsync(conn);
            await Exec(conn, BuildDdl(withObjectTypePredicate: false));
            _filterModulesClientSide = true;
        }

        await Exec(conn, $"ALTER EVENT SESSION [{_sessionName}] ON {scope} STATE = START;");
    }

    private async Task DropSessionAsync(System.Data.IDbConnection conn)
    {
        var scope   = _isAzureSqlDb ? "DATABASE" : "SERVER";
        var catalog = _isAzureSqlDb ? "sys.database_event_sessions" : "sys.server_event_sessions";

        // DROP EVENT SESSION has no IF EXISTS form, hence the catalog guard.
        await Exec(conn, $"""
            IF EXISTS (SELECT 1 FROM {catalog} WHERE name = @name)
                DROP EVENT SESSION [{_sessionName}] ON {scope};
            """, new { name = _sessionName });
    }

    private async Task<string?> ReadRingBufferXmlAsync(System.Data.IDbConnection conn)
    {
        var sessions = _isAzureSqlDb ? "sys.dm_xe_database_sessions"        : "sys.dm_xe_sessions";
        var targets  = _isAzureSqlDb ? "sys.dm_xe_database_session_targets" : "sys.dm_xe_session_targets";

        var row = await QFirst(conn, $"""
            SELECT CAST(t.target_data AS NVARCHAR(MAX)) AS TargetData
            FROM {sessions} s
            JOIN {targets} t ON t.event_session_address = s.address
            WHERE s.name = @name AND t.target_name = 'ring_buffer'
            """, new { name = _sessionName });

        return row?.TargetData is null or DBNull ? null : (string)row.TargetData;
    }

    // ── Procedure stats snapshot ─────────────────────────────────────────

    private async Task<Dictionary<ProcStatsKey, ProcStatsSnapshot>> ReadProcedureStatsAsync(
        System.Data.IDbConnection conn, bool currentDatabaseOnly)
    {
        var dbFilter = currentDatabaseOnly ? "WHERE ps.database_id = DB_ID()" : "";

        var sql = $"""
            SELECT
                ps.database_id                                       AS DatabaseId,
                ps.object_id                                         AS ObjectId,
                ps.cached_time                                       AS CachedTime,
                ISNULL(DB_NAME(ps.database_id), '')                  AS DatabaseName,
                ISNULL(OBJECT_SCHEMA_NAME(ps.object_id, ps.database_id), '') AS SchemaName,
                ISNULL(OBJECT_NAME(ps.object_id, ps.database_id), '') AS ProcedureName,
                ps.execution_count                                   AS ExecutionCount,
                ps.total_worker_time                                 AS TotalWorkerTimeUs,
                ps.total_elapsed_time                                AS TotalElapsedTimeUs,
                ps.max_elapsed_time                                  AS MaxElapsedTimeUs,
                ps.total_logical_reads                               AS TotalLogicalReads,
                ps.total_logical_writes                              AS TotalLogicalWrites,
                ps.total_physical_reads                              AS TotalPhysicalReads,
                ps.last_execution_time                               AS LastExecutionTime
            FROM sys.dm_exec_procedure_stats ps
            {dbFilter}
            """;

        var rows = await Q(conn, sql);
        var result = new Dictionary<ProcStatsKey, ProcStatsSnapshot>();

        foreach (var r in rows)
        {
            var key = new ProcStatsKey(
                Convert.ToInt32(r.DatabaseId),
                Convert.ToInt32(r.ObjectId),
                Convert.ToDateTime(r.CachedTime));

            // A procedure dropped since it was cached leaves an unresolvable name.
            var procName = (string)r.ProcedureName;
            if (string.IsNullOrEmpty(procName)) continue;

            result[key] = new ProcStatsSnapshot(
                key,
                (string)r.DatabaseName,
                (string)r.SchemaName,
                procName,
                Convert.ToInt64(r.ExecutionCount),
                Convert.ToInt64(r.TotalWorkerTimeUs),
                Convert.ToInt64(r.TotalElapsedTimeUs),
                Convert.ToInt64(r.MaxElapsedTimeUs),
                Convert.ToInt64(r.TotalLogicalReads),
                Convert.ToInt64(r.TotalLogicalWrites),
                Convert.ToInt64(r.TotalPhysicalReads),
                r.LastExecutionTime is DBNull ? null : Convert.ToDateTime(r.LastExecutionTime));
        }

        return result;
    }

    // ── State helpers ────────────────────────────────────────────────────

    private void ResetState()
    {
        _seenEventKeys.Clear();
        _seenEventOrder.Clear();
        _previousStats   = [];
        _filterModulesClientSide = false;
        _eventsCaptured  = 0;
        _eventsDropped   = 0;
        _targetTruncated = false;
    }

    /// <summary>Records a key, returning true when it had not been seen before.</summary>
    private bool MarkSeen(string key)
    {
        if (!_seenEventKeys.Add(key)) return false;

        _seenEventOrder.Enqueue(key);
        while (_seenEventOrder.Count > DedupWindowSize)
            _seenEventKeys.Remove(_seenEventOrder.Dequeue());

        return true;
    }

    private TraceSessionStatus BuildStatus() => new(
        _isRunning, _mode, _sessionName, _modeReason,
        _eventsCaptured, _eventsDropped, _targetTruncated);

    /// <summary>
    /// Reduces a configured session name to identifier characters. The name is
    /// interpolated into DDL, where it cannot be passed as a parameter.
    /// </summary>
    internal static string SanitiseIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultSessionName;

        var cleaned = IdentifierPattern().Replace(name.Trim(), "");
        return cleaned.Length == 0 ? DefaultSessionName : cleaned;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
