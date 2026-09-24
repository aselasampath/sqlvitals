using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.History;

/// <summary>
/// The local monitoring history: one SQLite file shared by every connection (and every running
/// copy of the app, which WAL mode allows). Not thread-safe: <see cref="HistoryWriter"/> owns it
/// on a single background thread. See the decision on issue #28 for why SQLite.
///
/// Times are stored as UTC epoch milliseconds taken on this PC. The server's own clock is kept
/// alongside as text, since it is local to the server and shifts at daylight saving.
/// </summary>
public sealed class HistoryStore(string path) : IDisposable
{
    public const int SchemaVersion = 1;

    private const long DayMs          = 24L * 60 * 60 * 1000;
    private const int  PurgeBatchSize = 500;

    private const string ServerTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string CreateSchemaSql = """
        CREATE TABLE IF NOT EXISTS connections (
            connection_id   TEXT    NOT NULL PRIMARY KEY,
            name            TEXT    NOT NULL,
            server          TEXT    NOT NULL,
            database_name   TEXT    NOT NULL,
            last_seen_utc   INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS metric_samples (
            connection_id             TEXT    NOT NULL,
            captured_utc              INTEGER NOT NULL,
            server_time               TEXT    NOT NULL,
            sql_cpu_pct               REAL    NOT NULL,
            wait_cpu_ms_per_sec       REAL    NOT NULL,
            wait_io_ms_per_sec        REAL    NOT NULL,
            wait_lock_ms_per_sec      REAL    NOT NULL,
            wait_memory_ms_per_sec    REAL    NOT NULL,
            wait_network_ms_per_sec   REAL    NOT NULL,
            wait_other_ms_per_sec     REAL    NOT NULL,
            batch_requests_per_sec    REAL    NOT NULL,
            compilations_per_sec      REAL    NOT NULL,
            recompilations_per_sec    REAL    NOT NULL,
            transactions_per_sec      REAL    NOT NULL,
            physical_reads_per_sec    REAL    NOT NULL,
            physical_writes_per_sec   REAL    NOT NULL,
            page_life_expectancy_sec  REAL    NOT NULL,
            memory_grants_pending     REAL    NOT NULL,
            buffer_cache_hit_ratio    REAL    NOT NULL,
            PRIMARY KEY (connection_id, captured_utc)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS detail_snapshots (
            snapshot_id                 INTEGER NOT NULL PRIMARY KEY,
            connection_id               TEXT    NOT NULL,
            captured_utc                INTEGER NOT NULL,
            server_time                 TEXT    NOT NULL,
            interval_sec                REAL    NOT NULL,
            total_server_memory_kb      INTEGER,
            target_server_memory_kb     INTEGER,
            database_cache_memory_kb    INTEGER,
            sql_cache_memory_kb         INTEGER,
            granted_workspace_memory_kb INTEGER,
            free_memory_kb              INTEGER,
            memory_grants_outstanding   INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_detail_snapshots_connection_time
            ON detail_snapshots (connection_id, captured_utc);

        CREATE TABLE IF NOT EXISTS wait_deltas (
            snapshot_id         INTEGER NOT NULL,
            wait_type           TEXT    NOT NULL,
            waiting_tasks       INTEGER NOT NULL,
            wait_time_ms        INTEGER NOT NULL,
            signal_wait_time_ms INTEGER NOT NULL,
            PRIMARY KEY (snapshot_id, wait_type)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS file_io_deltas (
            snapshot_id     INTEGER NOT NULL,
            database_name   TEXT    NOT NULL,
            file_id         INTEGER NOT NULL,
            file_name       TEXT    NOT NULL,
            file_type       TEXT    NOT NULL,
            reads           INTEGER NOT NULL,
            bytes_read      INTEGER NOT NULL,
            read_stall_ms   INTEGER NOT NULL,
            writes          INTEGER NOT NULL,
            bytes_written   INTEGER NOT NULL,
            write_stall_ms  INTEGER NOT NULL,
            PRIMARY KEY (snapshot_id, database_name, file_id)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS query_deltas (
            snapshot_id     INTEGER NOT NULL,
            query_hash      TEXT    NOT NULL,
            executions      INTEGER NOT NULL,
            worker_time_us  INTEGER NOT NULL,
            elapsed_time_us INTEGER NOT NULL,
            logical_reads   INTEGER NOT NULL,
            logical_writes  INTEGER NOT NULL,
            PRIMARY KEY (snapshot_id, query_hash)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS query_texts (
            connection_id   TEXT    NOT NULL,
            query_hash      TEXT    NOT NULL,
            database_name   TEXT,
            query_text      TEXT    NOT NULL,
            last_seen_utc   INTEGER NOT NULL,
            PRIMARY KEY (connection_id, query_hash)
        ) WITHOUT ROWID;
        """;

    private SqliteConnection? _conn;

    public string Path { get; } = path;

    /// <summary>
    /// Opens the file, creating it and its folder when missing. A corrupt file is renamed (see
    /// <see cref="Quarantine"/>) and a new one started. Throws <see cref="HistorySchemaTooNewException"/>
    /// for a file written by a newer SqlVitals, which is left untouched.
    /// </summary>
    public void Open()
    {
        if (_conn is not null) return;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        try
        {
            OpenAndMigrate();
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            CloseConnection();
            Quarantine(Path);
            OpenAndMigrate();
        }
    }

    private void OpenAndMigrate()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource     = Path,
            Mode           = SqliteOpenMode.ReadWriteCreate,
            // A pooled connection keeps the file open after Dispose, which would stop a corrupt
            // file from being renamed. There is only ever one connection per store anyway.
            Pooling        = false,
            DefaultTimeout = 5,
        }.ToString();

        var conn = new SqliteConnection(connectionString);
        try
        {
            conn.Open();
            conn.Execute("PRAGMA busy_timeout = 5000;");

            var version = conn.ExecuteScalar<long>("PRAGMA user_version;");
            if (version > SchemaVersion)
                throw new HistorySchemaTooNewException(Path, version);

            if (version == 0)
                // Only takes effect before the first table is created; lets Purge shrink the file.
                conn.Execute("PRAGMA auto_vacuum = INCREMENTAL;");

            conn.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
            conn.Execute("PRAGMA synchronous = NORMAL;");

            if (version < SchemaVersion)
            {
                using var tx = conn.BeginTransaction();
                conn.Execute(CreateSchemaSql, transaction: tx);
                conn.Execute($"PRAGMA user_version = {SchemaVersion};", transaction: tx);
                tx.Commit();
            }
        }
        catch
        {
            conn.Dispose();
            throw;
        }

        _conn = conn;
    }

    /// <summary>Stores the records in one transaction: all of them or, on failure, none.</summary>
    public void Write(IReadOnlyCollection<HistoryRecord> records)
    {
        var conn = Connection;
        using var tx = conn.BeginTransaction();

        foreach (var record in records)
        {
            var connectionId = record.Connection.Id.ToString("D");
            var capturedMs   = ToEpochMs(record.CapturedUtc);

            conn.Execute("""
                INSERT INTO connections (connection_id, name, server, database_name, last_seen_utc)
                VALUES (@connectionId, @Name, @Server, @Database, @capturedMs)
                ON CONFLICT (connection_id) DO UPDATE SET
                    name = excluded.name, server = excluded.server, database_name = excluded.database_name,
                    last_seen_utc = MAX(last_seen_utc, excluded.last_seen_utc)
                """,
                new { connectionId, record.Connection.Name, record.Connection.Server, record.Connection.Database, capturedMs },
                tx);

            switch (record)
            {
                case MetricSampleRecord m:
                    WriteSample(conn, tx, connectionId, capturedMs, m.Sample);
                    break;
                case DetailRecord d:
                    WriteDetail(conn, tx, connectionId, capturedMs, d);
                    break;
            }
        }

        tx.Commit();
    }

    private static void WriteSample(SqliteConnection conn, SqliteTransaction tx, string connectionId, long capturedMs,
                                    LiveMetricSample s)
    {
        // OR IGNORE: two samples in the same millisecond for one connection are the same sample.
        conn.Execute("""
            INSERT OR IGNORE INTO metric_samples (
                connection_id, captured_utc, server_time, sql_cpu_pct,
                wait_cpu_ms_per_sec, wait_io_ms_per_sec, wait_lock_ms_per_sec,
                wait_memory_ms_per_sec, wait_network_ms_per_sec, wait_other_ms_per_sec,
                batch_requests_per_sec, compilations_per_sec, recompilations_per_sec, transactions_per_sec,
                physical_reads_per_sec, physical_writes_per_sec,
                page_life_expectancy_sec, memory_grants_pending, buffer_cache_hit_ratio)
            VALUES (
                @connectionId, @capturedMs, @serverTime, @SqlCpuPct,
                @WaitCpuMsPerSec, @WaitIoMsPerSec, @WaitLockMsPerSec,
                @WaitMemoryMsPerSec, @WaitNetworkMsPerSec, @WaitOtherMsPerSec,
                @BatchRequestsPerSec, @CompilationsPerSec, @RecompilationsPerSec, @TransactionsPerSec,
                @PhysicalReadsPerSec, @PhysicalWritesPerSec,
                @PageLifeExpectancySec, @MemoryGrantsPending, @BufferCacheHitRatio)
            """,
            new
            {
                connectionId, capturedMs, serverTime = FormatServerTime(s.Time),
                s.SqlCpuPct,
                s.WaitCpuMsPerSec, s.WaitIoMsPerSec, s.WaitLockMsPerSec,
                s.WaitMemoryMsPerSec, s.WaitNetworkMsPerSec, s.WaitOtherMsPerSec,
                s.BatchRequestsPerSec, s.CompilationsPerSec, s.RecompilationsPerSec, s.TransactionsPerSec,
                s.PhysicalReadsPerSec, s.PhysicalWritesPerSec,
                s.PageLifeExpectancySec, s.MemoryGrantsPending, s.BufferCacheHitRatio,
            },
            tx);
    }

    private static void WriteDetail(SqliteConnection conn, SqliteTransaction tx, string connectionId, long capturedMs,
                                    DetailRecord record)
    {
        var d = record.Detail;
        var m = d.Memory;

        var snapshotId = conn.ExecuteScalar<long>("""
            INSERT INTO detail_snapshots (
                connection_id, captured_utc, server_time, interval_sec,
                total_server_memory_kb, target_server_memory_kb, database_cache_memory_kb,
                sql_cache_memory_kb, granted_workspace_memory_kb, free_memory_kb, memory_grants_outstanding)
            VALUES (
                @connectionId, @capturedMs, @serverTime, @IntervalSeconds,
                @total, @target, @dbCache, @sqlCache, @workspace, @free, @outstanding)
            RETURNING snapshot_id
            """,
            new
            {
                connectionId, capturedMs, serverTime = FormatServerTime(d.ServerTime), d.IntervalSeconds,
                total = m?.TotalServerMemoryKB, target = m?.TargetServerMemoryKB, dbCache = m?.DatabaseCacheMemoryKB,
                sqlCache = m?.SqlCacheMemoryKB, workspace = m?.GrantedWorkspaceMemoryKB, free = m?.FreeMemoryKB,
                outstanding = m?.MemoryGrantsOutstanding,
            },
            tx);

        conn.Execute("""
            INSERT INTO wait_deltas (snapshot_id, wait_type, waiting_tasks, wait_time_ms, signal_wait_time_ms)
            VALUES (@snapshotId, @WaitType, @WaitingTasks, @WaitTimeMs, @SignalWaitTimeMs)
            """,
            d.Waits.Select(w => new { snapshotId, w.WaitType, w.WaitingTasks, w.WaitTimeMs, w.SignalWaitTimeMs }),
            tx);

        conn.Execute("""
            INSERT INTO file_io_deltas (snapshot_id, database_name, file_id, file_name, file_type,
                                        reads, bytes_read, read_stall_ms, writes, bytes_written, write_stall_ms)
            VALUES (@snapshotId, @DatabaseName, @FileId, @FileName, @FileType,
                    @Reads, @BytesRead, @ReadStallMs, @Writes, @BytesWritten, @WriteStallMs)
            """,
            d.Files.Select(f => new
            {
                snapshotId, f.DatabaseName, f.FileId, f.FileName, f.FileType,
                f.Reads, f.BytesRead, f.ReadStallMs, f.Writes, f.BytesWritten, f.WriteStallMs,
            }),
            tx);

        conn.Execute("""
            INSERT INTO query_deltas (snapshot_id, query_hash, executions, worker_time_us, elapsed_time_us,
                                      logical_reads, logical_writes)
            VALUES (@snapshotId, @QueryHash, @Executions, @WorkerTimeUs, @ElapsedTimeUs, @LogicalReads, @LogicalWrites)
            """,
            d.TopQueries.Select(q => new
            {
                snapshotId, q.QueryHash, q.Executions, q.WorkerTimeUs, q.ElapsedTimeUs, q.LogicalReads, q.LogicalWrites,
            }),
            tx);

        conn.Execute("""
            INSERT INTO query_texts (connection_id, query_hash, database_name, query_text, last_seen_utc)
            VALUES (@connectionId, @QueryHash, @DatabaseName, @QueryText, @capturedMs)
            ON CONFLICT (connection_id, query_hash) DO UPDATE SET
                database_name = excluded.database_name, query_text = excluded.query_text,
                last_seen_utc = MAX(last_seen_utc, excluded.last_seen_utc)
            """,
            record.QueryTexts.Select(t => new { connectionId, t.QueryHash, t.DatabaseName, t.QueryText, capturedMs }),
            tx);

        // Text is fetched once per query per session; keep it as long as the query keeps showing up.
        conn.Execute("""
            UPDATE query_texts SET last_seen_utc = MAX(last_seen_utc, @capturedMs)
            WHERE connection_id = @connectionId AND query_hash = @QueryHash
            """,
            d.TopQueries.Select(q => new { connectionId, q.QueryHash, capturedMs }),
            tx);
    }

    /// <summary>
    /// Deletes everything captured before <paramref name="cutoffUtc"/>, a day or a few hundred
    /// snapshots per transaction so other writers are never held up for long, then gives the
    /// freed space back to the file system.
    /// </summary>
    public void Purge(DateTime cutoffUtc)
    {
        var conn   = Connection;
        var cutoff = ToEpochMs(cutoffUtc);

        foreach (var connectionId in conn.Query<string>("SELECT connection_id FROM connections;").ToList())
        {
            while (true)
            {
                var oldest = conn.ExecuteScalar<long?>(
                    "SELECT MIN(captured_utc) FROM metric_samples WHERE connection_id = @connectionId;",
                    new { connectionId });
                if (oldest is null || oldest >= cutoff)
                    break;

                conn.Execute(
                    "DELETE FROM metric_samples WHERE connection_id = @connectionId AND captured_utc < @until;",
                    new { connectionId, until = Math.Min(oldest.Value + DayMs, cutoff) });
            }
        }

        while (true)
        {
            var ids = conn.Query<long>(
                "SELECT snapshot_id FROM detail_snapshots WHERE captured_utc < @cutoff ORDER BY snapshot_id LIMIT @PurgeBatchSize;",
                new { cutoff, PurgeBatchSize }).ToList();
            if (ids.Count == 0)
                break;

            using var tx = conn.BeginTransaction();
            conn.Execute("DELETE FROM wait_deltas      WHERE snapshot_id IN @ids;", new { ids }, tx);
            conn.Execute("DELETE FROM file_io_deltas   WHERE snapshot_id IN @ids;", new { ids }, tx);
            conn.Execute("DELETE FROM query_deltas     WHERE snapshot_id IN @ids;", new { ids }, tx);
            conn.Execute("DELETE FROM detail_snapshots WHERE snapshot_id IN @ids;", new { ids }, tx);
            tx.Commit();
        }

        conn.Execute("DELETE FROM query_texts WHERE last_seen_utc < @cutoff;", new { cutoff });

        Compact(conn);
    }

    /// <summary>Deletes all history, for every connection, and shrinks the file.</summary>
    public void Clear()
    {
        var conn = Connection;

        // A DELETE without WHERE empties a table in one step, however big it has grown.
        using (var tx = conn.BeginTransaction())
        {
            conn.Execute("""
                DELETE FROM wait_deltas;
                DELETE FROM file_io_deltas;
                DELETE FROM query_deltas;
                DELETE FROM detail_snapshots;
                DELETE FROM query_texts;
                DELETE FROM metric_samples;
                DELETE FROM connections;
                """, transaction: tx);
            tx.Commit();
        }

        Compact(conn);
    }

    // Gives freed pages back to the file system and empties the WAL file.
    private static void Compact(SqliteConnection conn)
    {
        conn.Execute("PRAGMA incremental_vacuum;");
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>
    /// Bytes the history takes on disk: the file plus its WAL files. Zero when there is none yet.
    /// Reads file sizes only, so it is safe to call while the writer is busy.
    /// </summary>
    public static long SizeOnDisk(string path)
    {
        long total = 0;
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                    total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted or replaced between the check and the read; it no longer counts.
            }
        }
        return total;
    }

    /// <summary>Samples for one connection, oldest first; for tests and a future History page.</summary>
    public IReadOnlyList<StoredMetricSample> ReadSamples(Guid connectionId, DateTime fromUtc, DateTime toUtc)
    {
        var rows = Connection.Query("""
            SELECT * FROM metric_samples
            WHERE connection_id = @connectionId AND captured_utc >= @from AND captured_utc < @to
            ORDER BY captured_utc
            """,
            new { connectionId = connectionId.ToString("D"), from = ToEpochMs(fromUtc), to = ToEpochMs(toUtc) });

        return rows.Select(r => new StoredMetricSample(
            FromEpochMs((long)r.captured_utc),
            new LiveMetricSample(
                DateTime.ParseExact((string)r.server_time, ServerTimeFormat, CultureInfo.InvariantCulture),
                (double)r.wait_cpu_ms_per_sec, (double)r.wait_io_ms_per_sec, (double)r.wait_lock_ms_per_sec,
                (double)r.wait_memory_ms_per_sec, (double)r.wait_network_ms_per_sec, (double)r.wait_other_ms_per_sec,
                (double)r.sql_cpu_pct,
                (double)r.batch_requests_per_sec, (double)r.compilations_per_sec,
                (double)r.recompilations_per_sec, (double)r.transactions_per_sec,
                (double)r.physical_reads_per_sec, (double)r.physical_writes_per_sec,
                (double)r.page_life_expectancy_sec, (double)r.memory_grants_pending,
                (double)r.buffer_cache_hit_ratio))).ToList();
    }

    /// <summary>
    /// Renames a damaged history file (and its WAL files) to history.corrupt-&lt;timestamp&gt;.db
    /// beside it, so a new one can be started without losing the old data for good.
    /// </summary>
    public static string Quarantine(string path)
    {
        var stamp  = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
            $"{System.IO.Path.GetFileNameWithoutExtension(path)}.corrupt-{stamp}{System.IO.Path.GetExtension(path)}");

        File.Move(path, target);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(path + suffix))
                File.Move(path + suffix, target + suffix);

        return target;
    }

    /// <summary>SQLITE_CORRUPT or SQLITE_NOTADB: the file can't be used as it is.</summary>
    public static bool IsCorruption(SqliteException ex) => (ex.SqliteErrorCode & 0xFF) is 11 or 26;

    internal static long ToEpochMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    internal static DateTime FromEpochMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static string FormatServerTime(DateTime time) => time.ToString(ServerTimeFormat, CultureInfo.InvariantCulture);

    private SqliteConnection Connection =>
        _conn ?? throw new InvalidOperationException("The history store is not open.");

    private void CloseConnection()
    {
        _conn?.Dispose();
        _conn = null;
    }

    public void Dispose() => CloseConnection();
}

/// <summary>A stored sample with the UTC time it was captured on this PC.</summary>
public sealed record StoredMetricSample(DateTime CapturedUtc, LiveMetricSample Sample);

/// <summary>The history file was written by a newer SqlVitals; this version leaves it alone.</summary>
public sealed class HistorySchemaTooNewException(string path, long version)
    : Exception($"{path} uses history schema version {version}; this SqlVitals understands up to {HistoryStore.SchemaVersion}. " +
                "History is off until SqlVitals is updated.")
{
    public long Version { get; } = version;
}
