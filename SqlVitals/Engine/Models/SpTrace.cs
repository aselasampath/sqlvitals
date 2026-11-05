namespace SqlVitals.Engine.Models;

// ── Live stored-procedure trace ───────────────────────────────────────
// Two capture modes feed these models:
//   ExtendedEvents — per-call rows from an app-managed XE session (SpTraceEvent)
//   DmvFallback    — per-procedure aggregates from sys.dm_exec_procedure_stats
//                    snapshot deltas (SpAggregateRow)
// The DMV mode needs no permission beyond VIEW SERVER STATE, so the page still
// works on servers where the login cannot create event sessions.

/// <summary>How the trace is capturing data.</summary>
public enum TraceMode
{
    /// <summary>Per-call rows from an Extended Events session.</summary>
    ExtendedEvents,

    /// <summary>Per-procedure aggregates from sys.dm_exec_procedure_stats deltas.</summary>
    DmvFallback
}

/// <summary>User-tunable knobs applied when a trace is started.</summary>
public record SpTraceOptions(
    int  MinDurationMs      = 0,
    int  MaxRingBufferKb    = 4096,
    int  MaxEventsLimit     = 1000,
    bool CurrentDatabaseOnly = true)
{
    public static SpTraceOptions Default { get; } = new();
}

/// <summary>A single captured stored-procedure / RPC completion.</summary>
public record SpTraceEvent(
    DateTime EventTime,        // converted to local time for display
    string   EventName,        // rpc_completed | module_end | sp_statement_completed
    string?  ObjectName,       // schema-qualified procedure name where resolvable
    string?  ObjectType,       // module_end only: "P" for a stored procedure, "TR", "FN", …
    string?  DatabaseName,
    long     DurationMs,       // XE reports microseconds — converted here
    long     CpuTimeMs,
    long     LogicalReads,
    long     PhysicalReads,
    long     Writes,
    long     RowsReturned,     // "RowCount" is a reserved word — see README
    int      SessionId,
    string?  LoginName,
    string?  ClientAppName,
    string?  ClientHostName,
    string?  Statement,
    string?  ActivityId)       // dedup key, present when TRACK_CAUSALITY = ON
{
    /// <summary>
    /// The duration as XE reports it, in microseconds (#238); 0 when it wasn't read, as for an event
    /// made in a test, and <see cref="DurationMs"/> stands in. Spans are worked out from it: in
    /// whole milliseconds, a call's start could be read up to a millisecond late.
    /// </summary>
    public long DurationUs { get; init; }

    /// <summary>When the call began: its completion less its duration.</summary>
    public DateTime StartTime => EventTime - TimeSpan.FromMicroseconds(DurationUs > 0 ? DurationUs : DurationMs * 1000);

    /// <summary>
    /// Stable identity for de-duplicating events across ring-buffer polls. The ring
    /// buffer is a rolling snapshot, so each poll re-returns events already seen.
    /// </summary>
    public string DedupKey => ActivityId is { Length: > 0 }
        ? $"{EventName}|{ActivityId}"
        : $"{EventName}|{EventTime.Ticks}|{SessionId}|{DurationMs}|{Statement?.Length ?? 0}";

    /// <summary>
    /// True when this event represents a stored procedure. <c>rpc_completed</c> always is;
    /// <c>module_end</c> also fires for functions and triggers, which this feature excludes.
    /// An event with no captured object_type is kept rather than guessed away.
    /// </summary>
    public bool IsStoredProcedure =>
        !EventName.Equals("module_end", StringComparison.OrdinalIgnoreCase)
        || ObjectType is null
        || ObjectType is "P" or "PC" or "RF" or "8272";
}

/// <summary>
/// What called a captured call, and how deep it ran (#238), worked out by
/// <see cref="Repositories.SpTraceNesting"/>.
/// </summary>
/// <param name="CalledBy">The calling procedure's name; <see cref="Client"/> for a call the client
/// made; null when the caller isn't known.</param>
/// <param name="Level">1 for a call the client made, 2 for a procedure that one called, and so on,
/// counting the callers captured (a call whose caller isn't known is 1); null when the call can't
/// be tied to its request.</param>
/// <param name="RepeatsRpc">The module_end of the procedure the client called: the same call as its
/// rpc_completed row, so its time is in that row's.</param>
public readonly record struct CallNesting(string? CalledBy, int? Level, bool RepeatsRpc = false)
{
    /// <summary>Called by for a call the client made: the rpc_completed row, and its own module_end.</summary>
    public const string Client = "(client)";

    /// <summary>A call another procedure made: its time is in its caller's too.</summary>
    public bool IsNested => Level > 1;

    /// <summary>
    /// The client's call, read once (Top-level only): not nested, and not the module_end that
    /// repeats an rpc_completed row.
    /// </summary>
    public bool IsTopLevel => !IsNested && !RepeatsRpc;
}

/// <summary>Per-procedure rollup — populated by both capture modes.</summary>
public record SpAggregateRow(
    string    DatabaseName,
    string    SchemaName,
    string    ProcedureName,
    long      ExecCount,        // executions observed in this window
    double    AvgDurationMs,
    double    MaxDurationMs,
    double    TotalDurationMs,
    double    AvgCpuMs,
    long      AvgLogicalReads,
    long      TotalLogicalReads,
    DateTime? LastExecutionTime)
{
    public string FullName => string.IsNullOrEmpty(SchemaName)
        ? ProcedureName
        : $"{SchemaName}.{ProcedureName}";
}

/// <summary>Current state of the trace, surfaced in the page toolbar.</summary>
public record TraceSessionStatus(
    bool      IsRunning,
    TraceMode Mode,
    string    SessionName,
    string?   Reason,           // why DMV fallback was chosen, when it was
    long      EventsCaptured,
    long      EventsDropped,
    bool      TargetTruncated)
{
    public static TraceSessionStatus Stopped(string sessionName) =>
        new(false, TraceMode.DmvFallback, sessionName, null, 0, 0, false);
}

// ── sys.dm_exec_procedure_stats snapshot plumbing ────────────────────

/// <summary>
/// Identity of a cached procedure plan. <c>CachedTime</c> is part of the key because a
/// recompile resets the cumulative counters — treating it as a new row avoids computing
/// a negative delta against the pre-recompile totals.
/// </summary>
public readonly record struct ProcStatsKey(int DatabaseId, int ObjectId, DateTime CachedTime);

/// <summary>One row of a sys.dm_exec_procedure_stats snapshot (cumulative totals).</summary>
public record ProcStatsSnapshot(
    ProcStatsKey Key,
    string       DatabaseName,
    string       SchemaName,
    string       ProcedureName,
    long         ExecutionCount,
    long         TotalWorkerTimeUs,
    long         TotalElapsedTimeUs,
    long         MaxElapsedTimeUs,
    long         TotalLogicalReads,
    long         TotalLogicalWrites,
    long         TotalPhysicalReads,
    DateTime?    LastExecutionTime);

// ── Ring-buffer target parse result ───────────────────────────────────

/// <summary>
/// One read of the XE ring_buffer target: the events it held plus the target's own
/// health counters. <c>DroppedCount</c> matters — the ring buffer discards silently
/// under burst, and the UI must say so rather than imply the server was quiet.
/// </summary>
public record SpTraceTargetSnapshot(
    IReadOnlyList<SpTraceEvent> Events,
    long                        TotalEventsProcessed,
    long                        DroppedCount,
    bool                        Truncated)
{
    public static SpTraceTargetSnapshot Empty { get; } = new([], 0, 0, false);
}
