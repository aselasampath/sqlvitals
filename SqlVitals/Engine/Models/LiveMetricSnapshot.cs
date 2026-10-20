namespace SqlVitals.Engine.Models;

/// <summary>
/// A single point-in-time capture of the live performance counters
/// used by the Live Metrics Dashboard.
/// All per-second counters are instantaneous values from
/// sys.dm_os_performance_counters (PERF_COUNTER_BULK_COUNT type).
/// </summary>
public record LiveMetricSnapshot(
    DateTime CaptureTime,

    // ?? SQL Throughput ?????????????????????????????????????????????
    long BatchRequestsPerSec,
    long SQLCompilationsPerSec,
    long SQLRecompilationsPerSec,
    long TransactionsPerSec,

    // ?? Physical I/O ???????????????????????????????????????????????
    long PhysicalReadsPerSec,
    long PhysicalWritesPerSec,

    // ?? CPU ????????????????????????????????????????????????????????
    double SqlCpuUtilizationPct,    // from sys.dm_os_ring_buffers

    // ?? Memory ????????????????????????????????????????????????????
    long PageLifeExpectancy,
    long MemoryGrantsPending,
    double BufferCacheHitRatio,

    // ?? Waits (cumulative ms � caller diffs successive snapshots) ??
    long TotalWaitMs,
    long CpuWaitMs,
    long IoWaitMs,
    long LockWaitMs,
    long MemoryWaitMs,
    long NetworkWaitMs,
    long OtherWaitMs,

    // Health indicators (gauges; zero when the server doesn't report them)
    int     BlockedSessions   = 0,
    long    LongestBlockMs    = 0,
    double  LogUsedPct        = 0,     // the fullest transaction log on the server
    string? LogUsedDatabase   = null,
    double  TempDbUsedPct     = 0,     // TempDB data files, of their current size
    int     WaitingTasks      = 0,     // user tasks waiting on a resource now (#143)

    // What the health score judges them by (#173): the buffer pool for page life expectancy, and
    // the fullest log's size and why it can't be reused yet (sys.databases.log_reuse_wait_desc)
    long    BufferPoolPages   = 0,     // 8 KB data pages in the buffer pool
    double  LogSizeMb         = 0,
    string? LogReuseWait      = null,

    // Azure SQL Database only (#234), from sys.dm_db_resource_stats: data IO and log write as
    // shares of the service tier's limits, and the DTU limit (null on a vCore tier). Null on
    // other editions, or while the view has no row (after a failover or a scale).
    double? AzureDataIoPct    = null,
    double? AzureLogWritePct  = null,
    int?    AzureDtuLimit     = null,

    // The server is Azure SQL Database (EngineEdition 5), as its capabilities say: the figures
    // above apply to it even while they are null.
    bool    IsAzureSqlDatabase = false
);
