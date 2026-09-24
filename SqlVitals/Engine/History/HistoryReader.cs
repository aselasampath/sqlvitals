using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.History;

/// <summary>
/// Reads past ranges from the monitoring history for the trend pages. Each call opens its own
/// short-lived connection, which WAL mode lets run alongside <see cref="HistoryWriter"/>, so any
/// thread can use it. It never creates, upgrades or repairs the file; that is the writer's job.
///
/// Long ranges come back averaged into buckets (see <see cref="HistoryRange.BucketFor"/>), each
/// stamped with the average capture time of what it holds. Times are UTC on this PC's clock.
/// </summary>
public sealed class HistoryReader(string path)
{
    public string Path { get; } = path;

    /// <summary>Live Metrics samples of one connection, averaged per bucket, oldest first.</summary>
    public IReadOnlyList<MetricHistoryPoint> ReadMetrics(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        using var conn = OpenOrNull(out _);
        if (conn is null)
            return [];

        // With exactly one MIN() in the query, SQLite takes the bare server_time from that same
        // row: the first sample's server clock, beside its capture time on this PC.
        var rows = conn.Query("""
            SELECT CAST(AVG(captured_utc) AS INTEGER) AS t, MIN(captured_utc) AS first_utc, server_time,
                   AVG(sql_cpu_pct) AS cpu,
                   AVG(wait_cpu_ms_per_sec) AS w_cpu, AVG(wait_io_ms_per_sec) AS w_io,
                   AVG(wait_lock_ms_per_sec) AS w_lock, AVG(wait_memory_ms_per_sec) AS w_mem,
                   AVG(wait_network_ms_per_sec) AS w_net, AVG(wait_other_ms_per_sec) AS w_other,
                   AVG(batch_requests_per_sec) AS batches, AVG(compilations_per_sec) AS compiles,
                   AVG(recompilations_per_sec) AS recompiles, AVG(transactions_per_sec) AS txns,
                   AVG(physical_reads_per_sec) AS reads, AVG(physical_writes_per_sec) AS writes,
                   AVG(page_life_expectancy_sec) AS ple, AVG(memory_grants_pending) AS grants,
                   AVG(buffer_cache_hit_ratio) AS bchr
            FROM metric_samples
            WHERE connection_id = @connectionId AND captured_utc >= @from AND captured_utc < @to
            GROUP BY captured_utc / @bucketMs
            ORDER BY 1
            """,
            Args(connectionId, fromUtc, toUtc, bucket));

        return rows.Select(r =>
        {
            var serverTime = ParseServerTime((string)r.server_time);
            return new MetricHistoryPoint(
                HistoryStore.FromEpochMs((long)r.t),
                serverTime - HistoryStore.FromEpochMs((long)r.first_utc),
                new LiveMetricSample(
                    serverTime,
                    (double)r.w_cpu, (double)r.w_io, (double)r.w_lock, (double)r.w_mem, (double)r.w_net, (double)r.w_other,
                    (double)r.cpu,
                    (double)r.batches, (double)r.compiles, (double)r.recompiles, (double)r.txns,
                    (double)r.reads, (double)r.writes,
                    (double)r.ple, (double)r.grants, (double)r.bchr));
        }).ToList();
    }

    /// <summary>
    /// Waits per type from the detail snapshots of one connection, summed per bucket, oldest
    /// first. A wait type missing from a bucket had no waits in it.
    /// </summary>
    public IReadOnlyList<WaitHistoryBucket> ReadWaits(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        using var conn = OpenOrNull(out _);
        if (conn is null)
            return [];

        var args = Args(connectionId, fromUtc, toUtc, bucket);

        var waits = conn.Query("""
            SELECT s.captured_utc / @bucketMs AS b, w.wait_type,
                   SUM(w.waiting_tasks) AS tasks, SUM(w.wait_time_ms) AS ms, SUM(w.signal_wait_time_ms) AS signal_ms
            FROM detail_snapshots s
            JOIN wait_deltas w ON w.snapshot_id = s.snapshot_id
            WHERE s.connection_id = @connectionId AND s.captured_utc >= @from AND s.captured_utc < @to
            GROUP BY b, w.wait_type
            """, args)
            .GroupBy(r => (long)r.b)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, WaitTypeTotals>)g.ToDictionary(
                    r => (string)r.wait_type,
                    r => new WaitTypeTotals((string)r.wait_type, (long)r.tasks, (long)r.ms, (long)r.signal_ms)));

        return conn.Query("""
            SELECT captured_utc / @bucketMs AS b, CAST(AVG(captured_utc) AS INTEGER) AS t, SUM(interval_sec) AS secs
            FROM detail_snapshots
            WHERE connection_id = @connectionId AND captured_utc >= @from AND captured_utc < @to
            GROUP BY b
            ORDER BY b
            """, args)
            .Select(r => new WaitHistoryBucket(
                HistoryStore.FromEpochMs((long)r.t),
                (double)r.secs,
                waits.TryGetValue((long)r.b, out var types) ? types : Empty<WaitTypeTotals>()))
            .ToList();
    }

    /// <summary>
    /// Perfmon counters from the detail snapshots of one connection, averaged per bucket, oldest
    /// first. A counter missing from a bucket was zero. Snapshots taken before SqlVitals recorded
    /// counters (before schema version 2) are left out rather than shown as zeros.
    /// </summary>
    public IReadOnlyList<CounterHistoryBucket> ReadCounters(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        using var conn = OpenOrNull(out var version);
        if (conn is null || version < 2)
            return [];

        var args = Args(connectionId, fromUtc, toUtc, bucket);

        // Only snapshots with at least one counter row count; see the summary.
        var buckets = conn.Query("""
            SELECT s.captured_utc / @bucketMs AS b, CAST(AVG(s.captured_utc) AS INTEGER) AS t, COUNT(*) AS snapshots
            FROM detail_snapshots s
            WHERE s.connection_id = @connectionId AND s.captured_utc >= @from AND s.captured_utc < @to
              AND EXISTS (SELECT 1 FROM counter_values c WHERE c.snapshot_id = s.snapshot_id)
            GROUP BY b
            ORDER BY b
            """, args).ToList();

        var sums = conn.Query("""
            SELECT s.captured_utc / @bucketMs AS b, c.counter_name, SUM(c.value) AS total
            FROM detail_snapshots s
            JOIN counter_values c ON c.snapshot_id = s.snapshot_id
            WHERE s.connection_id = @connectionId AND s.captured_utc >= @from AND s.captured_utc < @to
            GROUP BY b, c.counter_name
            """, args)
            .GroupBy(r => (long)r.b)
            .ToDictionary(g => g.Key, g => g.ToList());

        return buckets.Select(r =>
        {
            var snapshots = (long)r.snapshots;
            IReadOnlyDictionary<string, double> values = sums.TryGetValue((long)r.b, out var rows)
                ? rows.ToDictionary(x => (string)x.counter_name, x => (double)x.total / snapshots)
                : Empty<double>();
            return new CounterHistoryBucket(HistoryStore.FromEpochMs((long)r.t), values);
        }).ToList();
    }

    /// <summary>
    /// Work per query_hash over the range, summed from the detail snapshots of one connection.
    /// Only the top queries by CPU of each snapshot are recorded, so a query appears for the
    /// snapshots it was busy enough to make that list.
    /// </summary>
    public IReadOnlyList<HistoryQueryTotals> ReadQueryTotals(Guid connectionId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = OpenOrNull(out _);
        if (conn is null)
            return [];

        return conn.Query("""
            SELECT q.query_hash, SUM(q.executions) AS executions,
                   SUM(q.worker_time_us) AS worker_us, SUM(q.elapsed_time_us) AS elapsed_us
            FROM detail_snapshots s
            JOIN query_deltas q ON q.snapshot_id = s.snapshot_id
            WHERE s.connection_id = @connectionId AND s.captured_utc >= @from AND s.captured_utc < @to
            GROUP BY q.query_hash
            """,
            new
            {
                connectionId = connectionId.ToString("D"),
                from         = HistoryStore.ToEpochMs(fromUtc),
                to           = HistoryStore.ToEpochMs(toUtc),
            })
            .Select(r => new HistoryQueryTotals((string)r.query_hash, (long)r.executions, (long)r.worker_us, (long)r.elapsed_us))
            .ToList();
    }

    /// <summary>The recorded statement text and database of each hash that has one, by hash.</summary>
    public IReadOnlyDictionary<string, QueryTextInfo> ReadQueryTexts(Guid connectionId, IReadOnlyCollection<string> queryHashes)
    {
        if (queryHashes.Count == 0)
            return Empty<QueryTextInfo>();

        using var conn = OpenOrNull(out _);
        if (conn is null)
            return Empty<QueryTextInfo>();

        return conn.Query("""
            SELECT query_hash, database_name, query_text
            FROM query_texts
            WHERE connection_id = @connectionId AND query_hash IN @queryHashes
            """,
            new { connectionId = connectionId.ToString("D"), queryHashes })
            .ToDictionary(
                r => (string)r.query_hash,
                r => new QueryTextInfo((string)r.query_hash, (string?)r.database_name, (string)r.query_text));
    }

    /// <summary>
    /// Alerts active at any time in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), of one
    /// connection or, with null, of every connection, newest first. Active alerts are always
    /// included when they started before the end. Empty for a file from before alerts (schema 3).
    /// </summary>
    public IReadOnlyList<StoredAlert> ReadAlerts(Guid? connectionId, DateTime fromUtc, DateTime toUtc)
    {
        using var conn = OpenOrNull(out var version);
        if (conn is null || version < 3)
            return [];

        return conn.Query("""
            SELECT a.*, c.name AS connection_name
            FROM alerts a
            LEFT JOIN connections c ON c.connection_id = a.connection_id
            WHERE (@connectionId IS NULL OR a.connection_id = @connectionId)
              AND a.started_utc < @to
              AND (a.ended_utc IS NULL OR a.ended_utc >= @from)
            ORDER BY a.started_utc DESC
            """,
            new
            {
                connectionId = connectionId?.ToString("D"),
                from         = HistoryStore.ToEpochMs(fromUtc),
                to           = HistoryStore.ToEpochMs(toUtc),
            })
            .Select(r => new StoredAlert(
                new Alert(
                    Guid.Parse((string)r.alert_id),
                    Guid.Parse((string)r.connection_id),
                    Enum.TryParse<HealthIndicator>((string)r.indicator, out var indicator) ? indicator : HealthIndicator.Cpu,
                    Enum.TryParse<HealthLevel>((string)r.severity, out var severity) ? severity : HealthLevel.Warning,
                    HistoryStore.FromEpochMs((long)r.started_utc),
                    r.ended_utc is long ended ? HistoryStore.FromEpochMs(ended) : null,
                    (double)r.value,
                    (double?)r.threshold,
                    (string)r.detail,
                    r.end_reason is string reason && Enum.TryParse<AlertEndReason>(reason, out var why) ? why : null),
                (string?)r.connection_name ?? string.Empty))
            .ToList();
    }

    // Null when there is no history to read: no file yet, or one the writer hasn't set up.
    private SqliteConnection? OpenOrNull(out long version)
    {
        version = 0;
        if (!File.Exists(Path))
            return null;

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource     = Path,
            // Not ReadOnly: a reader of a WAL file may need to set up its shared-memory index.
            // query_only below still refuses any change.
            Mode           = SqliteOpenMode.ReadWrite,
            // A pooled connection would keep the file open, stopping the writer from moving a
            // damaged file aside.
            Pooling        = false,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            conn.Open();
            conn.Execute("PRAGMA busy_timeout = 5000; PRAGMA query_only = ON;");

            version = conn.ExecuteScalar<long>("PRAGMA user_version;");
            if (version > HistoryStore.SchemaVersion)
                throw new HistorySchemaTooNewException(Path, version);
            if (version > 0)
                return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }

        conn.Dispose();
        return null;
    }

    private static object Args(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => new
    {
        connectionId = connectionId.ToString("D"),
        from         = HistoryStore.ToEpochMs(fromUtc),
        to           = HistoryStore.ToEpochMs(toUtc),
        bucketMs     = Math.Max(1L, (long)bucket.TotalMilliseconds),
    };

    private static DateTime ParseServerTime(string text) =>
        DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, T> Empty<T>() => new Dictionary<string, T>();
}

/// <summary>
/// Live Metrics samples averaged over one bucket. <see cref="ServerClockOffset"/> is how far the
/// server's clock was from this PC's UTC at the first sample in the bucket, which is also the
/// sample <see cref="Averages"/>'s Time comes from.
/// </summary>
public sealed record MetricHistoryPoint(DateTime TimeUtc, TimeSpan ServerClockOffset, LiveMetricSample Averages)
{
    /// <summary>
    /// The bucket's time on the server's clock, as Live Metrics plots its live samples. The
    /// Kind is Unspecified, like every server time.
    /// </summary>
    public DateTime ServerTime => DateTime.SpecifyKind(TimeUtc + ServerClockOffset, DateTimeKind.Unspecified);
}

/// <summary>Waits per type over one bucket, and how many seconds of snapshots it covers.</summary>
public sealed record WaitHistoryBucket(DateTime TimeUtc, double Seconds, IReadOnlyDictionary<string, WaitTypeTotals> Waits);

/// <summary>Perfmon counters averaged over one bucket; a counter not listed was zero.</summary>
public sealed record CounterHistoryBucket(DateTime TimeUtc, IReadOnlyDictionary<string, double> Values);

/// <summary>An alert read from the history, with the name its connection was last saved under.</summary>
public sealed record StoredAlert(Alert Alert, string ConnectionName);

/// <summary>Work one query_hash did over a range of the history; times in microseconds.</summary>
public sealed record HistoryQueryTotals(string QueryHash, long Executions, long WorkerTimeUs, long ElapsedTimeUs);
