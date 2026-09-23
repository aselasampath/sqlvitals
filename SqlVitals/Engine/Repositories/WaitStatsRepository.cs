using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Repositories;

public class WaitStatsRepository(IConfiguration configuration) : BaseRepository(configuration), IWaitStatsRepository
{
    // ── Helper: category → hex color ─────────────────────────────────
    internal static string CategoryColor(string cat) => cat switch
    {

        "CPU"         => "#FF6B6B",
        "I/O"         => "#FFA62B",
        "Memory"      => "#7B68EE",
        "Locks"       => "#FF4560",
        "Network"     => "#00D9FF",
        "Benign/Idle" => "#555555",
        _             => "#26E7A6"
    };

    internal static int CategorySortOrder(string cat) => cat switch
    {
        "CPU"     => 1, "I/O"  => 2, "Memory" => 3,
        "Locks"   => 4, "Network" => 5, "Other" => 6,
        _ => 7
    };

    private readonly PlanCacheHealthRepository _planHealth = new(configuration);

    // Stateful across polls (dedup window + previous DMV snapshot), so it must be a single
    // long-lived instance rather than created per call.
    private readonly SpTraceRepository _spTrace = new(configuration);

    // ── Engine edition detection ──────────────────────────────────────
    // EngineEdition 5 = Azure SQL Database, which lacks sys.master_files and other
    // server-scoped DMVs used by TempDB file-level reporting (see GetTempDbPressureAsync).
    public async Task<bool> IsAzureSqlDatabaseAsync()
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
        return row?.Edition == 5;
    }

    // Online index rebuilds are an edition feature, so the maintenance script only offers
    // WITH (ONLINE = ON) where the server accepts it (see IndexMaintenanceScript).
    public async Task<bool> SupportsOnlineIndexRebuildAsync()
    {
        using var conn = CreateConnection();
        var row = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition");
        return row is not null && IndexMaintenanceScript.EditionSupportsOnlineRebuild(Convert.ToInt32(row.Edition));
    }

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
    public async Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync()
    {
        const string sql = """
            SELECT r.session_id AS SessionId, r.blocking_session_id AS BlockingSessionId,
                r.wait_type AS WaitType, r.wait_time AS WaitTimeMs,
                CAST(r.wait_time AS FLOAT)/1000.0 AS WaitTimeSec,
                r.last_wait_type AS LastWaitType, r.wait_resource AS WaitResource,
                r.status AS RequestStatus, r.command AS Command,
                r.cpu_time AS CpuTimeMs, r.total_elapsed_time AS TotalElapsedMs,
                r.logical_reads AS LogicalReads, r.writes AS Writes,
                r.reads AS PhysicalReads,
                r.granted_query_memory*8 AS GrantedMemoryKB,
                r.dop AS DegreeOfParallelism,
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

        using var conn = CreateConnection();
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
                (long?)r.GrantedMemoryKB ?? 0, (int)r.DegreeOfParallelism,
                (string?)r.LoginName, (string?)r.HostName,
                (string?)r.ProgramName, (string?)r.DatabaseName,
                (string)r.WaitCategory, (int)r.IsBlocked,
                (string?)r.QueryText, (DateTime)r.CaptureTime, severity
            );
        });
    }

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

    // ── 8. TempDB Pressure ───────────────────────────────────────────
    public async Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync()
    {
        const string filesSql = """
            SELECT mf.name AS FileName, mf.type_desc AS FileType,
                mf.physical_name AS PhysicalPath,
                mf.size * 8 / 1024 AS FileSizeMB,
                ISNULL(FILEPROPERTY(mf.name,'SpaceUsed'),0) * 8 / 1024 AS SpaceUsedMB,
                (mf.size - ISNULL(FILEPROPERTY(mf.name,'SpaceUsed'),0)) * 8 / 1024 AS FreeSpaceMB,
                CAST(ISNULL(FILEPROPERTY(mf.name,'SpaceUsed'),0)*100.0/NULLIF(mf.size,0) AS DECIMAL(10,2)) AS UsedPct,
                CASE mf.is_percent_growth WHEN 1 THEN CAST(mf.growth AS VARCHAR)+'%'
                    ELSE CAST(mf.growth*8/1024 AS VARCHAR)+' MB' END AS AutoGrowth,
                GETDATE() AS CaptureTime
            FROM sys.master_files mf WHERE mf.database_id = 2
            ORDER BY mf.type_desc, mf.file_id
            """;

        const string sessionsSql = """
            SELECT TOP 20 su.session_id AS SessionId,
                su.user_objects_alloc_page_count*8 AS UserObjAllocKB,
                su.user_objects_dealloc_page_count*8 AS UserObjDeallocKB,
                su.internal_objects_alloc_page_count*8 AS InternalObjAllocKB,
                su.internal_objects_dealloc_page_count*8 AS InternalObjDeallocKB,
                (su.user_objects_alloc_page_count+su.internal_objects_alloc_page_count
                 -su.user_objects_dealloc_page_count-su.internal_objects_dealloc_page_count)*8 AS NetTempDBUsageKB,
                s.login_name AS LoginName, s.host_name AS HostName,
                s.program_name AS ProgramName,
                r.wait_type AS WaitType, r.status AS RequestStatus,
                ISNULL(r.cpu_time,0) AS CpuTimeMs,
                DB_NAME(r.database_id) AS DatabaseName,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_db_session_space_usage su
            JOIN sys.dm_exec_sessions s ON su.session_id = s.session_id
            LEFT JOIN sys.dm_exec_requests r ON su.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
            WHERE su.session_id > 50
              AND (su.user_objects_alloc_page_count+su.internal_objects_alloc_page_count) > 0
            ORDER BY NetTempDBUsageKB DESC
            """;

        const string countersSql = """
            SELECT counter_name AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%Transactions%' AND instance_name='tempdb'
              AND counter_name IN ('Active Transactions','Version Store Size (KB)','Version Generation rate (KB/s)','Version Cleanup rate (KB/s)')
            UNION ALL
            SELECT counter_name, cntr_value, GETDATE()
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%General Statistics%'
              AND counter_name IN ('Active Temp Tables','Temp Tables Creation Rate','Temp Tables For Destruction')
            """;

        using var conn = CreateConnection();
        var files = (await Q(conn, filesSql)).Select(r => new TempDbFile(
            (string)r.FileName, (string)r.FileType, (string)r.PhysicalPath,
            (long)r.FileSizeMB, (long)r.SpaceUsedMB, (long)r.FreeSpaceMB,
            (double)(r.UsedPct ?? 0), (string)r.AutoGrowth, (DateTime)r.CaptureTime));

        var sessions = (await Q(conn, sessionsSql)).Select(r =>
        {
            long net = (long)r.NetTempDBUsageKB;
            string sev = net > 1_000_000 ? "Critical" : net > 100_000 ? "Warning" : "Info";
            return new TempDbSession(
                (int)r.SessionId, (long)r.UserObjAllocKB, (long)r.UserObjDeallocKB,
                (long)r.InternalObjAllocKB, (long)r.InternalObjDeallocKB, net,
                (string?)r.LoginName, (string?)r.HostName, (string?)r.ProgramName,
                (string?)r.WaitType, (string?)r.RequestStatus, (long)r.CpuTimeMs,
                (string?)r.DatabaseName, (string?)r.QueryText, (DateTime)r.CaptureTime, sev);
        });

        var counters = (await Q(conn, countersSql)).Select(r =>
            new TempDbCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        return (files, sessions, counters);
    }


    // ── 9. Memory grants ──────────────────────────────────────────────
    public async Task<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)> GetMemoryGrantsAsync()
    {
        // SQL Server 2016+ (ProductMajorVersion >= 13) and Azure SQL have queue_id,
        // wait_order, is_next_candidate, wait_time_ms in sys.dm_exec_query_memory_grants.
        // Older on-prem versions (2014/2012) lack those columns — use NULL/0 placeholders.
        const string grantsSqlFull = """
            SELECT TOP 25
                mg.session_id AS SessionId, mg.request_id AS RequestId,
                mg.scheduler_id AS SchedulerId, mg.dop AS DegreeOfParallelism,
                mg.request_time AS RequestTime, mg.grant_time AS GrantTime,
                DATEDIFF(ms,mg.request_time,ISNULL(mg.grant_time,GETDATE())) AS WaitForGrantMs,
                mg.requested_memory_kb AS RequestedMemoryKB,
                ISNULL(mg.granted_memory_kb,0) AS GrantedMemoryKB,
                ISNULL(mg.used_memory_kb,0) AS UsedMemoryKB,
                ISNULL(mg.max_used_memory_kb,0) AS MaxUsedMemoryKB,
                CASE WHEN ISNULL(mg.granted_memory_kb,0)>0
                     THEN CAST(ISNULL(mg.used_memory_kb,0)*100.0/NULLIF(mg.granted_memory_kb,0) AS DECIMAL(10,1))
                     ELSE NULL END AS MemGrantUsedPct,
                mg.ideal_memory_kb AS IdealMemoryKB,
                mg.required_memory_kb AS RequiredMemoryKB,
                mg.queue_id AS QueueId, mg.wait_order AS WaitOrder,
                CAST(mg.is_next_candidate AS BIT) AS IsNextCandidate,
                ISNULL(mg.wait_time_ms,0) AS WaitTimeMs,
                s.login_name AS LoginName, s.host_name AS HostName,
                s.program_name AS ProgramName,
                DB_NAME(r.database_id) AS DatabaseName,
                r.status AS RequestStatus, r.wait_type AS WaitType,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_query_memory_grants mg
            JOIN sys.dm_exec_sessions s ON mg.session_id = s.session_id
            LEFT JOIN sys.dm_exec_requests r ON mg.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(mg.plan_handle) qt
            ORDER BY ISNULL(mg.granted_memory_kb,mg.requested_memory_kb) DESC
            """;

        // Compatible query for SQL Server 2014 and earlier (ProductMajorVersion <= 12):
        // queue_id, wait_order, is_next_candidate, wait_time_ms did not exist yet.
        const string grantsSqlCompat = """
            SELECT TOP 25
                mg.session_id AS SessionId, mg.request_id AS RequestId,
                mg.scheduler_id AS SchedulerId, mg.dop AS DegreeOfParallelism,
                mg.request_time AS RequestTime, mg.grant_time AS GrantTime,
                DATEDIFF(ms,mg.request_time,ISNULL(mg.grant_time,GETDATE())) AS WaitForGrantMs,
                mg.requested_memory_kb AS RequestedMemoryKB,
                ISNULL(mg.granted_memory_kb,0) AS GrantedMemoryKB,
                ISNULL(mg.used_memory_kb,0) AS UsedMemoryKB,
                ISNULL(mg.max_used_memory_kb,0) AS MaxUsedMemoryKB,
                CASE WHEN ISNULL(mg.granted_memory_kb,0)>0
                     THEN CAST(ISNULL(mg.used_memory_kb,0)*100.0/NULLIF(mg.granted_memory_kb,0) AS DECIMAL(10,1))
                     ELSE NULL END AS MemGrantUsedPct,
                mg.ideal_memory_kb AS IdealMemoryKB,
                mg.required_memory_kb AS RequiredMemoryKB,
                NULL AS QueueId, NULL AS WaitOrder,
                CAST(0 AS BIT) AS IsNextCandidate,
                0 AS WaitTimeMs,
                s.login_name AS LoginName, s.host_name AS HostName,
                s.program_name AS ProgramName,
                DB_NAME(r.database_id) AS DatabaseName,
                r.status AS RequestStatus, r.wait_type AS WaitType,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_query_memory_grants mg
            JOIN sys.dm_exec_sessions s ON mg.session_id = s.session_id
            LEFT JOIN sys.dm_exec_requests r ON mg.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(mg.plan_handle) qt
            ORDER BY ISNULL(mg.granted_memory_kb,mg.requested_memory_kb) DESC
            """;

        // Select the right variant based on SQL Server version.
        // Azure SQL DB (5) and MI (8) always support the full column set.
        const string sqlMeta = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) AS MajorVersion, CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition";
        var grantsSql = grantsSqlFull; // resolved after meta query below

        const string clerksSql = """
            SELECT TOP 15 mc.type AS ClerkType, mc.name AS ClerkName,
                SUM(mc.pages_kb) AS TotalPagesKB,
                SUM(mc.pages_kb)/1024 AS TotalPagesMB,
                CAST(SUM(mc.pages_kb)*100.0/NULLIF((SELECT SUM(pages_kb) FROM sys.dm_os_memory_clerks),0) AS DECIMAL(10,2)) AS PctOfTotalMemory,
                GETDATE() AS CaptureTime
            FROM sys.dm_os_memory_clerks mc
            GROUP BY mc.type, mc.name ORDER BY TotalPagesKB DESC
            """;

        const string countersSql = """
            SELECT counter_name AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%Memory Manager%'
              AND counter_name IN (
                  'Memory Grants Outstanding','Memory Grants Pending',
                  'Maximum Workspace Memory (KB)','Target Server Memory (KB)',
                  'Total Server Memory (KB)','Stolen Server Memory (KB)',
                  'Free Memory (KB)','Database Cache Memory (KB)')
            ORDER BY counter_name
            """;

        using var conn = CreateConnection();

        // Detect version once; default to full query if meta unavailable
        var meta = await QFirst(conn, sqlMeta);
        int majorVer = meta != null ? (int)meta.MajorVersion : 13;
        int eng      = meta != null ? (int)meta.Edition      : 0;
        bool supportsNewCols = majorVer >= 13 || eng == 5 || eng == 8; // 2016+ | Azure SQL DB | MI
        grantsSql = supportsNewCols ? grantsSqlFull : grantsSqlCompat;

        var grants = (await Q(conn, grantsSql)).Select(r =>
        {
            var row = (IDictionary<string, object>)r;
            object? Get(string k) => row.TryGetValue(k, out var v) && v is not DBNull ? v : null;
            static int    ToInt(object? v)   => v is null ? 0   : Convert.ToInt32(v);
            static long   ToLong(object? v)  => v is null ? 0L  : Convert.ToInt64(v);
            static bool   ToBool(object? v)  => v is not null && Convert.ToBoolean(v);
            static int?   ToIntN(object? v)  => v is null ? (int?)null   : Convert.ToInt32(v);
            static double? ToDblN(object? v) => v is null ? (double?)null : Convert.ToDouble(v);

            long granted = ToLong(Get("GrantedMemoryKB"));
            bool pending = Get("GrantTime") is null;
            string sev   = pending ? "Critical" : granted > 1_048_576 ? "Warning" : "Info";

            return new MemoryGrant(
                ToInt(Get("SessionId")),
                ToInt(Get("RequestId")),
                ToInt(Get("SchedulerId")),
                ToInt(Get("DegreeOfParallelism")),
                (DateTime)row["RequestTime"],
                Get("GrantTime") is DateTime gt ? gt : (DateTime?)null,
                ToInt(Get("WaitForGrantMs")),
                ToLong(Get("RequestedMemoryKB")),
                granted,
                ToLong(Get("UsedMemoryKB")),
                ToLong(Get("MaxUsedMemoryKB")),
                ToDblN(Get("MemGrantUsedPct")),
                ToLong(Get("IdealMemoryKB")),
                ToLong(Get("RequiredMemoryKB")),
                ToIntN(Get("QueueId")),
                ToIntN(Get("WaitOrder")),
                ToBool(Get("IsNextCandidate")),
                ToInt(Get("WaitTimeMs")),
                Get("LoginName")     as string,
                Get("HostName")      as string,
                Get("ProgramName")   as string,
                Get("DatabaseName")  as string,
                Get("RequestStatus") as string,
                Get("WaitType")      as string,
                Get("QueryText")     as string,
                (DateTime)row["CaptureTime"],
                pending, sev);
        });

        var clerks = (await Q(conn, clerksSql)).Select(r =>
            new MemoryClerk((string)r.ClerkType, (string)r.ClerkName,
                (long)r.TotalPagesKB, (long)r.TotalPagesMB,
                (double)(r.PctOfTotalMemory ?? 0), (DateTime)r.CaptureTime));

        var counters = (await Q(conn, countersSql)).Select(r =>
            new MemoryCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        return (grants, clerks, counters);
    }

    // ── 10. Query Store ───────────────────────────────────────────────
    public async Task<(QueryStoreHealth?, IEnumerable<QueryStoreTopQuery>)> GetQueryStoreAsync()
    {
        const string healthSql = """
            SELECT actual_state_desc AS ActualState, desired_state_desc AS DesiredState,
                readonly_reason AS ReadOnlyReason,
                current_storage_size_mb AS CurrentStorageSizeMB,
                max_storage_size_mb AS MaxStorageSizeMB,
                CAST(current_storage_size_mb*100.0/NULLIF(max_storage_size_mb,0) AS DECIMAL(10,1)) AS StorageUsedPct,
                flush_interval_seconds AS FlushIntervalSec,
                interval_length_minutes AS IntervalLengthMin,
                stale_query_threshold_days AS StaleQueryThresholdDays,
                size_based_cleanup_mode_desc AS SizeCleanupMode,
                query_capture_mode_desc AS CaptureMode,
                max_plans_per_query AS MaxPlansPerQuery,
                (SELECT COUNT(*) FROM sys.query_store_query) AS TotalQueries,
                (SELECT COUNT(*) FROM sys.query_store_plan) AS TotalPlans,
                (SELECT COUNT(*) FROM sys.query_store_plan WHERE is_forced_plan=1) AS ForcedPlans,
                GETDATE() AS CaptureTime
            FROM sys.database_query_store_options
            """;

        const string queriesSql = """
            ;WITH PlanAgg AS (
                SELECT rs.plan_id,
                    SUM(rs.avg_cpu_time*rs.count_executions) AS TotalCpuUs,
                    SUM(rs.avg_duration*rs.count_executions) AS TotalDurationUs,
                    SUM(rs.avg_logical_io_reads*rs.count_executions) AS TotalLogicalReads,
                    SUM(rs.avg_logical_io_writes*rs.count_executions) AS TotalLogicalWrites,
                    SUM(rs.avg_physical_io_reads*rs.count_executions) AS TotalPhysicalReads,
                    SUM(rs.avg_query_max_used_memory*rs.count_executions) AS TotalMemoryPages,
                    SUM(rs.count_executions) AS TotalExecutions,
                    MAX(rs.last_execution_time) AS LastExecutionTime
                FROM sys.query_store_runtime_stats rs
                WHERE rs.last_execution_time >= DATEADD(HOUR,-24,GETUTCDATE())
                GROUP BY rs.plan_id
            ),
            Ranked AS (
                SELECT p.query_id, pa.plan_id, pa.TotalCpuUs, pa.TotalDurationUs,
                    pa.TotalLogicalReads, pa.TotalLogicalWrites, pa.TotalPhysicalReads,
                    pa.TotalMemoryPages, pa.TotalExecutions, pa.LastExecutionTime,
                    p.is_forced_plan, p.plan_type_desc,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalCpuUs/pa.TotalExecutions ELSE 0 END AS AvgCpuUs,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalDurationUs/pa.TotalExecutions ELSE 0 END AS AvgDurationUs,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalLogicalReads/pa.TotalExecutions ELSE 0 END AS AvgLogicalReads,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalMemoryPages*8/pa.TotalExecutions ELSE 0 END AS AvgMemoryKB,
                    ROW_NUMBER() OVER (PARTITION BY p.query_id ORDER BY pa.TotalCpuUs DESC) AS rn
                FROM PlanAgg pa JOIN sys.query_store_plan p ON pa.plan_id=p.plan_id
                WHERE p.query_plan IS NOT NULL
            )
            SELECT TOP 20 r.query_id AS QueryId, r.plan_id AS PlanId,
                qt.query_sql_text AS QueryText,
                r.TotalExecutions,
                r.TotalCpuUs/1000.0 AS TotalCpuMs, r.AvgCpuUs/1000.0 AS AvgCpuMs,
                r.TotalDurationUs/1000.0 AS TotalDurationMs, r.AvgDurationUs/1000.0 AS AvgDurationMs,
                r.TotalLogicalReads, r.AvgLogicalReads,
                r.TotalLogicalWrites, r.TotalPhysicalReads,
                r.TotalMemoryPages*8/1024.0 AS TotalMemoryMB, r.AvgMemoryKB*1.0 AS AvgMemoryKB,
                r.is_forced_plan AS IsForcedPlan, r.plan_type_desc AS PlanType,
                r.LastExecutionTime AS LastExecutionTimeUtc, GETDATE() AS CaptureTime
            FROM Ranked r
            JOIN sys.query_store_query q ON r.query_id=q.query_id
            JOIN sys.query_store_query_text qt ON q.query_text_id=qt.query_text_id
            WHERE r.rn=1 ORDER BY r.TotalCpuUs DESC OPTION (LOOP JOIN)
            """;

        using var conn = CreateConnection();

        QueryStoreHealth? health = null;
        try
        {
            var hr = await QFirst(conn, healthSql);
            if (hr is not null)
            {
                bool enabled = ((string)hr.ActualState).StartsWith("READ", StringComparison.OrdinalIgnoreCase);
                double used = (double)(hr.StorageUsedPct ?? 0);
                string sev = !enabled ? "WARNING" : used > 90 ? "WARNING" : "OK";
                health = new QueryStoreHealth(
                    (string)hr.ActualState, (string)hr.DesiredState,
                    (string?)hr.ReadOnlyReason,
                    (long)hr.CurrentStorageSizeMB, (long)hr.MaxStorageSizeMB,
                    used, (int)hr.FlushIntervalSec, (int)hr.IntervalLengthMin,
                    (int)hr.StaleQueryThresholdDays, (string)hr.SizeCleanupMode,
                    (string)hr.CaptureMode, (int)hr.MaxPlansPerQuery,
                    (int)hr.TotalQueries, (int)hr.TotalPlans, (int)hr.ForcedPlans,
                    (DateTime)hr.CaptureTime, enabled, sev);
            }
        }
        catch (SqlException ex) when (ex.Number == 208) { /* Query Store catalog views don't exist on this SQL version */ }

        IEnumerable<QueryStoreTopQuery> queries = [];
        try
        {
            var rows = await Q(conn, queriesSql);
            queries = rows.Select(r =>
            {
                double cpu = (double)r.TotalCpuMs;
                string sev = cpu > 60_000 ? "Critical" : cpu > 10_000 ? "Warning" : "Info";
                return new QueryStoreTopQuery(
                    (long)r.QueryId, (long)r.PlanId,
                    (string)r.QueryText, (long)r.TotalExecutions,
                    cpu, (double)r.AvgCpuMs,
                    (double)r.TotalDurationMs, (double)r.AvgDurationMs,
                    (long)r.TotalLogicalReads, (double)r.AvgLogicalReads,
                    (long)r.TotalLogicalWrites, (long)r.TotalPhysicalReads,
                    (double)r.TotalMemoryMB, (double)r.AvgMemoryKB,
                    (bool)r.IsForcedPlan, (string)r.PlanType,
                    (DateTime)r.LastExecutionTimeUtc, (DateTime)r.CaptureTime, sev);
            }).ToList();
        }
        catch (SqlException ex) when (ex.Number == 208) { /* Query Store catalog views don't exist on this SQL version */ }

        return (health, queries);
    }

    // ── 11. Index health ──────────────────────────────────────────────
    public async Task<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)> GetIndexHealthAsync()
    {
        const string missingSql = """
            SELECT TOP 25
                DB_NAME(mid.database_id) AS DatabaseName,
                OBJECT_SCHEMA_NAME(mid.object_id,mid.database_id) AS SchemaName,
                OBJECT_NAME(mid.object_id,mid.database_id) AS TableName,
                mid.equality_columns AS EqualityColumns,
                mid.inequality_columns AS InequalityColumns,
                mid.included_columns AS IncludedColumns,
                migs.unique_compiles AS UniqueCompiles,
                migs.user_seeks AS UserSeeks, migs.user_scans AS UserScans,
                migs.last_user_seek AS LastUserSeek, migs.last_user_scan AS LastUserScan,
                migs.avg_total_user_cost AS AvgTotalUserCost,
                migs.avg_user_impact AS AvgUserImpact,
                CAST((migs.user_seeks+migs.user_scans)*migs.avg_total_user_cost*(migs.avg_user_impact/100.0) AS DECIMAL(18,2)) AS ImpactScore,
                CASE WHEN migs.avg_user_impact>=80 THEN 'CRITICAL' WHEN migs.avg_user_impact>=50 THEN 'WARNING' ELSE 'INFO' END AS Severity,
                'CREATE NONCLUSTERED INDEX [IX_'+OBJECT_NAME(mid.object_id,mid.database_id)+'_Missing'+CAST(mid.index_handle AS VARCHAR)+'] ON '+
                QUOTENAME(OBJECT_SCHEMA_NAME(mid.object_id,mid.database_id))+'.'+QUOTENAME(OBJECT_NAME(mid.object_id,mid.database_id))+' ('+
                ISNULL(mid.equality_columns,'')+
                CASE WHEN mid.equality_columns IS NOT NULL AND mid.inequality_columns IS NOT NULL THEN ',' ELSE '' END+
                ISNULL(mid.inequality_columns,'')+')'+ 
                CASE WHEN mid.included_columns IS NOT NULL THEN ' INCLUDE ('+mid.included_columns+')' ELSE '' END+';' AS CreateIndexStatement,
                GETDATE() AS CaptureTime
            FROM sys.dm_db_missing_index_details mid
            JOIN sys.dm_db_missing_index_groups mig ON mid.index_handle=mig.index_handle
            JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle=migs.group_handle
            WHERE mid.database_id=DB_ID()
            ORDER BY ImpactScore DESC
            """;

        const string unusedSql = """
            SELECT DB_NAME() AS DatabaseName,
                OBJECT_SCHEMA_NAME(ix.object_id) AS SchemaName,
                OBJECT_NAME(ix.object_id) AS TableName,
                ix.name AS IndexName, ix.type_desc AS IndexType,
                CAST(ix.is_unique AS BIT) AS IsUnique,
                CAST(ix.is_primary_key AS BIT) AS IsPrimaryKey,
                CAST(ix.is_unique_constraint AS BIT) AS IsUniqueConstraint,
                ISNULL(ius.user_seeks,0) AS UserSeeks,
                ISNULL(ius.user_scans,0) AS UserScans,
                ISNULL(ius.user_lookups,0) AS UserLookups,
                ISNULL(ius.user_updates,0) AS UserUpdates,
                ius.last_user_seek AS LastUserSeek,
                ius.last_user_scan AS LastUserScan,
                ius.last_user_update AS LastUserUpdate,
                ISNULL(ius.user_seeks+ius.user_scans+ius.user_lookups,0) AS TotalReads,
                SUM(p.rows) AS TableRows,
                GETDATE() AS CaptureTime
            FROM sys.indexes ix
            JOIN sys.objects o ON ix.object_id=o.object_id
            LEFT JOIN sys.dm_db_index_usage_stats ius
                ON ix.object_id=ius.object_id AND ix.index_id=ius.index_id AND ius.database_id=DB_ID()
            LEFT JOIN sys.partitions p ON ix.object_id=p.object_id AND ix.index_id=p.index_id
            WHERE o.type='U' AND ix.type>0 AND ix.is_primary_key=0 AND ix.is_unique_constraint=0
              AND ISNULL(ius.user_seeks+ius.user_scans+ius.user_lookups,0)=0
              AND ISNULL(ius.user_updates,0)>0
            GROUP BY ix.object_id,ix.name,ix.type_desc,ix.is_unique,ix.is_primary_key,ix.is_unique_constraint,
                ius.user_seeks,ius.user_scans,ius.user_lookups,ius.user_updates,
                ius.last_user_seek,ius.last_user_scan,ius.last_user_update
            ORDER BY ISNULL(ius.user_updates,0) DESC
            """;

        using var conn = CreateConnection();
        var missing = (await Q(conn, missingSql)).Select(r => new MissingIndex(
            (string)r.DatabaseName, (string)r.SchemaName, (string)r.TableName,
            (string?)r.EqualityColumns, (string?)r.InequalityColumns, (string?)r.IncludedColumns,
            (long)r.UniqueCompiles, (long)r.UserSeeks, (long)r.UserScans,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan,
            (double)r.AvgTotalUserCost, (double)r.AvgUserImpact, (double)r.ImpactScore,
            (string)r.Severity, (string)r.CreateIndexStatement, (DateTime)r.CaptureTime));

        var unused = (await Q(conn, unusedSql)).Select(r => new UnusedIndex(
            (string)r.DatabaseName, (string)r.SchemaName, (string)r.TableName,
            (string)r.IndexName, (string)r.IndexType,
            (bool)r.IsUnique, (bool)r.IsPrimaryKey, (bool)r.IsUniqueConstraint,
            (long)r.UserSeeks, (long)r.UserScans, (long)r.UserLookups, (long)r.UserUpdates,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan, (DateTime?)r.LastUserUpdate,
            (long)r.TotalReads, (long)r.TableRows, (DateTime)r.CaptureTime));

        return (missing, unused);
    }

    // ── 12. Plan cache ────────────────────────────────────────────────
    public async Task<(IEnumerable<PlanCacheTypeSummary>, IEnumerable<PlanCacheCounter>, IEnumerable<PlanCacheTopQuery>)> GetPlanCacheAsync()
    {
        const string summarySql = """
            SELECT cp.objtype AS CacheType, COUNT(*) AS PlanCount,
                SUM(cp.size_in_bytes)/1024/1024 AS TotalSizeMB,
                AVG(CAST(cp.usecounts AS FLOAT)) AS AvgUseCounts,
                SUM(CASE WHEN cp.usecounts=1 THEN 1 ELSE 0 END) AS SingleUsePlans,
                CAST(SUM(CASE WHEN cp.usecounts=1 THEN 1 ELSE 0 END)*100.0/NULLIF(COUNT(*),0) AS DECIMAL(10,1)) AS SingleUsePct,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_cached_plans cp
            GROUP BY cp.objtype ORDER BY TotalSizeMB DESC
            """;

        const string countersSql = """
            SELECT counter_name AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%SQL Statistics%'
              AND counter_name IN ('SQL Compilations/sec','SQL Re-Compilations/sec',
                  'Batch Requests/sec','Auto-Param Attempts/sec','Failed Auto-Params/sec',
                  'Safe Auto-Params/sec','Unsafe Auto-Params/sec')
            ORDER BY counter_name
            """;

        const string topSql = """
            SELECT TOP 20
                qs.execution_count AS ExecutionCount,
                qs.total_worker_time/1000 AS TotalCpuMs,
                qs.total_elapsed_time/1000 AS TotalElapsedMs,
                CAST(qs.total_worker_time*1.0/NULLIF(qs.execution_count,0)/1000 AS DECIMAL(18,2)) AS AvgCpuMs,
                CAST(qs.total_elapsed_time*1.0/NULLIF(qs.execution_count,0)/1000 AS DECIMAL(18,2)) AS AvgElapsedMs,
                qs.total_logical_reads AS TotalLogicalReads,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.plan_generation_num AS PlanGenerations,
                cp.objtype AS PlanType, cp.size_in_bytes/1024 AS PlanSizeKB,
                cp.usecounts AS UseCounts,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(st.dbid) AS DatabaseName,
                SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_query_stats qs
            JOIN sys.dm_exec_cached_plans cp ON qs.plan_handle=cp.plan_handle
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.dbid = DB_ID()
            ORDER BY qs.execution_count DESC
            """;

        using var conn = CreateConnection();
        var summary = (await Q(conn, summarySql)).Select(r =>
        {
            double singlePct = (double)(r.SingleUsePct ?? 0);
            return new PlanCacheTypeSummary(
                (string)r.CacheType, (int)r.PlanCount, (long)r.TotalSizeMB,
                (double)r.AvgUseCounts, (int)r.SingleUsePlans, singlePct,
                (DateTime)r.CaptureTime,
                singlePct > 40 ? "WARNING" : "OK");
        });

        var counters = (await Q(conn, countersSql)).Select(r =>
            new PlanCacheCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        var top = (await Q(conn, topSql)).Select(r => new PlanCacheTopQuery(
            (long)r.ExecutionCount, (long)r.TotalCpuMs, (long)r.TotalElapsedMs,
            (double)r.AvgCpuMs, (double)r.AvgElapsedMs,
            (long)r.TotalLogicalReads, (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (int)r.PlanGenerations, (string)r.PlanType, (long)r.PlanSizeKB,
            (long)r.UseCounts, (DateTime)r.PlanCreationTime,
            (string?)r.DatabaseName, (string?)r.QueryText, (DateTime)r.CaptureTime));

        return (summary, counters, top);
    }

    // ── 13 + 14. Resource-intensive queries (logical reads + CPU) ─────
    public async Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
                       IEnumerable<ResourceIntensiveQuery> ByCpu,
                       IEnumerable<ResourceIntensiveQuery> ByHighestReads)>
        GetResourceIntensiveQueriesAsync(int topN = 20)
    {
        const string objectTypeSql = """
            CASE
                WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
                ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                              WHEN 'FN' THEN 'Scalar Func'
                                              WHEN 'TF' THEN 'Table Func'
                                              WHEN 'IF' THEN 'Inline Func'
                                              WHEN 'V'  THEN 'View'
                                              WHEN 'TR' THEN 'Trigger'
                                              ELSE type_desc END
                             FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
            END AS ObjectType
            """;
        _ = objectTypeSql; // used inline in the SQL literals below

        const string readsSql = """
            SELECT TOP (@TopN)
                SUBSTRING(qt.text,(qs.statement_start_offset/2)+1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset)/2)+1) AS QueryText,
                qs.execution_count AS ExecutionCount,
                qs.total_logical_reads AS TotalLogicalReads,
                CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
                qs.total_worker_time AS TotalCPUTime,
                CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
                qs.total_elapsed_time AS TotalElapsedTime,
                CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.last_execution_time AS LastExecutionTime,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(qt.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
                GETDATE() AS CaptureTime,
                CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
                CASE
                    WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
                    ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                                  WHEN 'FN' THEN 'Scalar Func'
                                                  WHEN 'TF' THEN 'Table Func'
                                                  WHEN 'IF' THEN 'Inline Func'
                                                  WHEN 'V'  THEN 'View'
                                                  WHEN 'TR' THEN 'Trigger'
                                                  ELSE type_desc END
                                 FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
                END AS ObjectType
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE qt.dbid = DB_ID()
            ORDER BY qs.total_logical_reads DESC
            """;

        const string cpuSql = """
            SELECT TOP (@TopN)
                SUBSTRING(qt.text,(qs.statement_start_offset/2)+1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset)/2)+1) AS QueryText,
                qs.execution_count AS ExecutionCount,
                qs.total_logical_reads AS TotalLogicalReads,
                CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
                qs.total_worker_time AS TotalCPUTime,
                CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
                qs.total_elapsed_time AS TotalElapsedTime,
                CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.last_execution_time AS LastExecutionTime,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(qt.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
                GETDATE() AS CaptureTime,
                CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
                CASE
                    WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
                    ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                                  WHEN 'FN' THEN 'Scalar Func'
                                                  WHEN 'TF' THEN 'Table Func'
                                                  WHEN 'IF' THEN 'Inline Func'
                                                  WHEN 'V'  THEN 'View'
                                                  WHEN 'TR' THEN 'Trigger'
                                                  ELSE type_desc END
                                 FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
                END AS ObjectType
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE qt.dbid = DB_ID()
            ORDER BY qs.total_worker_time DESC
            """;

        const string highestReadsSql = """
            SELECT TOP (@TopN)
                st.text AS QueryText,
                qs.execution_count AS ExecutionCount,
                qs.total_logical_reads AS TotalLogicalReads,
                CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
                qs.total_worker_time AS TotalCPUTime,
                CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
                qs.total_elapsed_time AS TotalElapsedTime,
                CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.last_execution_time AS LastExecutionTime,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(st.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(st.objectid,st.dbid),'') AS ObjectName,
                GETDATE() AS CaptureTime,
                CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
                qs.last_logical_reads AS LastLogicalReads,
                CASE
                    WHEN st.objectid IS NULL THEN 'Ad-Hoc'
                    ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                                  WHEN 'FN' THEN 'Scalar Func'
                                                  WHEN 'TF' THEN 'Table Func'
                                                  WHEN 'IF' THEN 'Inline Func'
                                                  WHEN 'V'  THEN 'View'
                                                  WHEN 'TR' THEN 'Trigger'
                                                  ELSE type_desc END
                                 FROM sys.objects WHERE object_id = st.objectid), 'Ad-Hoc')
                END AS ObjectType
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE st.dbid = DB_ID()
            ORDER BY qs.total_logical_reads DESC
            """;

        static ResourceIntensiveQuery Map(dynamic r) => new(
            (string?)r.QueryText, (string?)r.DatabaseName, (string?)r.ObjectName,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads, (long)r.AvgLogicalReads,
            (long)r.TotalCPUTime,      (long)r.AvgCPUTime,
            (long)r.TotalElapsedTime,  (long)r.AvgElapsedTime,
            (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (DateTime)r.LastExecutionTime, (DateTime)r.PlanCreationTime,
            (DateTime)r.CaptureTime, (string?)r.QueryPlan, null, (string?)r.ObjectType);

        static ResourceIntensiveQuery MapHighestReads(dynamic r) => new(
            (string?)r.QueryText, (string?)r.DatabaseName, (string?)r.ObjectName,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads, (long)r.AvgLogicalReads,
            (long)r.TotalCPUTime,      (long)r.AvgCPUTime,
            (long)r.TotalElapsedTime,  (long)r.AvgElapsedTime,
            (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (DateTime)r.LastExecutionTime, (DateTime)r.PlanCreationTime,
            (DateTime)r.CaptureTime,
            (string?)r.QueryPlan, (long?)r.LastLogicalReads, (string?)r.ObjectType);

        var p = new { TopN = topN };
        using var conn = CreateConnection();
        // These queries scan dm_exec_query_stats and fetch plans, which can be slow.
        // Increase timeout to 2 minutes to avoid timeouts on busy servers.
        const int extendedTimeout = 120;

        var byReads        = (await Q(conn, readsSql,        p, extendedTimeout)).Select(r => Map(r));
        var byCpu          = (await Q(conn, cpuSql,          p, extendedTimeout)).Select(r => Map(r));
        var byHighestReads = (await Q(conn, highestReadsSql, p, extendedTimeout)).Select(r => MapHighestReads(r));
        return (byReads, byCpu, byHighestReads);
    }

    public async Task<string?> GetObjectQueryPlanAsync(string objectName)
    {
        const string sql = """
            SELECT TOP 1 CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan
            FROM (
                SELECT plan_handle FROM sys.dm_exec_procedure_stats
                WHERE object_id = OBJECT_ID(@ObjectName) AND database_id = DB_ID()
                UNION ALL
                SELECT plan_handle FROM sys.dm_exec_function_stats
                WHERE object_id = OBJECT_ID(@ObjectName) AND database_id = DB_ID()
            ) x
            CROSS APPLY sys.dm_exec_query_plan(x.plan_handle) qp
            WHERE qp.query_plan IS NOT NULL
            """;
        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { ObjectName = objectName });
        return r is null ? null : (string?)r.QueryPlan;
    }

    // ── 15. Index usage patterns ──────────────────────────────────────
    public async Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync()
    {
        const string sql = """
            SELECT OBJECT_NAME(s.object_id) AS TableName,
                i.name AS IndexName, i.type_desc AS IndexType,
                ISNULL(s.user_seeks,0)   AS UserSeeks,
                ISNULL(s.user_scans,0)   AS UserScans,
                ISNULL(s.user_lookups,0) AS UserLookups,
                ISNULL(s.user_updates,0) AS UserUpdates,
                s.last_user_seek   AS LastUserSeek,
                s.last_user_scan   AS LastUserScan,
                s.last_user_lookup AS LastUserLookup,
                s.last_user_update AS LastUserUpdate,
                CASE
                    WHEN ISNULL(s.user_seeks,0)+ISNULL(s.user_scans,0)+ISNULL(s.user_lookups,0) = 0 THEN 'Unused'
                    WHEN ISNULL(s.user_scans,0) > ISNULL(s.user_seeks,0) THEN 'Mostly Scanned - Review'
                    ELSE 'Healthy'
                END AS IndexHealth,
                DB_NAME(DB_ID()) AS DatabaseName, GETDATE() AS CaptureTime
            FROM sys.indexes i
            JOIN sys.objects o ON i.object_id = o.object_id
            LEFT JOIN sys.dm_db_index_usage_stats s
                ON i.object_id = s.object_id AND i.index_id = s.index_id AND s.database_id = DB_ID()
            WHERE o.type = 'U' AND i.type > 0
            ORDER BY ISNULL(s.user_seeks,0)+ISNULL(s.user_scans,0)+ISNULL(s.user_lookups,0) DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new IndexUsagePattern(
            (string)r.TableName, (string?)r.IndexName, (string)r.IndexType,
            (long)r.UserSeeks, (long)r.UserScans, (long)r.UserLookups, (long)r.UserUpdates,
            (DateTime?)r.LastUserSeek, (DateTime?)r.LastUserScan,
            (DateTime?)r.LastUserLookup, (DateTime?)r.LastUserUpdate,
            (string)r.IndexHealth, (string)r.DatabaseName, (DateTime)r.CaptureTime));
    }

    // ── 16. Index fragmentation ───────────────────────────────────────
    public async Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000)
    {
        const string sql = """
            SELECT SCHEMA_NAME(o.schema_id) AS SchemaName,
                OBJECT_NAME(ips.object_id) AS TableName, i.name AS IndexName,
                ips.index_type_desc AS IndexType,
                ips.avg_fragmentation_in_percent AS FragmentationPercent,
                ips.page_count AS PageCount,
                ips.avg_page_space_used_in_percent AS AvgPageSpaceUsed,
                ips.record_count AS RecordCount,
                DB_NAME(ips.database_id) AS DatabaseName,
                CASE
                    WHEN ips.avg_fragmentation_in_percent < 10 THEN 'No Action Needed'
                    WHEN ips.avg_fragmentation_in_percent < 30 THEN 'Reorganize'
                    ELSE 'Rebuild'
                END AS RecommendedAction,
                GETDATE() AS CaptureTime
            FROM sys.dm_db_index_physical_stats(DB_ID(),NULL,NULL,NULL,'LIMITED') ips
            INNER JOIN sys.indexes i  ON ips.object_id = i.object_id AND ips.index_id = i.index_id
            INNER JOIN sys.objects o  ON ips.object_id = o.object_id
            WHERE o.type = 'U'
              AND ips.avg_fragmentation_in_percent >= @MinFrag
              AND ips.page_count >= @MinPageCount
            ORDER BY ips.avg_fragmentation_in_percent DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql, new { MinFrag = minFragmentationPercent, MinPageCount = minPageCount })).Select(r =>
        {
            var row = (IDictionary<string, object>)r;
            object? Get(string k) => row.TryGetValue(k, out var v) && v is not DBNull ? v : null;
            return new IndexFragmentation(
                (string)r.SchemaName, (string)r.TableName, (string?)r.IndexName, (string)r.IndexType,
                Convert.ToDouble(Get("FragmentationPercent") ?? 0.0),
                Convert.ToInt64(Get("PageCount") ?? 0L),
                Convert.ToDouble(Get("AvgPageSpaceUsed") ?? 0.0),
                Convert.ToInt64(Get("RecordCount") ?? 0L),
                (string)r.DatabaseName, (string)r.RecommendedAction, (DateTime)r.CaptureTime);
        });
    }

    // ── 17. Implicit conversions ──────────────────────────────────────
    public async Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync()
    {
        const string sql = """
            SELECT TOP 50
                DB_NAME(qt.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
                cp.usecounts AS ExecutionCount,
                cp.size_in_bytes/1024 AS PlanSizeKB,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) qt
            WHERE CAST(qp.query_plan AS NVARCHAR(MAX)) LIKE '%CONVERT_IMPLICIT%'
            ORDER BY cp.usecounts DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new ImplicitConversion(
            (string?)r.DatabaseName, (string?)r.ObjectName,
            (int)r.ExecutionCount, (long)r.PlanSizeKB,
            (string?)r.QueryText, (DateTime)r.CaptureTime));
    }

    // ── 18. Stale statistics ──────────────────────────────────────────
    public async Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync()
    {
        const string sql = """
            SELECT OBJECT_NAME(s.object_id) AS TableName, s.name AS StatisticsName,
                STATS_DATE(s.object_id,s.stats_id) AS LastUpdated,
                DATEDIFF(DAY,STATS_DATE(s.object_id,s.stats_id),GETDATE()) AS DaysOld,
                sp.rows AS RowsInTable, sp.rows_sampled AS RowsSampled,
                CAST(sp.rows_sampled*100.0/NULLIF(sp.rows,0) AS DECIMAL(10,1)) AS SamplePct,
                sp.modification_counter AS ModificationsSinceLastUpdate,
                s.is_incremental AS IsIncremental, s.auto_created AS AutoCreated,
                s.user_created AS UserCreated, DB_NAME() AS DatabaseName,
                GETDATE() AS CaptureTime
            FROM sys.stats s
            CROSS APPLY sys.dm_db_stats_properties(s.object_id,s.stats_id) sp
            WHERE OBJECTPROPERTY(s.object_id,'IsUserTable')=1 AND sp.modification_counter>0
            ORDER BY sp.modification_counter DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new StaleStatistic(
            (string)r.TableName, (string)r.StatisticsName,
            (DateTime?)r.LastUpdated, (int)r.DaysOld,
            (long)r.RowsInTable, (long)r.RowsSampled, (double?)r.SamplePct ?? 0,
            (long)r.ModificationsSinceLastUpdate,
            (bool)r.IsIncremental, (bool)r.AutoCreated, (bool)r.UserCreated,
            (string)r.DatabaseName, (DateTime)r.CaptureTime));
    }

    // ── 19. Database storage & configuration ─────────────────────────
    // Detects Azure SQL (EngineEdition 5/8) and switches to compatible queries.
    // On-prem uses sys.master_files for all DBs; Azure uses sys.database_files
    // for the current DB only and skips the cross-DB tempdb query.
    public async Task<(
        IEnumerable<ServerConfigParam>  Config,
        IEnumerable<DatabaseFile>       Files,
        IEnumerable<TempDbFileSummary>  TempDbFiles,
        IEnumerable<TableStorage>       TopTables
    )> GetDatabaseStorageAsync()
    {
        // ── Detect engine edition: 5 = Azure SQL DB, 8 = Azure SQL MI ─
        const string sqlEdition = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition";

        // ── A) Server / database-scoped config ────────────────────────
        // On-prem: sys.configurations (server-wide)
        // Azure SQL DB: sys.database_scoped_configurations (DB-level only)
        const string sqlConfigOnPrem = """
            SELECT name                       AS ParameterName,
                   CAST(value_in_use AS BIGINT) AS CurrentValue,
                   CAST(minimum      AS BIGINT) AS MinValue,
                   CAST(maximum      AS BIGINT) AS MaxValue,
                   description                AS Description,
                   CAST(is_dynamic   AS BIT)  AS IsDynamic,
                   CAST(is_advanced  AS BIT)  AS IsAdvanced
            FROM sys.configurations
            WHERE name IN (
                'max degree of parallelism','cost threshold for parallelism',
                'max server memory (MB)','min server memory (MB)',
                'optimize for ad hoc workloads','max worker threads',
                'lightweight pooling','priority boost','fill factor (%)',
                'index create memory (KB)','min memory per query (KB)',
                'query wait (s)','remote query timeout (s)','locks',
                'open objects','user connections','backup compression default',
                'Database Mail XPs','xp_cmdshell'
            )
            ORDER BY name
            """;

        const string sqlConfigAzure = """
            SELECT name                                              AS ParameterName,
                   CASE
                       WHEN ISNUMERIC(CAST(value AS NVARCHAR(256))) = 1
                       THEN CAST(CAST(value AS NVARCHAR(256)) AS BIGINT)
                       ELSE 0
                   END                                              AS CurrentValue,
                   CAST(0  AS BIGINT)                               AS MinValue,
                   CAST(0  AS BIGINT)                               AS MaxValue,
                   CAST(ISNULL(CAST(value AS NVARCHAR(256)),'')
                        AS NVARCHAR(MAX))                           AS Description,
                   CAST(1  AS BIT)                                  AS IsDynamic,
                   CAST(0  AS BIT)                                  AS IsAdvanced
            FROM sys.database_scoped_configurations
            ORDER BY name
            """;

        // ── B) Database files ─────────────────────────────────────────
        // On-prem: join sys.databases + sys.master_files (all databases)
        // Azure: sys.database_files for current DB only
        const string sqlFilesOnPrem = """
            SELECT
                d.name                              AS DatabaseName,
                d.state_desc                        AS State,
                d.recovery_model_desc               AS RecoveryModel,
                d.compatibility_level               AS CompatibilityLevel,
                d.collation_name                    AS Collation,
                CAST(d.is_auto_shrink_on       AS BIT) AS AutoShrink,
                CAST(d.is_auto_create_stats_on AS BIT) AS AutoCreateStats,
                CAST(d.is_auto_update_stats_on AS BIT) AS AutoUpdateStats,
                CAST(d.is_query_store_on       AS BIT) AS QueryStoreOn,
                CAST(d.is_cdc_enabled          AS BIT) AS CDCEnabled,
                CAST(d.is_encrypted            AS BIT) AS Encrypted,
                f.type_desc                         AS FileType,
                f.name                              AS LogicalFileName,
                f.physical_name                     AS PhysicalPath,
                CAST(f.size * 8.0 / 1024 AS DECIMAL(18,2))                                                       AS FileSizeMB,
                CAST(ISNULL(FILEPROPERTY(f.name,'SpaceUsed'),0) * 8.0/1024 AS DECIMAL(18,2))                     AS SpaceUsedMB,
                CAST((f.size - ISNULL(FILEPROPERTY(f.name,'SpaceUsed'),0)) * 8.0/1024 AS DECIMAL(18,2))          AS FreeSpaceMB,
                CASE WHEN f.max_size = -1 THEN CAST(-1 AS DECIMAL(18,2))
                     ELSE CAST(f.max_size * 8.0/1024 AS DECIMAL(18,2)) END                                        AS MaxSizeMB,
                CASE WHEN f.is_percent_growth = 1
                     THEN CAST(f.growth AS VARCHAR(10)) + '%'
                     ELSE CAST(f.growth * 8 / 1024 AS VARCHAR(10)) + ' MB'
                END AS AutoGrowth
            FROM sys.databases d
            JOIN sys.master_files f ON d.database_id = f.database_id
            WHERE d.state_desc = 'ONLINE'
            ORDER BY d.name, f.type_desc, f.name
            """;

        const string sqlFilesAzure = """
            SELECT
                DB_NAME()                              AS DatabaseName,
                'ONLINE'                               AS State,
                CAST(DATABASEPROPERTYEX(DB_NAME(),'Recovery')  AS NVARCHAR(60))  AS RecoveryModel,
                CAST(d.compatibility_level             AS INT)  AS CompatibilityLevel,
                CAST(DATABASEPROPERTYEX(DB_NAME(),'Collation') AS NVARCHAR(128)) AS Collation,
                CAST(0 AS BIT)                         AS AutoShrink,
                CAST(1 AS BIT)                         AS AutoCreateStats,
                CAST(1 AS BIT)                         AS AutoUpdateStats,
                CAST(d.is_query_store_on          AS BIT) AS QueryStoreOn,
                CAST(0 AS BIT)                         AS CDCEnabled,
                CAST(0 AS BIT)                         AS Encrypted,
                f.type_desc                            AS FileType,
                f.name                                 AS LogicalFileName,
                ISNULL(f.physical_name,'')             AS PhysicalPath,
                CAST(f.size * 8.0 / 1024 AS DECIMAL(18,2))                                                       AS FileSizeMB,
                CAST(ISNULL(FILEPROPERTY(f.name,'SpaceUsed'),0) * 8.0/1024 AS DECIMAL(18,2))                     AS SpaceUsedMB,
                CAST((f.size - ISNULL(FILEPROPERTY(f.name,'SpaceUsed'),0)) * 8.0/1024 AS DECIMAL(18,2))          AS FreeSpaceMB,
                CASE WHEN f.max_size = -1 THEN CAST(-1 AS DECIMAL(18,2))
                     ELSE CAST(f.max_size * 8.0/1024 AS DECIMAL(18,2)) END                                        AS MaxSizeMB,
                CASE WHEN f.is_percent_growth = 1
                     THEN CAST(f.growth AS VARCHAR(10)) + '%'
                     ELSE CAST(f.growth * 8 / 1024 AS VARCHAR(10)) + ' MB'
                END AS AutoGrowth
            FROM sys.database_files f
            CROSS JOIN (SELECT compatibility_level, is_query_store_on
                        FROM sys.databases WHERE name = DB_NAME()) d
            ORDER BY f.type_desc, f.name
            """;

        // ── C) TempDB files ───────────────────────────────────────────
        // Azure SQL DB: tempdb cross-DB query not allowed — skip
        const string sqlTempDb = """
            SELECT
                name          AS FileName,
                type_desc     AS FileType,
                physical_name AS PhysicalPath,
                CAST(size * 8.0/1024 AS DECIMAL(18,2))                                                    AS SizeMB,
                CAST(ISNULL(FILEPROPERTY(name,'SpaceUsed'),0) * 8.0/1024 AS DECIMAL(18,2))                AS UsedMB,
                CAST((size - ISNULL(FILEPROPERTY(name,'SpaceUsed'),0)) * 8.0/1024 AS DECIMAL(18,2))       AS FreeMB
            FROM tempdb.sys.database_files
            ORDER BY type_desc, name
            """;

        // ── D) Top 20 tables by storage (same on both) ────────────────
        const string sqlTables = """
            SELECT TOP 20
                SCHEMA_NAME(t.schema_id)                                                   AS SchemaName,
                t.name                                                                     AS TableName,
                p.rows                                                                     AS RowsCount,
                CAST(SUM(CASE WHEN i.type <= 1 THEN a.total_pages ELSE 0 END) * 8.0/1024 AS DECIMAL(18,3)) AS DataSizeMB,
                CAST(SUM(CASE WHEN i.type  > 1 THEN a.total_pages ELSE 0 END) * 8.0/1024 AS DECIMAL(18,3)) AS IndexSizeMB,
                CAST(SUM(a.total_pages)  * 8.0/1024 AS DECIMAL(18,3))                     AS TotalSizeMB,
                CAST(SUM(a.used_pages)   * 8.0/1024 AS DECIMAL(18,3))                     AS UsedSizeMB,
                CAST((SUM(a.total_pages)-SUM(a.used_pages)) * 8.0/1024 AS DECIMAL(18,3))  AS UnusedSizeMB,
                COUNT(DISTINCT i.index_id) - 1                                             AS IndexCount,
                MAX(CASE WHEN h.object_id IS NOT NULL THEN 'Heap' ELSE 'Clustered' END)   AS TableType
            FROM sys.tables t
            JOIN sys.indexes i           ON t.object_id = i.object_id
            JOIN sys.partitions p        ON i.object_id = p.object_id AND i.index_id = p.index_id
            JOIN sys.allocation_units a  ON p.partition_id = a.container_id
            LEFT JOIN (SELECT DISTINCT object_id FROM sys.indexes WHERE type = 0) h
                                         ON h.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            GROUP BY SCHEMA_NAME(t.schema_id), t.name, p.rows
            ORDER BY SUM(a.total_pages) DESC
            """;

        using var conn = CreateConnection();

        // Detect Azure SQL (EngineEdition 5 = Azure SQL DB, 8 = Azure SQL MI)
        var editionRow = await QFirst(conn, sqlEdition);
        int edition = editionRow != null ? (int)editionRow.Edition : 0;
        bool isAzureSqlDb = edition == 5;   // Managed Instance (8) still has master_files

        // Config
        var configSql = isAzureSqlDb ? sqlConfigAzure : sqlConfigOnPrem;
        var config = (await Q(conn, configSql)).Select(r => new ServerConfigParam(
            (string)r.ParameterName,
            Convert.ToInt64(r.CurrentValue),
            Convert.ToInt64(r.MinValue),
            Convert.ToInt64(r.MaxValue),
            r.Description == null || r.Description is DBNull ? "" : (string)r.Description,
            Convert.ToBoolean(r.IsDynamic),
            Convert.ToBoolean(r.IsAdvanced)));

        // Files
        var filesSql = isAzureSqlDb ? sqlFilesAzure : sqlFilesOnPrem;
        var files = (await Q(conn, filesSql)).Select(r => new DatabaseFile(
            (string)r.DatabaseName,
            (string)r.State,
            r.RecoveryModel  == null || r.RecoveryModel  is DBNull ? "" : (string)r.RecoveryModel,
            Convert.ToInt32(r.CompatibilityLevel),
            r.Collation == null || r.Collation is DBNull ? null : (string)r.Collation,
            Convert.ToBoolean(r.AutoShrink),
            Convert.ToBoolean(r.AutoCreateStats),
            Convert.ToBoolean(r.AutoUpdateStats),
            Convert.ToBoolean(r.QueryStoreOn),
            Convert.ToBoolean(r.CDCEnabled),
            Convert.ToBoolean(r.Encrypted),
            (string)r.FileType,
            (string)r.LogicalFileName,
            r.PhysicalPath == null || r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath,
            Convert.ToDecimal(r.FileSizeMB),
            Convert.ToDecimal(r.SpaceUsedMB),
            Convert.ToDecimal(r.FreeSpaceMB),
            Convert.ToDecimal(r.MaxSizeMB),
            (string)r.AutoGrowth));

        // ── C) TempDB — skip on Azure SQL DB
        IEnumerable<TempDbFileSummary> tempFiles = [];
        if (!isAzureSqlDb)
        {
            tempFiles = (await Q(conn, sqlTempDb)).Select(r => new TempDbFileSummary(
                (string)r.FileName,
                (string)r.FileType,
                r.PhysicalPath == null || r.PhysicalPath is DBNull ? "" : (string)r.PhysicalPath,
                Convert.ToDecimal(r.SizeMB),
                Convert.ToDecimal(r.UsedMB),
                Convert.ToDecimal(r.FreeMB)));
        }

        // ── D) Top 20 tables by storage (same on both) ────────────────
        var tables = (await Q(conn, sqlTables)).Select(r => new TableStorage(
            (string)r.SchemaName,
            (string)r.TableName,
            Convert.ToInt64(r.RowsCount),
            Convert.ToDecimal(r.DataSizeMB),
            Convert.ToDecimal(r.IndexSizeMB),
            Convert.ToDecimal(r.TotalSizeMB),
            Convert.ToDecimal(r.UsedSizeMB),
            Convert.ToDecimal(r.UnusedSizeMB),
            Convert.ToInt32(r.IndexCount),
            (string)r.TableType));

        return (config, files, tempFiles, tables);
    }

    // ── 20. Process map (Active sessions + blocking) ─────────────────
    public async Task<IEnumerable<ProcessNode>> GetProcessesAsync()
    {
        const string sql = """
            SELECT s.session_id AS SessionId,
                r.blocking_session_id AS BlockingSessionId,
                s.login_name AS LoginName,
                s.host_name AS HostName,
                s.program_name AS ProgramName,
                ISNULL(r.status, s.status) AS Status,
                CASE WHEN s.session_id = @@SPID THEN 'Current' ELSE 'User' END AS Type,
                r.wait_type AS WaitType,
                r.last_wait_type AS LastWaitType,
                r.wait_resource AS WaitResource,
                r.wait_time AS WaitTimeMs,
                r.cpu_time AS CpuTimeMs,
                r.total_elapsed_time AS TotalElapsedMs,
                r.reads AS LogicalReads,
                ISNULL(r.reads,0) + ISNULL(r.writes,0) AS PhysicalIo,
                s.memory_usage AS MemoryUsage,
                s.login_time AS LoginTime,
                s.last_request_end_time AS LastBatch,
                r.open_transaction_count AS OpenTran,
                r.command AS Command,
                DB_NAME(r.database_id) AS DatabaseName,
                ISNULL(qt.text, '') AS QueryText
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
            WHERE s.is_user_process = 1
            """;

        using var conn = CreateConnection();
        var rows = await Q(conn, sql);
        return rows.Select(r => new ProcessNode(
            (int)r.SessionId,
            (int)(r.BlockingSessionId ?? 0),
            (string)r.Status,
            (string?)r.LoginName,
            (string?)r.HostName,
            (string?)r.ProgramName,
            (string?)r.WaitType,
            (long)(r.WaitTimeMs ?? 0),
            (string?)r.LastWaitType,
            (string?)r.WaitResource,
            (string?)r.DatabaseName,
            (long)(r.CpuTimeMs ?? 0),
            (long)(r.PhysicalIo ?? 0),
            (long)(r.MemoryUsage ?? 0),
            (DateTime)(r.LoginTime ?? DateTime.MinValue),
            (DateTime)(r.LastBatch ?? DateTime.MinValue),
            (int)(r.OpenTran ?? 0),
            (string?)r.Command,
            (string?)r.QueryText,
             (int)(r.BlockingSessionId ?? 0) > 0 ? "Blocked"
                : (string)r.Status == "sleeping" ? "Sleeping"
                : "Active"
        ));
    }

    // ── 21. Live metrics dashboard (perf counters + ring buffers) ────
    public async Task<LiveMetricSnapshot> GetLiveMetricsAsync()
    {
        const string sql = """
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
                -- CPU (via ring buffer - approximates last minute avg)
                (SELECT TOP 1
                    x.xml_record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int')
                 FROM (
                    SELECT timestamp, CONVERT(XML, record) AS xml_record
                    FROM sys.dm_os_ring_buffers
                    WHERE ring_buffer_type = 'RING_BUFFER_SCHEDULER_MONITOR'
                   AND record LIKE '%<SystemHealth>%'
                 ) AS x
                 ORDER BY timestamp DESC) AS SqlCpuUtilizationPct,
                -- Cumulative specific waits (caller will diff these)
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('SOS_SCHEDULER_YIELD','THREADPOOL','CMEMTHREAD','CMEMPARTITIONED','EE_PMOLOCK','EXCHANGE','CXPACKET','CXCONSUMER','CXSYNC_PORT','CXSYNC_CONSUMER')) AS CpuWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('ASYNC_IO_COMPLETION','IO_COMPLETION','PAGEIOLATCH_SH','PAGEIOLATCH_EX','PAGEIOLATCH_UP','PAGEIOLATCH_NL','PAGEIOLATCH_DT','PAGEIOLATCH_KP','WRITELOG','WRITE_COMPLETION','IO_QUEUE_LIMIT','IO_RETRY','BACKUPIO','BACKUPBUFFER','BACKUPTHREAD','LOGMGR','LOGMGR_QUEUE','LOGMGR_FLUSH','LOGMGR_RESERVE_APPEND','LOG_RATE_GOVERNOR')) AS IoWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'LCK%') AS LockWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('RESOURCE_SEMAPHORE','RESOURCE_SEMAPHORE_QUERY_COMPILE','RESOURCE_SEMAPHORE_MUTEX','PAGELATCH_SH','PAGELATCH_EX','PAGELATCH_UP','PAGELATCH_NL','PAGELATCH_DT','PAGELATCH_KP','LATCH_SH','LATCH_EX','LATCH_NL','LATCH_DT','LATCH_KP','LATCH_UP','MEMORY_ALLOCATION_EXT','MEMORY_GRANT_UPDATE','SOS_MEMORY_USAGE_ADJUSTMENT','SOS_VIRTUALMEMORY_LOW')) AS MemoryWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type IN ('ASYNC_NETWORK_IO','NET_WAITFOR_PACKET','NETWORK_IO','OLEDB')) AS NetworkWaitMs,
                (SELECT SUM(wait_time_ms) FROM sys.dm_os_wait_stats WHERE wait_type NOT IN ('SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP','DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE','DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','DBMIRROR_EVENTS_QUEUED','DBMIRROR_WORKER_QUEUE','BROKER_EVENTHANDLER','BROKER_RECEIVE_WAITFOR','BROKER_TRANSMITTER','CLR_AUTO_EVENT','CLR_MANUAL_EVENT','CLR_SYNCHRONIZE','FT_IFTS_SCHEDULER_IDLE_WAIT','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','DEADLOCK_ENUM_MUTEX','WAIT_XTP_OFFLINE_CKPT_NEW_LOG','WAIT_XTP_RECOVERY','WAIT_XTP_HOST_WAIT','WAIT_XTP_CKPT_CLOSE','PARALLEL_REDO_DRAIN_WORKER','PARALLEL_REDO_LOG_CACHE','PARALLEL_REDO_TRAN_LIST','PARALLEL_REDO_WORKER_SYNC','PARALLEL_REDO_WORKER_WAIT_WORK','XE_DISPATCHER_JOIN','XE_DISPATCH_QUEUE_SEMAPHORE','SQLTRACE_WAIT_ENTRIES')) AS TotalWaitMs,
                GETDATE() AS CaptureTime
        """;

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql);
        if (r is null) throw new InvalidOperationException("Failed to capture metrics");

        long total  = (long)(r.TotalWaitMs ?? 0);
        long cpu    = (long)(r.CpuWaitMs ?? 0);
        long io     = (long)(r.IoWaitMs ?? 0);
        long lockWait   = (long)(r.LockWaitMs ?? 0);
        long mem    = (long)(r.MemoryWaitMs ?? 0);
        long net    = (long)(r.NetworkWaitMs ?? 0);
        long other  = total - (cpu + io + lockWait + mem + net);

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
            total, cpu, io, lockWait, mem, net, other
        );
    }

    // ── 22. Perfmon counters (live DMV snapshot) ─────────────────────
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
            ORDER BY object_name, counter_name
            """;

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

    // ── 21. Application connections ──────────────────────────────────
    public async Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync()
    {
        const string sql = """
            SELECT
                ISNULL(s.program_name, '') AS ApplicationName,
                COUNT(*)                   AS CurrentConnections,
                SUM(CASE WHEN r.session_id IS NOT NULL THEN 1 ELSE 0 END) AS ActiveRequests,
                SUM(s.cpu_time)            AS TotalCpuTimeMs,
                SUM(s.memory_usage) * 8    AS TotalMemoryKB,
                SUM(s.logical_reads)       AS TotalLogicalReads,
                SUM(s.reads)               AS TotalReads,
                SUM(s.writes)              AS TotalWrites,
                MIN(s.login_time)          AS FirstLoginTime,
                MAX(s.last_request_start_time) AS LastRequestTime,
                GETDATE()                  AS CaptureTime
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            WHERE s.is_user_process = 1
            GROUP BY s.program_name
            ORDER BY COUNT(*) DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r =>
            new ApplicationConnection(
                (string)(r.ApplicationName ?? ""),
                (int)r.CurrentConnections,
                (int)r.ActiveRequests,
                (long)r.TotalCpuTimeMs,
                (long)r.TotalMemoryKB,
                (long)r.TotalLogicalReads,
                (long)r.TotalReads,
                (long)r.TotalWrites,
                (DateTime)r.FirstLoginTime,
                (DateTime)r.LastRequestTime,
                (DateTime)r.CaptureTime));
    }

    // ── 23. Memory KPI snapshot ──────────────────────────────────────
    public async Task<MemorySnapshot> GetMemorySnapshotAsync()
    {
        // Column sources (all SQL versions):
        //   sys.dm_os_sys_info  (osi): physical_memory_kb; memory_model_desc added in SQL 2016
        //   sys.dm_os_sys_memory (osm): available_physical_memory_kb, total_page_file_kb,
        //                               available_page_file_kb, system_memory_state_desc
        //   sys.dm_os_performance_counters (pc): memory KB counters
        //
        // SQL 2016+ (v13+):  osi also exposes memory_model_desc — use it directly.
        // SQL 2012/2014 (v11-12): memory_model_desc NOT on osi → fall back to N'N/A'.
        // Azure SQL DB (EngineEdition 5): sys.dm_os_sys_memory not exposed → counters only.
        const string sqlOnPrem = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT
                osi.physical_memory_kb                                                                    AS PhysicalMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Total Server Memory (KB)'      THEN pc.cntr_value ELSE 0 END) AS TotalServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Target Server Memory (KB)'     THEN pc.cntr_value ELSE 0 END) AS TargetServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Database Cache Memory (KB)'    THEN pc.cntr_value ELSE 0 END) AS DatabaseCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'SQL Cache Memory (KB)'         THEN pc.cntr_value ELSE 0 END) AS SqlCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Granted Workspace Memory (KB)' THEN pc.cntr_value ELSE 0 END) AS GrantedWorkspaceMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Free Memory (KB)'              THEN pc.cntr_value ELSE 0 END) AS FreeMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Pending'         THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsPending,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Outstanding'     THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsOutstanding,
                osm.available_physical_memory_kb                                                          AS AvailablePhysicalKB,
                ISNULL(osm.total_page_file_kb, 0)                                                         AS TotalPageFileKB,
                ISNULL(osm.available_page_file_kb, 0)                                                     AS AvailablePageFileKB,
                ISNULL(osm.system_memory_state_desc, N'Unknown')                                          AS SystemMemoryState,
                ISNULL(osi.memory_model_desc, N'N/A')                                                     AS MemoryModel,
                GETDATE()                                                                                 AS CaptureTime
            FROM sys.dm_os_sys_info osi
            CROSS JOIN sys.dm_os_sys_memory osm
            CROSS JOIN sys.dm_os_performance_counters pc
            WHERE RTRIM(pc.object_name) LIKE N'%Memory Manager%'
              AND RTRIM(pc.counter_name) IN (
                N'Total Server Memory (KB)', N'Target Server Memory (KB)',
                N'Database Cache Memory (KB)', N'SQL Cache Memory (KB)',
                N'Granted Workspace Memory (KB)',
                N'Free Memory (KB)', N'Memory Grants Pending', N'Memory Grants Outstanding'
              )
            GROUP BY
                osi.physical_memory_kb, osi.memory_model_desc,
                osm.available_physical_memory_kb, osm.total_page_file_kb,
                osm.available_page_file_kb, osm.system_memory_state_desc;
            """;

        // SQL Server 2012/2014 (ProductMajorVersion 11-12): memory_model_desc not yet on
        // sys.dm_os_sys_info — use N'N/A' placeholder.  Everything else is identical.
        const string sqlOnPremLegacy = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT
                osi.physical_memory_kb                                                                    AS PhysicalMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Total Server Memory (KB)'      THEN pc.cntr_value ELSE 0 END) AS TotalServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Target Server Memory (KB)'     THEN pc.cntr_value ELSE 0 END) AS TargetServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Database Cache Memory (KB)'    THEN pc.cntr_value ELSE 0 END) AS DatabaseCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'SQL Cache Memory (KB)'         THEN pc.cntr_value ELSE 0 END) AS SqlCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Granted Workspace Memory (KB)' THEN pc.cntr_value ELSE 0 END) AS GrantedWorkspaceMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Free Memory (KB)'              THEN pc.cntr_value ELSE 0 END) AS FreeMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Pending'         THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsPending,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Outstanding'     THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsOutstanding,
                osm.available_physical_memory_kb                                                          AS AvailablePhysicalKB,
                ISNULL(osm.total_page_file_kb, 0)                                                         AS TotalPageFileKB,
                ISNULL(osm.available_page_file_kb, 0)                                                     AS AvailablePageFileKB,
                ISNULL(osm.system_memory_state_desc, N'Unknown')                                          AS SystemMemoryState,
                N'N/A'                                                                                    AS MemoryModel,
                GETDATE()                                                                                 AS CaptureTime
            FROM sys.dm_os_sys_info osi
            CROSS JOIN sys.dm_os_sys_memory osm
            CROSS JOIN sys.dm_os_performance_counters pc
            WHERE RTRIM(pc.object_name) LIKE N'%Memory Manager%'
              AND RTRIM(pc.counter_name) IN (
                N'Total Server Memory (KB)', N'Target Server Memory (KB)',
                N'Database Cache Memory (KB)', N'SQL Cache Memory (KB)',
                N'Granted Workspace Memory (KB)',
                N'Free Memory (KB)', N'Memory Grants Pending', N'Memory Grants Outstanding'
              )
            GROUP BY
                osi.physical_memory_kb,
                osm.available_physical_memory_kb, osm.total_page_file_kb,
                osm.available_page_file_kb, osm.system_memory_state_desc;
            """;

        // Azure SQL DB: sys.dm_os_sys_memory, sys.dm_os_process_memory and host memory
        // columns on sys.dm_os_sys_info are not exposed. Use counters only.
        const string sqlAzure = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT
                CAST(0 AS BIGINT)                                                                         AS PhysicalMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Total Server Memory (KB)'      THEN pc.cntr_value ELSE 0 END) AS TotalServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Target Server Memory (KB)'     THEN pc.cntr_value ELSE 0 END) AS TargetServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Database Cache Memory (KB)'    THEN pc.cntr_value ELSE 0 END) AS DatabaseCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'SQL Cache Memory (KB)'         THEN pc.cntr_value ELSE 0 END) AS SqlCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Granted Workspace Memory (KB)' THEN pc.cntr_value ELSE 0 END) AS GrantedWorkspaceMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Free Memory (KB)'              THEN pc.cntr_value ELSE 0 END) AS FreeMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Pending'         THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsPending,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Outstanding'     THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsOutstanding,
                CAST(0 AS BIGINT)                                                                         AS AvailablePhysicalKB,
                CAST(0 AS BIGINT)                                                                         AS TotalPageFileKB,
                CAST(0 AS BIGINT)                                                                         AS AvailablePageFileKB,
                N'N/A'                                                                                    AS SystemMemoryState,
                N'N/A'                                                                                    AS MemoryModel,
                GETDATE()                                                                                 AS CaptureTime
            FROM sys.dm_os_performance_counters pc
            WHERE RTRIM(pc.object_name) LIKE N'%Memory Manager%'
              AND RTRIM(pc.counter_name) IN (
                N'Total Server Memory (KB)', N'Target Server Memory (KB)',
                N'Database Cache Memory (KB)', N'SQL Cache Memory (KB)',
                N'Granted Workspace Memory (KB)',
                N'Free Memory (KB)', N'Memory Grants Pending', N'Memory Grants Outstanding'
              );
            """;

        using var conn = CreateConnection();

        // Detect edition AND version in one round-trip.
        // EngineEdition 5 = Azure SQL DB; 8 = Managed Instance (MI has the DMVs).
        // ProductMajorVersion < 13 = SQL Server 2014 or older (missing the 2016+ columns).
        var meta = await QFirst(conn, "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition, CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) AS MajorVersion");
        int eng2    = meta != null ? (int)meta.Edition      : 0;
        int major2  = meta != null ? (int)meta.MajorVersion : 13;
        bool isAzureSqlDb  = eng2 == 5;
        bool isLegacyOnPrem = !isAzureSqlDb && major2 < 13; // 2014 and below

        string chosenSql = isAzureSqlDb ? sqlAzure : (isLegacyOnPrem ? sqlOnPremLegacy : sqlOnPrem);
        var r = await QFirst(conn, chosenSql);
        if (r is null) return new MemorySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Unknown", "N/A", DateTime.Now);
        return new MemorySnapshot(
            (long)r.PhysicalMemoryKB,
            (long)r.TotalServerMemoryKB,
            (long)r.TargetServerMemoryKB,
            (long)r.DatabaseCacheMemoryKB,
            (long)r.SqlCacheMemoryKB,
            (long)r.GrantedWorkspaceMemoryKB,
            (long)r.FreeMemoryKB,
            (long)r.MemoryGrantsPending,
            (long)r.MemoryGrantsOutstanding,
            (long)r.AvailablePhysicalKB,
            (long)r.TotalPageFileKB,
            (long)r.AvailablePageFileKB,
            (string)r.SystemMemoryState,
            (string)r.MemoryModel,
            (DateTime)r.CaptureTime);
    }

    // ── Plan Cache Health (delegates to PlanCacheHealthRepository) ────
    public Task<(
        SqlVitals.Engine.Models.PlanCacheHealthSummary Summary,
        IEnumerable<SqlVitals.Engine.Models.MultiPlanQuery> MultiPlanQueries,
        IEnumerable<SqlVitals.Engine.Models.KeyLookupQuery> KeyLookupQueries,
        IEnumerable<SqlVitals.Engine.Models.CardinalityMisestimate> CardinalityMisestimates,
        IEnumerable<SqlVitals.Engine.Models.SkewedParallelQuery> SkewedParallelQueries,
        IEnumerable<SqlVitals.Engine.Models.PlanGuide> PlanGuides
    )> GetPlanCacheHealthAsync() => _planHealth.GetPlanCacheHealthAsync();

    // ── Live SP trace (delegated to SpTraceRepository) ────────────────
    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StartTraceAsync(SqlVitals.Engine.Models.SpTraceOptions options)
        => _spTrace.StartTraceAsync(options);

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StopTraceAsync()
        => _spTrace.StopTraceAsync();

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> GetTraceStatusAsync()
        => _spTrace.GetTraceStatusAsync();

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpTraceEvent>> PollTraceEventsAsync()
        => _spTrace.PollTraceEventsAsync();

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> PollProcedureStatsAsync()
        => _spTrace.PollProcedureStatsAsync();

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> GetProcedureStatsTotalsAsync(int topN = 25)
        => _spTrace.GetProcedureStatsTotalsAsync(topN);
}
