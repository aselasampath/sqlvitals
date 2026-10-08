using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Live metrics: the dashboard's counters and ring buffers, and the Perfmon counters. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class LiveMetricsRepository(IConfiguration configuration) : BaseRepository(configuration), ILiveMetricsRepository
{
    // ── 21. Live metrics dashboard (perf counters + ring buffers) ────

    // SQL Server's CPU %: the scheduler monitor's latest record. Azure SQL Database has no such
    // records (the query returns nothing, which read as 0 %), so there it is the database's CPU
    // as a share of its service tier's limit: the latest 15-second row of sys.dm_db_resource_stats,
    // the same figure as the portal's "CPU percentage". That view doesn't exist elsewhere.
    internal const string RingBufferCpuSql = """
        (SELECT TOP 1
            x.xml_record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int')
         FROM (
            SELECT timestamp, CONVERT(XML, record) AS xml_record
            FROM sys.dm_os_ring_buffers
            WHERE ring_buffer_type = 'RING_BUFFER_SCHEDULER_MONITOR'
           AND record LIKE '%<SystemHealth>%'
         ) AS x
         ORDER BY timestamp DESC)
        """;

    internal const string AzureSqlDbCpuSql =
        "(SELECT TOP (1) avg_cpu_percent FROM sys.dm_db_resource_stats ORDER BY end_time DESC)";

    /// <summary>The live metrics query, with the CPU figure read where this edition keeps it.</summary>
    internal static string LiveMetricsSql(bool isAzureSqlDb) =>
        LiveMetricsSqlTemplate.Replace("/*CPU*/", isAzureSqlDb ? AzureSqlDbCpuSql : RingBufferCpuSql);

    // Read on the first live metrics query; one repository per connection.
    private bool? _liveIsAzureSqlDb;

    public async Task<LiveMetricSnapshot> GetLiveMetricsAsync()
    {
        using var conn = CreateConnection();
        if (_liveIsAzureSqlDb is null)
        {
            var edition = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
            _liveIsAzureSqlDb = edition is not null && Convert.ToInt32(edition.Edition) == 5;
        }

        var r = await QFirst(conn, LiveMetricsSql(_liveIsAzureSqlDb.Value)) ?? throw new InvalidOperationException("Failed to capture metrics");
        return ToLiveMetricSnapshot(r);
    }

    private const string LiveMetricsSqlTemplate = """
            SELECT
                -- Throughput
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Batch Requests/sec') AS BatchRequestsPerSec,
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='SQL Compilations/sec') AS SQLCompilationsPerSec,
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='SQL Re-Compilations/sec') AS SQLRecompilationsPerSec,
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Transactions/sec' AND instance_name='_Total') AS TransactionsPerSec,
                -- Physical I/O
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Page reads/sec' AND object_name LIKE '%Buffer Manager%') AS PhysicalReadsPerSec,
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Page writes/sec' AND object_name LIKE '%Buffer Manager%') AS PhysicalWritesPerSec,
                -- Memory
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Page life expectancy' AND object_name LIKE '%Buffer Manager%') AS PageLifeExpectancy,
                (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Memory Grants Pending' AND object_name LIKE '%Memory Manager%') AS MemoryGrantsPending,
                ISNULL((SELECT CAST(A.cntr_value * 100.0 / NULLIF(B.cntr_value,0) AS DECIMAL(10,2))
                        FROM sys.dm_os_performance_counters A
                        JOIN sys.dm_os_performance_counters B ON A.object_name = B.object_name
                        WHERE A.counter_name='Buffer cache hit ratio' AND B.counter_name='Buffer cache hit ratio base'
                        AND A.object_name LIKE '%Buffer Manager%'),0) AS BufferCacheHitRatio,
                -- CPU: see RingBufferCpuSql / AzureSqlDbCpuSql
                /*CPU*/ AS SqlCpuUtilizationPct,
                -- Health indicators: blocking, the fullest log, TempDB data space
                (SELECT COUNT(*) FROM sys.dm_exec_requests
                 WHERE blocking_session_id > 0 AND blocking_session_id <> session_id) AS BlockedSessions,
                (SELECT MAX(wait_time) FROM sys.dm_exec_requests
                 WHERE blocking_session_id > 0 AND blocking_session_id <> session_id) AS LongestBlockMs,
                (SELECT TOP (1) cntr_value FROM sys.dm_os_performance_counters
                 WHERE counter_name='Percent Log Used' AND object_name LIKE '%:Databases%'
                   AND instance_name NOT IN ('_Total','mssqlsystemresource')
                 ORDER BY cntr_value DESC, instance_name) AS LogUsedPct,
                (SELECT TOP (1) RTRIM(instance_name) FROM sys.dm_os_performance_counters
                 WHERE counter_name='Percent Log Used' AND object_name LIKE '%:Databases%'
                   AND instance_name NOT IN ('_Total','mssqlsystemresource')
                 ORDER BY cntr_value DESC, instance_name) AS LogUsedDatabase,
                (SELECT TOP (1) CAST(100.0 - f.cntr_value * 100.0 / NULLIF(s.cntr_value,0) AS DECIMAL(10,2))
                 FROM sys.dm_os_performance_counters f
                 CROSS JOIN sys.dm_os_performance_counters s
                 WHERE f.counter_name='Free Space in tempdb (KB)' AND f.object_name LIKE '%:Transactions%'
                   AND s.counter_name='Data File(s) Size (KB)' AND s.object_name LIKE '%:Databases%'
                   AND s.instance_name='tempdb') AS TempDbUsedPct,
                -- User tasks waiting on anything right now (#143): the Overview's "Waiting tasks"
                (SELECT COUNT(*) FROM sys.dm_os_waiting_tasks wt
                 JOIN sys.dm_exec_sessions es ON es.session_id = wt.session_id
                 WHERE es.is_user_process = 1) AS WaitingTasks,
                -- Cumulative specific waits (caller will diff these)
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('SOS_SCHEDULER_YIELD','THREADPOOL','CMEMTHREAD','CMEMPARTITIONED','EE_PMOLOCK','EXCHANGE','CXPACKET','CXCONSUMER','CXSYNC_PORT','CXSYNC_CONSUMER')) AS CpuWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('ASYNC_IO_COMPLETION','IO_COMPLETION','PAGEIOLATCH_SH','PAGEIOLATCH_EX','PAGEIOLATCH_UP','PAGEIOLATCH_NL','PAGEIOLATCH_DT','PAGEIOLATCH_KP','WRITELOG','WRITE_COMPLETION','IO_QUEUE_LIMIT','IO_RETRY','BACKUPIO','BACKUPBUFFER','BACKUPTHREAD','LOGMGR','LOGMGR_QUEUE','LOGMGR_FLUSH','LOGMGR_RESERVE_APPEND','LOG_RATE_GOVERNOR')) AS IoWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'LCK%') AS LockWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('RESOURCE_SEMAPHORE','RESOURCE_SEMAPHORE_QUERY_COMPILE','RESOURCE_SEMAPHORE_MUTEX','PAGELATCH_SH','PAGELATCH_EX','PAGELATCH_UP','PAGELATCH_NL','PAGELATCH_DT','PAGELATCH_KP','LATCH_SH','LATCH_EX','LATCH_NL','LATCH_DT','LATCH_KP','LATCH_UP','MEMORY_ALLOCATION_EXT','MEMORY_GRANT_UPDATE','SOS_MEMORY_USAGE_ADJUSTMENT','SOS_VIRTUALMEMORY_LOW')) AS MemoryWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('ASYNC_NETWORK_IO','NET_WAITFOR_PACKET','NETWORK_IO','OLEDB')) AS NetworkWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type NOT IN ('SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP','DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE','DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','DBMIRROR_EVENTS_QUEUED','DBMIRROR_WORKER_QUEUE','BROKER_EVENTHANDLER','BROKER_RECEIVE_WAITFOR','BROKER_TRANSMITTER','CLR_AUTO_EVENT','CLR_MANUAL_EVENT','CLR_SYNCHRONIZE','FT_IFTS_SCHEDULER_IDLE_WAIT','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','DEADLOCK_ENUM_MUTEX','WAIT_XTP_OFFLINE_CKPT_NEW_LOG','WAIT_XTP_RECOVERY','WAIT_XTP_HOST_WAIT','WAIT_XTP_CKPT_CLOSE','PARALLEL_REDO_DRAIN_WORKER','PARALLEL_REDO_LOG_CACHE','PARALLEL_REDO_TRAN_LIST','PARALLEL_REDO_WORKER_SYNC','PARALLEL_REDO_WORKER_WAIT_WORK','XE_DISPATCHER_JOIN','XE_DISPATCH_QUEUE_SEMAPHORE','SQLTRACE_WAIT_ENTRIES')) AS TotalWaitMs,
                GETDATE() AS CaptureTime
        """;

    private static LiveMetricSnapshot ToLiveMetricSnapshot(dynamic r)
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
            WaitingTasks: Convert.ToInt32(r.WaitingTasks ?? 0)
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
    internal const string PerfmonCounterFilter = """
            WHERE (
                   object_name LIKE '%SQL Statistics%'
                OR object_name LIKE '%Buffer Manager%'
                OR object_name LIKE '%Memory Manager%'
                OR object_name LIKE '%Access Methods%'
                OR object_name LIKE '%General Statistics%'
                OR object_name LIKE '%Wait Statistics%'
                OR (object_name LIKE '%Locks%'     AND instance_name = '_Total')
                OR (object_name LIKE '%Databases%' AND instance_name IN ('_Total',''))
            )
            AND instance_name NOT IN (
                'internal','master','model','msdb','tempdb','mssqlsystemresource'
            )
            AND counter_name IN (
                -- General Throughput
                'Batch Requests/sec',
                'SQL Compilations/sec',
                'SQL Re-Compilations/sec',
                'Query optimizations/sec',
                'Network IO waits',
                -- Memory Pressure
                'Memory Grants Pending',
                'Granted Workspace Memory (KB)',
                'Target Server Memory (KB)',
                'Total Server Memory (KB)',
                'Stolen Server Memory (KB)',
                'Lock Memory (KB)',
                'SQL Cache Memory (KB)',
                'Lazy writes/sec',
                'Free list stalls/sec',
                'Reduced memory grants/sec',
                'Memory grant queue waits',
                'Thread-safe memory objects waits',
                'Page reads/sec',
                'Readahead pages/sec',
                -- CPU / Compilation
                'Active parallel threads',
                'Active requests',
                'Queued requests',
                'Wait for the worker',
                -- I/O Pressure
                'Page writes/sec',
                'Checkpoint pages/sec',
                'Page lookups/sec',
                'Background writer pages/sec',
                'Log Flushes/sec',
                'Log Bytes Flushed/sec',
                'Log Flush Write Time (ms)',
                'Page IO latch waits',
                'Log buffer waits',
                'Log write waits',
                'Full Scans/sec',
                'Index Searches/sec',
                'Page Splits/sec',
                'Forwarded Records/sec',
                -- TempDB Pressure
                'Version Store Size (KB)',
                'Free Space in tempdb (KB)',
                'Active Temp Tables',
                'Version Generation rate (KB/s)',
                'Version Cleanup rate (KB/s)',
                'Temp Tables Creation Rate',
                'Workfiles Created/sec',
                'Worktables Created/sec',
                -- Lock / Blocking
                'Lock Requests/sec',
                'Lock Wait Time (ms)',
                'Lock Waits/sec',
                'Number of Deadlocks/sec',
                'Table Lock Escalations/sec',
                'Blocked tasks',
                'Lock waits',
                'Non-Page latch waits',
                'Page latch waits',
                'Processes blocked',
                'Lock Timeouts/sec',
                -- Extras
                'Page life expectancy',
                'Buffer cache hit ratio',
                'Transactions/sec'
            )
            ORDER BY object_name, counter_name, instance_name
            """;

    public async Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync()
    {
        const string sql = """
            SELECT
                RTRIM(object_name)   AS ObjectName,
                RTRIM(counter_name)  AS CounterName,
                RTRIM(instance_name) AS InstanceName,
                cntr_value           AS CntrValue,
                cntr_type            AS CntrType,
                GETDATE()            AS CaptureTime
            FROM sys.dm_os_performance_counters
            """ + "\n" + PerfmonCounterFilter;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r =>
            new PerfmonCounter(
                (string)r.ObjectName,
                (string)r.CounterName,
                (string)r.InstanceName,
                (long)r.CntrValue,
                (int)r.CntrType,
                (DateTime)r.CaptureTime));
    }
}
