using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Wait statistics: cumulative, active, signal versus resource, and the top wait types. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class WaitsRepository(IConfiguration configuration) : BaseRepository(configuration), IWaitsRepository
{
    // ── Helper: category → hex color ─────────────────────────────────
    internal static string CategoryColor(string cat) => cat switch
    {

        "CPU" => "#FF6B6B",
        "I/O" => "#FFA62B",
        "Memory" => "#7B68EE",
        "Locks" => "#FF4560",
        "Network" => "#00D9FF",
        "Benign/Idle" => "#555555",
        _ => "#26E7A6"
    };

    // ── 1. Cumulative wait stats ──────────────────────────────────────
    public async Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync()
    {
        const string sql = """
            ;WITH WaitCategories AS (
                SELECT wait_type, waiting_tasks_count, wait_time_ms, max_wait_time_ms,
                    signal_wait_time_ms,
                    (wait_time_ms - signal_wait_time_ms) AS resource_wait_time_ms,
                    CASE
                        WHEN wait_type IN ('SOS_SCHEDULER_YIELD','THREADPOOL','CMEMTHREAD','CMEMPARTITIONED','EE_PMOLOCK','EXCHANGE','CXPACKET','CXCONSUMER','CXSYNC_PORT','CXSYNC_CONSUMER') THEN 'CPU'
                        WHEN wait_type IN ('ASYNC_IO_COMPLETION','IO_COMPLETION','PAGEIOLATCH_SH','PAGEIOLATCH_EX','PAGEIOLATCH_UP','PAGEIOLATCH_NL','PAGEIOLATCH_DT','PAGEIOLATCH_KP','WRITELOG','WRITE_COMPLETION','IO_QUEUE_LIMIT','IO_RETRY','BACKUPIO','BACKUPBUFFER','BACKUPTHREAD','LOGMGR','LOGMGR_QUEUE','LOGMGR_FLUSH','LOGMGR_RESERVE_APPEND','LOG_RATE_GOVERNOR') THEN 'I/O'
                        WHEN wait_type IN ('RESOURCE_SEMAPHORE','RESOURCE_SEMAPHORE_QUERY_COMPILE','RESOURCE_SEMAPHORE_MUTEX','PAGELATCH_SH','PAGELATCH_EX','PAGELATCH_UP','PAGELATCH_NL','PAGELATCH_DT','PAGELATCH_KP','LATCH_SH','LATCH_EX','LATCH_NL','LATCH_DT','LATCH_KP','LATCH_UP','MEMORY_ALLOCATION_EXT','MEMORY_GRANT_UPDATE','SOS_MEMORY_USAGE_ADJUSTMENT','SOS_VIRTUALMEMORY_LOW','CMEMTHREAD','CMEMPARTITIONED') THEN 'Memory'
                        WHEN wait_type LIKE 'LCK_M%' OR wait_type = 'LOCK_HASH' THEN 'Locks'
                        WHEN wait_type IN ('ASYNC_NETWORK_IO','NET_WAITFOR_PACKET','NETWORK_IO','OLEDB') THEN 'Network'
                        WHEN wait_type IN ('SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP','DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE','DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP') THEN 'Benign/Idle'
                        ELSE 'Other'
                    END AS WaitCategory,
                    CASE WHEN wait_type IN ('SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP','DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE','DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE') THEN 1 ELSE 0 END AS IsBenign
                FROM sys.dm_os_wait_stats WHERE waiting_tasks_count > 0
            ),
            TotalWaits AS (SELECT SUM(CAST(wait_time_ms AS BIGINT)) AS TotalWaitMs FROM WaitCategories WHERE IsBenign = 0)
            SELECT w.wait_type AS WaitType, w.WaitCategory, w.IsBenign,
                w.waiting_tasks_count AS WaitingTasksCount, w.wait_time_ms AS WaitTimeMs,
                w.max_wait_time_ms AS MaxWaitTimeMs, w.signal_wait_time_ms AS SignalWaitTimeMs,
                w.resource_wait_time_ms AS ResourceWaitTimeMs,
                CAST(w.wait_time_ms AS FLOAT)/NULLIF(w.waiting_tasks_count,0) AS AvgWaitTimeMs,
                CAST(w.signal_wait_time_ms AS FLOAT)/NULLIF(w.wait_time_ms,0)*100 AS SignalWaitPct,
                CAST(w.wait_time_ms AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 AS PctOfTotalWaits,
                CAST(w.wait_time_ms AS FLOAT)/1000.0 AS WaitTimeSec,
                GETDATE() AS CaptureTime, si.sqlserver_start_time AS ServerStartTime
            FROM WaitCategories w CROSS JOIN TotalWaits t CROSS JOIN sys.dm_os_sys_info si
            ORDER BY w.wait_time_ms DESC
            """;

        using var conn = CreateConnection();
        var rows = await Q(conn, sql);
        return rows.Select(r => new WaitStatCumulative(
            (string)r.WaitType, (string)r.WaitCategory, (int)r.IsBenign,
            (long)r.WaitingTasksCount, (long)r.WaitTimeMs, (long)r.MaxWaitTimeMs,
            (long)r.SignalWaitTimeMs, (long)r.ResourceWaitTimeMs,
            (double)(r.AvgWaitTimeMs ?? 0), (double)(r.SignalWaitPct ?? 0),
            (double)(r.PctOfTotalWaits ?? 0), (double)r.WaitTimeSec,
            (DateTime)r.CaptureTime, (DateTime)r.ServerStartTime,
            CategoryColor((string)r.WaitCategory),
            (double)(r.PctOfTotalWaits ?? 0) > 20 ? "High"
                : (double)(r.PctOfTotalWaits ?? 0) > 5 ? "Medium" : "Low"
        ));
    }

    // ── 2. Active waits ───────────────────────────────────────────────
    // sys.dm_exec_requests.dop is SQL Server 2016+; before that the degree of parallelism reads 0.
    internal static string ActiveWaitsSql(bool hasDop) => ActiveWaitsSqlTemplate.Replace("/*DOP*/", hasDop ? "r.dop" : "0");

    public async Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync()
    {
        using var conn = CreateConnection();
        var sql = ActiveWaitsSql(await HasColumnAsync(conn, "sys.dm_exec_requests", "dop"));
        var rows = await Q(conn, sql);
        return rows.Select(r =>
        {
            double sec = (double)r.WaitTimeSec;
            string severity = sec > 60 ? "Critical" : sec > 10 ? "High" : sec > 1 ? "Medium" : "Low";
            return new ActiveWait(
                (int)r.SessionId, (int)r.BlockingSessionId,
                (string?)r.WaitType, (long)r.WaitTimeMs, sec,
                (string?)r.LastWaitType, (string?)r.WaitResource,
                (string?)r.RequestStatus, (string?)r.Command,
                (long)r.CpuTimeMs, (long)r.TotalElapsedMs,
                (long)r.LogicalReads, (long)r.Writes, (long)r.PhysicalReads,
                (long?)r.GrantedMemoryKB ?? 0, Convert.ToInt32(r.DegreeOfParallelism),
                (string?)r.LoginName, (string?)r.HostName,
                (string?)r.ProgramName, (string?)r.DatabaseName,
                (string)r.WaitCategory, (int)r.IsBlocked,
                (string?)r.QueryText, (DateTime)r.CaptureTime, severity
            );
        });
    }

    private const string ActiveWaitsSqlTemplate = """
            SELECT r.session_id AS SessionId, r.blocking_session_id AS BlockingSessionId,
                r.wait_type AS WaitType, r.wait_time AS WaitTimeMs,
                CAST(r.wait_time AS FLOAT)/1000.0 AS WaitTimeSec,
                r.last_wait_type AS LastWaitType, r.wait_resource AS WaitResource,
                r.status AS RequestStatus, r.command AS Command,
                r.cpu_time AS CpuTimeMs, r.total_elapsed_time AS TotalElapsedMs,
                r.logical_reads AS LogicalReads, r.writes AS Writes,
                r.reads AS PhysicalReads,
                r.granted_query_memory*8 AS GrantedMemoryKB,
                /*DOP*/ AS DegreeOfParallelism,
                s.login_name AS LoginName, s.host_name AS HostName,
                s.program_name AS ProgramName, DB_NAME(r.database_id) AS DatabaseName,
                CASE
                    WHEN r.wait_type IN ('SOS_SCHEDULER_YIELD','THREADPOOL','CMEMTHREAD','CXPACKET','CXCONSUMER') THEN 'CPU'
                    WHEN r.wait_type IN ('ASYNC_IO_COMPLETION','IO_COMPLETION','PAGEIOLATCH_SH','PAGEIOLATCH_EX','PAGEIOLATCH_UP','WRITELOG','WRITE_COMPLETION') THEN 'I/O'
                    WHEN r.wait_type IN ('RESOURCE_SEMAPHORE','PAGELATCH_SH','PAGELATCH_EX','PAGELATCH_UP','LATCH_SH','LATCH_EX','LATCH_UP','MEMORY_ALLOCATION_EXT','SOS_VIRTUALMEMORY_LOW') THEN 'Memory'
                    WHEN r.wait_type LIKE 'LCK_M%' THEN 'Locks'
                    WHEN r.wait_type IN ('ASYNC_NETWORK_IO','NET_WAITFOR_PACKET','NETWORK_IO','OLEDB') THEN 'Network'
                    ELSE 'Other'
                END AS WaitCategory,
                CASE WHEN r.blocking_session_id > 0 THEN 1 ELSE 0 END AS IsBlocked,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_requests r
            JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
            WHERE r.session_id <> @@SPID AND s.is_user_process = 1
            """;

    // ── 4. Signal vs resource ─────────────────────────────────────────
    public async Task<SignalVsResourceWait?> GetSignalVsResourceAsync()
    {
        const string sql = """
            ;WITH WaitData AS (
                SELECT wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms,
                       (wait_time_ms - signal_wait_time_ms) AS resource_wait_time_ms
                FROM sys.dm_os_wait_stats WHERE waiting_tasks_count > 0
                  AND wait_type NOT IN ('SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP','DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE','DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP')
            ),
            Totals AS (
                SELECT SUM(CAST(wait_time_ms AS BIGINT)) AS TotalWaitMs,
                       SUM(CAST(signal_wait_time_ms AS BIGINT)) AS TotalSignalMs,
                       SUM(CAST(resource_wait_time_ms AS BIGINT)) AS TotalResourceMs
                FROM WaitData
            )
            SELECT t.TotalWaitMs, t.TotalSignalMs, t.TotalResourceMs,
                CAST(t.TotalSignalMs AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 AS ServerSignalWaitPct,
                CAST(t.TotalResourceMs AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 AS ServerResourceWaitPct,
                CASE WHEN CAST(t.TotalSignalMs AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 > 25 THEN 'CRITICAL'
                     WHEN CAST(t.TotalSignalMs AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 > 10 THEN 'WARNING'
                     ELSE 'NORMAL' END AS CPUPressureLevel,
                CASE WHEN CAST(t.TotalSignalMs AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 > 25 THEN 'High CPU pressure detected. Threads waiting for CPU scheduler.'
                     WHEN CAST(t.TotalSignalMs AS FLOAT)/NULLIF(t.TotalWaitMs,0)*100 > 10 THEN 'Moderate CPU pressure. Monitor scheduler queue depth.'
                     ELSE 'CPU pressure within normal range.' END AS CPUPressureDescription,
                CAST(t.TotalWaitMs AS FLOAT)/1000.0 AS TotalWaitSec,
                CAST(t.TotalSignalMs AS FLOAT)/1000.0 AS TotalSignalSec,
                CAST(t.TotalResourceMs AS FLOAT)/1000.0 AS TotalResourceSec,
                si.sqlserver_start_time AS ServerStartTime, GETDATE() AS CaptureTime
            FROM Totals t CROSS JOIN sys.dm_os_sys_info si
            """;

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql);
        if (r is null) return null;
        return new SignalVsResourceWait(
            (long)r.TotalWaitMs, (long)r.TotalSignalMs, (long)r.TotalResourceMs,
            (double)(r.ServerSignalWaitPct ?? 0), (double)(r.ServerResourceWaitPct ?? 0),
            (string)r.CPUPressureLevel, (string)r.CPUPressureDescription,
            (double)r.TotalWaitSec, (double)r.TotalSignalSec, (double)r.TotalResourceSec,
            (DateTime)r.ServerStartTime, (DateTime)r.CaptureTime
        );
    }

    // ── 5. Top 25 wait types ──────────────────────────────────────────
    public async Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync()
    {
        const string sql = """
            ;WITH FilteredWaits AS (
                SELECT wait_type, waiting_tasks_count, wait_time_ms, max_wait_time_ms,
                    signal_wait_time_ms,
                    (wait_time_ms - signal_wait_time_ms) AS resource_wait_time_ms,
                    CAST(signal_wait_time_ms AS FLOAT)/NULLIF(wait_time_ms,0)*100 AS signal_wait_pct,
                    ROW_NUMBER() OVER (ORDER BY wait_time_ms DESC) AS WaitRank
                FROM sys.dm_os_wait_stats WHERE waiting_tasks_count > 0
                  AND wait_type NOT IN ('SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP','DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE','DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','DBMIRROR_EVENTS_QUEUED','DBMIRROR_WORKER_QUEUE','BROKER_EVENTHANDLER','BROKER_RECEIVE_WAITFOR','BROKER_TRANSMITTER','CLR_AUTO_EVENT','CLR_MANUAL_EVENT','CLR_SYNCHRONIZE','FT_IFTS_SCHEDULER_IDLE_WAIT','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','DEADLOCK_ENUM_MUTEX','WAIT_XTP_OFFLINE_CKPT_NEW_LOG','WAIT_XTP_RECOVERY','WAIT_XTP_HOST_WAIT','WAIT_XTP_CKPT_CLOSE','PARALLEL_REDO_DRAIN_WORKER','PARALLEL_REDO_LOG_CACHE','PARALLEL_REDO_TRAN_LIST','PARALLEL_REDO_WORKER_SYNC','PARALLEL_REDO_WORKER_WAIT_WORK','XE_DISPATCHER_JOIN','XE_DISPATCH_QUEUE_SEMAPHORE','SQLTRACE_WAIT_ENTRIES')
            ),
            TotalWait AS (SELECT SUM(CAST(wait_time_ms AS BIGINT)) AS TotalMs FROM FilteredWaits)
            SELECT TOP 25 f.WaitRank, f.wait_type AS WaitType,
                CASE
                    WHEN f.wait_type IN ('SOS_SCHEDULER_YIELD','THREADPOOL','CMEMTHREAD','CMEMPARTITIONED','EE_PMOLOCK','EXCHANGE','CXPACKET','CXCONSUMER','CXSYNC_PORT','CXSYNC_CONSUMER') THEN 'CPU'
                    WHEN f.wait_type IN ('ASYNC_IO_COMPLETION','IO_COMPLETION','PAGEIOLATCH_SH','PAGEIOLATCH_EX','PAGEIOLATCH_UP','PAGEIOLATCH_NL','PAGEIOLATCH_DT','PAGEIOLATCH_KP','WRITELOG','WRITE_COMPLETION','IO_QUEUE_LIMIT','IO_RETRY','BACKUPIO','BACKUPBUFFER','BACKUPTHREAD','LOGMGR','LOGMGR_QUEUE','LOGMGR_FLUSH','LOGMGR_RESERVE_APPEND','LOG_RATE_GOVERNOR') THEN 'I/O'
                    WHEN f.wait_type IN ('RESOURCE_SEMAPHORE','RESOURCE_SEMAPHORE_QUERY_COMPILE','PAGELATCH_SH','PAGELATCH_EX','PAGELATCH_UP','PAGELATCH_NL','PAGELATCH_DT','PAGELATCH_KP','LATCH_SH','LATCH_EX','LATCH_NL','LATCH_DT','LATCH_KP','LATCH_UP','MEMORY_ALLOCATION_EXT','SOS_VIRTUALMEMORY_LOW') THEN 'Memory'
                    WHEN f.wait_type LIKE 'LCK_M%' OR f.wait_type = 'LOCK_HASH' THEN 'Locks'
                    WHEN f.wait_type IN ('ASYNC_NETWORK_IO','NET_WAITFOR_PACKET','NETWORK_IO','OLEDB') THEN 'Network'
                    ELSE 'Other'
                END AS WaitCategory,
                f.waiting_tasks_count AS WaitingTasksCount,
                f.wait_time_ms AS WaitTimeMs, f.max_wait_time_ms AS MaxWaitTimeMs,
                f.signal_wait_time_ms AS SignalWaitTimeMs,
                f.resource_wait_time_ms AS ResourceWaitTimeMs,
                f.signal_wait_pct AS SignalWaitPct,
                CAST(f.wait_time_ms AS FLOAT)/1000.0 AS WaitTimeSec,
                CAST(f.wait_time_ms AS FLOAT)/NULLIF(f.waiting_tasks_count,0) AS AvgWaitMsPerTask,
                CAST(f.wait_time_ms AS FLOAT)/NULLIF(t.TotalMs,0)*100 AS PctOfTotal,
                SUM(CAST(f.wait_time_ms AS FLOAT)/NULLIF(t.TotalMs,0)*100)
                    OVER (ORDER BY f.wait_time_ms DESC ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS CumulativePct,
                GETDATE() AS CaptureTime
            FROM FilteredWaits f CROSS JOIN TotalWait t ORDER BY f.wait_time_ms DESC
            """;

        using var conn = CreateConnection();
        var rows = await Q(conn, sql);
        return rows.Select(r =>
        {
            string cat = (string)r.WaitCategory;
            string wt = (string)r.WaitType;
            int rank = (int)(long)r.WaitRank;
            return new TopWaitType(
                rank, wt, cat,
                (long)r.WaitingTasksCount, (long)r.WaitTimeMs, (long)r.MaxWaitTimeMs,
                (long)r.SignalWaitTimeMs, (long)r.ResourceWaitTimeMs,
                (double)(r.SignalWaitPct ?? 0), (double)r.WaitTimeSec,
                (double)(r.AvgWaitMsPerTask ?? 0), (double)(r.PctOfTotal ?? 0),
                (double)(r.CumulativePct ?? 0), (DateTime)r.CaptureTime,
                CategoryColor(cat),
                $"#{rank} {wt}"
            );
        });
    }
}
