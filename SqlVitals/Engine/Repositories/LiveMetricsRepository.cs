using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Repositories;

/// <summary>Live metrics: the dashboard's counters and ring buffers, and the Perfmon counters. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class LiveMetricsRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), ILiveMetricsRepository
{
    // ── 21. Live metrics dashboard (perf counters + ring buffers) ────

    // SQL Server's CPU %: the scheduler monitor's latest record. Azure SQL Database has no such
    // records (the query returns nothing, which read as 0 %), so there it is the database's CPU
    // as a share of its service tier's limit: the latest 15-second row of sys.dm_db_resource_stats,
    // the same figure as the portal's "CPU percentage". That view doesn't exist elsewhere.
    internal static readonly string RingBufferCpuSql = SqlScripts.Get("LiveMetrics/RingBufferCpu");

    // rs is AzureResourceApplySql's: LiveMetricsSql puts both in, or neither.
    internal const string AzureSqlDbCpuSql = "rs.avg_cpu_percent";

    // The same row's data IO and log write, as shares of the tier's limits, and its DTU limit,
    // which is NULL on a vCore tier (#234): the portal's "Data IO percentage" and "Log IO
    // percentage". Elsewhere they are NULL, and the view isn't named.
    internal const string AzureResourceColumnsSql =
        "rs.avg_data_io_percent AS AzureDataIoPct, rs.avg_log_write_percent AS AzureLogWritePct, rs.dtu_limit AS AzureDtuLimit,";

    internal const string NoAzureResourceColumnsSql =
        "CAST(NULL AS DECIMAL(5,2)) AS AzureDataIoPct, CAST(NULL AS DECIMAL(5,2)) AS AzureLogWritePct, CAST(NULL AS INT) AS AzureDtuLimit,";

    // One row for all of them, so CPU, data IO and log write are from the same 15 seconds.
    internal const string AzureResourceApplySql =
        "OUTER APPLY (SELECT TOP (1) avg_cpu_percent, avg_data_io_percent, avg_log_write_percent, dtu_limit " +
        "FROM sys.dm_db_resource_stats ORDER BY end_time DESC) AS rs";

    /// <summary>The live metrics query, with the CPU figure read where this edition keeps it, and Azure's resource figures on Azure SQL Database.</summary>
    internal static string LiveMetricsSql(bool isAzureSqlDb) =>
        LiveMetricsSqlTemplate
            .Replace("/*CPU*/",           isAzureSqlDb ? AzureSqlDbCpuSql : RingBufferCpuSql)
            .Replace("/*AZURE_COLUMNS*/", isAzureSqlDb ? AzureResourceColumnsSql : NoAzureResourceColumnsSql)
            .Replace("/*AZURE_APPLY*/",   isAzureSqlDb ? AzureResourceApplySql : string.Empty);

    public async Task<LiveMetricSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken = default)
    {
        var isAzureSqlDb = (await CapabilitiesAsync(cancellationToken)).IsAzureSqlDatabase;

        using var conn = CreateConnection();
        var r = await QFirst(conn, LiveMetricsSql(isAzureSqlDb), cancellationToken,
                             new { SmallLogKb = (long)(HealthRules.SmallLogMb * 1024) })
                ?? throw new InvalidOperationException("Failed to capture metrics");
        return ToLiveMetricSnapshot(r, isAzureSqlDb);
    }

    private static readonly string LiveMetricsSqlTemplate = SqlScripts.Get("LiveMetrics/Dashboard");

    private static LiveMetricSnapshot ToLiveMetricSnapshot(dynamic r, bool isAzureSqlDb)
    {
        long total = (long)(r.TotalWaitMs ?? 0);
        long cpu = (long)(r.CpuWaitMs ?? 0);
        long io = (long)(r.IoWaitMs ?? 0);
        long lockWait = (long)(r.LockWaitMs ?? 0);
        long mem = (long)(r.MemoryWaitMs ?? 0);
        long net = (long)(r.NetworkWaitMs ?? 0);
        long other = total - (cpu + io + lockWait + mem + net);

        return new LiveMetricSnapshot(
            (DateTime)r.CaptureTime,
            (long)(r.BatchRequestsPerSec ?? 0),
            (long)(r.SQLCompilationsPerSec ?? 0),
            (long)(r.SQLRecompilationsPerSec ?? 0),
            (long)(r.TransactionsPerSec ?? 0),
            (long)(r.PhysicalReadsPerSec ?? 0),
            (long)(r.PhysicalWritesPerSec ?? 0),
            Convert.ToDouble(r.SqlCpuUtilizationPct ?? 0),
            (long)(r.PageLifeExpectancy ?? 0),
            (long)(r.MemoryGrantsPending ?? 0),
            Convert.ToDouble(r.BufferCacheHitRatio ?? 0),
            total, cpu, io, lockWait, mem, net, other,
            BlockedSessions: Convert.ToInt32(r.BlockedSessions ?? 0),
            LongestBlockMs: Convert.ToInt64(r.LongestBlockMs ?? 0),
            LogUsedPct: Convert.ToDouble(r.LogUsedPct ?? 0),
            LogUsedDatabase: (string?)r.LogUsedDatabase,
            TempDbUsedPct: Math.Clamp(Convert.ToDouble(r.TempDbUsedPct ?? 0), 0, 100),
            WaitingTasks: Convert.ToInt32(r.WaitingTasks ?? 0),
            BufferPoolPages: Convert.ToInt64(r.BufferPoolPages ?? 0),
            LogSizeMb: Convert.ToDouble(r.LogSizeMb ?? 0),
            LogReuseWait: (string?)r.LogReuseWait,
            AzureDataIoPct: r.AzureDataIoPct is null ? null : Convert.ToDouble(r.AzureDataIoPct),
            AzureLogWritePct: r.AzureLogWritePct is null ? null : Convert.ToDouble(r.AzureLogWritePct),
            AzureDtuLimit: r.AzureDtuLimit is null ? null : Convert.ToInt32(r.AzureDtuLimit),
            IsAzureSqlDatabase: isAzureSqlDb
        );
    }

    // ── 22. Perfmon counters (live DMV snapshot) ─────────────────────
    /// <summary>cntr_type of a cumulative counter, which is diffed into a per-second rate.</summary>
    public const int PerfmonRateCounterType = 272696576;

    /// <summary>
    /// The WHERE and ORDER BY picking the Perfmon page's counters from sys.dm_os_performance_counters.
    /// The monitoring history records the same ones (see HistoryRepository). A name can come back
    /// once per instance; both take the first row, so the order is fixed down to the instance.
    /// </summary>
    internal static readonly string PerfmonCounterFilter = SqlScripts.Get("LiveMetrics/PerfmonCounterFilter");

    public async Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync(CancellationToken cancellationToken = default)
    {
        var sql = SqlScripts.Get("LiveMetrics/PerfmonCounters").Replace("/*COUNTER_FILTER*/", PerfmonCounterFilter);

        using var conn = CreateConnection();
        return (await Q(conn, sql, cancellationToken)).Select(r =>
            new PerfmonCounter(
                (string)r.ObjectName,
                (string)r.CounterName,
                (string)r.InstanceName,
                (long)r.CntrValue,
                (int)r.CntrType,
                (DateTime)r.CaptureTime));
    }
}
