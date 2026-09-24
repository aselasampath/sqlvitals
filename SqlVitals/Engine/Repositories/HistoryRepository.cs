using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public class HistoryRepository(IConfiguration configuration) : BaseRepository(configuration), IHistoryRepository
{
    // Same idle waits the Live Metrics total leaves out (see GetLiveMetricsAsync). They grow
    // every interval on an idle server and say nothing about a problem.
    private const string BenignWaits = """
        'SLEEP_TASK','SLEEP_SYSTEMTASK','SLEEP_DBSTARTUP','SLEEP_DBTASK','SLEEP_TEMPDBSTARTUP','SLEEP_MASTERDBREADY',
        'SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','WAITFOR','REQUEST_FOR_DEADLOCK_SEARCH',
        'RESOURCE_QUEUE','SERVER_IDLE_CHECK','SQLTRACE_BUFFER_FLUSH','BROKER_TO_FLUSH','BROKER_TASK_STOP',
        'DISPATCHER_QUEUE_SEMAPHORE','CHECKPOINT_QUEUE','XE_DISPATCHER_WAIT','XE_TIMER_EVENT','HADR_WORK_QUEUE',
        'DIRTY_PAGE_POLL','ONDEMAND_TASK_QUEUE','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE',
        'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','DBMIRROR_EVENTS_QUEUED','DBMIRROR_WORKER_QUEUE',
        'BROKER_EVENTHANDLER','BROKER_RECEIVE_WAITFOR','BROKER_TRANSMITTER','CLR_AUTO_EVENT','CLR_MANUAL_EVENT',
        'CLR_SYNCHRONIZE','FT_IFTS_SCHEDULER_IDLE_WAIT','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','DEADLOCK_ENUM_MUTEX',
        'WAIT_XTP_OFFLINE_CKPT_NEW_LOG','WAIT_XTP_RECOVERY','WAIT_XTP_HOST_WAIT','WAIT_XTP_CKPT_CLOSE',
        'PARALLEL_REDO_DRAIN_WORKER','PARALLEL_REDO_LOG_CACHE','PARALLEL_REDO_TRAN_LIST','PARALLEL_REDO_WORKER_SYNC',
        'PARALLEL_REDO_WORKER_WAIT_WORK','XE_DISPATCHER_JOIN','XE_DISPATCH_QUEUE_SEMAPHORE','SQLTRACE_WAIT_ENTRIES'
        """;

    // EngineEdition 5 = Azure SQL Database: no sys.master_files, and waits are per database.
    private bool? _isAzureSqlDb;

    public async Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries)
    {
        const string clockSql = """
            SELECT GETDATE() AS ServerTime, si.sqlserver_start_time AS ServerStartTime,
                   CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition
            FROM sys.dm_os_sys_info si
            """;

        const string filesSql = """
            SELECT ISNULL(DB_NAME(vfs.database_id), CONCAT('database ', vfs.database_id)) AS DatabaseName,
                   vfs.file_id AS FileId,
                   ISNULL(mf.name, CONCAT('file ', vfs.file_id)) AS FileName,
                   ISNULL(mf.type_desc, '') AS FileType,
                   vfs.num_of_reads AS Reads, vfs.num_of_bytes_read AS BytesRead, vfs.io_stall_read_ms AS ReadStallMs,
                   vfs.num_of_writes AS Writes, vfs.num_of_bytes_written AS BytesWritten, vfs.io_stall_write_ms AS WriteStallMs
            FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
            LEFT JOIN sys.master_files mf ON mf.database_id = vfs.database_id AND mf.file_id = vfs.file_id
            """;

        const string filesAzureSql = """
            SELECT DB_NAME() AS DatabaseName,
                   vfs.file_id AS FileId,
                   ISNULL(df.name, CONCAT('file ', vfs.file_id)) AS FileName,
                   ISNULL(df.type_desc, '') AS FileType,
                   vfs.num_of_reads AS Reads, vfs.num_of_bytes_read AS BytesRead, vfs.io_stall_read_ms AS ReadStallMs,
                   vfs.num_of_writes AS Writes, vfs.num_of_bytes_written AS BytesWritten, vfs.io_stall_write_ms AS WriteStallMs
            FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL) vfs
            LEFT JOIN sys.database_files df ON df.file_id = vfs.file_id
            """;

        // One row per query_hash: a statement recompiled or cached with several plans is still
        // one query to the DBA. The HAVING keeps each 5-minute read to what actually ran.
        const string queriesSql = """
            SELECT TOP (@maxQueries)
                   CONVERT(varchar(18), query_hash, 1) AS QueryHash,
                   SUM(execution_count)      AS Executions,
                   SUM(total_worker_time)    AS WorkerTimeUs,
                   SUM(total_elapsed_time)   AS ElapsedTimeUs,
                   SUM(total_logical_reads)  AS LogicalReads,
                   SUM(total_logical_writes) AS LogicalWrites,
                   MIN(creation_time)        AS FirstPlanCreated
            FROM sys.dm_exec_query_stats
            WHERE query_hash <> 0x0000000000000000
            GROUP BY query_hash
            HAVING @since IS NULL OR MAX(last_execution_time) >= @since
            ORDER BY SUM(total_worker_time) DESC
            """;

        const string memorySql = """
            SELECT
                MAX(CASE WHEN RTRIM(counter_name) = 'Total Server Memory (KB)'      THEN cntr_value END) AS TotalServerMemoryKB,
                MAX(CASE WHEN RTRIM(counter_name) = 'Target Server Memory (KB)'     THEN cntr_value END) AS TargetServerMemoryKB,
                MAX(CASE WHEN RTRIM(counter_name) = 'Database Cache Memory (KB)'    THEN cntr_value END) AS DatabaseCacheMemoryKB,
                MAX(CASE WHEN RTRIM(counter_name) = 'SQL Cache Memory (KB)'         THEN cntr_value END) AS SqlCacheMemoryKB,
                MAX(CASE WHEN RTRIM(counter_name) = 'Granted Workspace Memory (KB)' THEN cntr_value END) AS GrantedWorkspaceMemoryKB,
                MAX(CASE WHEN RTRIM(counter_name) = 'Free Memory (KB)'              THEN cntr_value END) AS FreeMemoryKB,
                MAX(CASE WHEN RTRIM(counter_name) = 'Memory Grants Outstanding'     THEN cntr_value END) AS MemoryGrantsOutstanding
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%Memory Manager%'
            """;

        const string countersSql = """
            SELECT RTRIM(counter_name) AS CounterName, cntr_value AS CntrValue, cntr_type AS CntrType
            FROM sys.dm_os_performance_counters
            """ + "\n" + WaitStatsRepository.PerfmonCounterFilter;

        using var conn = CreateConnection();

        var clock = await QFirst(conn, clockSql)
                    ?? throw new InvalidOperationException("Failed to read the server clock.");
        _isAzureSqlDb ??= Convert.ToInt32(clock.Edition) == 5;
        var isAzure = _isAzureSqlDb.Value;

        var waitsSql = $"""
            SELECT wait_type AS WaitType, waiting_tasks_count AS WaitingTasks,
                   wait_time_ms AS WaitTimeMs, signal_wait_time_ms AS SignalWaitTimeMs
            FROM {(isAzure ? "sys.dm_db_wait_stats" : "sys.dm_os_wait_stats")}
            WHERE wait_time_ms > 0 AND wait_type NOT IN ({BenignWaits})
            """;

        var waits = (await Q(conn, waitsSql)).Select(r => new WaitTypeTotals(
            (string)r.WaitType, Convert.ToInt64(r.WaitingTasks), Convert.ToInt64(r.WaitTimeMs),
            Convert.ToInt64(r.SignalWaitTimeMs))).ToList();

        var files = (await Q(conn, isAzure ? filesAzureSql : filesSql)).Select(r => new FileIoTotals(
            (string)r.DatabaseName, Convert.ToInt32(r.FileId), (string)r.FileName, (string)r.FileType,
            Convert.ToInt64(r.Reads), Convert.ToInt64(r.BytesRead), Convert.ToInt64(r.ReadStallMs),
            Convert.ToInt64(r.Writes), Convert.ToInt64(r.BytesWritten), Convert.ToInt64(r.WriteStallMs))).ToList();

        var queries = (await Q(conn, queriesSql, new { maxQueries, since = queriesExecutedSince })).Select(r => new QueryTotals(
            (string)r.QueryHash, Convert.ToInt64(r.Executions), Convert.ToInt64(r.WorkerTimeUs),
            Convert.ToInt64(r.ElapsedTimeUs), Convert.ToInt64(r.LogicalReads), Convert.ToInt64(r.LogicalWrites),
            (DateTime)r.FirstPlanCreated)).ToList();

        var m = await QFirst(conn, memorySql);
        HistoryMemory? memory = m?.TotalServerMemoryKB is null ? null : new HistoryMemory(
            Convert.ToInt64(m!.TotalServerMemoryKB),
            Convert.ToInt64(m.TargetServerMemoryKB ?? 0L),
            Convert.ToInt64(m.DatabaseCacheMemoryKB ?? 0L),
            Convert.ToInt64(m.SqlCacheMemoryKB ?? 0L),
            Convert.ToInt64(m.GrantedWorkspaceMemoryKB ?? 0L),
            Convert.ToInt64(m.FreeMemoryKB ?? 0L),
            Convert.ToInt64(m.MemoryGrantsOutstanding ?? 0L));

        // First row per name, as the Perfmon page takes it.
        var counters = (await Q(conn, countersSql))
            .Select(r => new CounterTotals(
                (string)r.CounterName, Convert.ToInt64(r.CntrValue),
                Convert.ToInt32(r.CntrType) == WaitStatsRepository.PerfmonRateCounterType))
            .DistinctBy(c => c.CounterName)
            .ToList();

        return new HistoryDetailSnapshot(
            (DateTime)clock.ServerTime, (DateTime)clock.ServerStartTime, waits, files, queries, memory, counters);
    }

    public async Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength)
    {
        if (queryHashes.Count == 0)
            return Array.Empty<QueryTextInfo>();

        const string sql = """
            SELECT x.QueryHash, DB_NAME(x.dbid) AS DatabaseName, x.QueryText
            FROM (
                SELECT CONVERT(varchar(18), qs.query_hash, 1) AS QueryHash,
                       st.dbid,
                       LEFT(SUBSTRING(st.text, qs.statement_start_offset / 2 + 1,
                            (CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                                  ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1),
                            @maxLength) AS QueryText,
                       ROW_NUMBER() OVER (PARTITION BY qs.query_hash ORDER BY qs.last_execution_time DESC) AS rn
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                WHERE CONVERT(varchar(18), qs.query_hash, 1) IN @hashes
            ) x
            WHERE x.rn = 1 AND x.QueryText IS NOT NULL
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql, new { hashes = queryHashes, maxLength }))
            .Select(r => new QueryTextInfo((string)r.QueryHash, (string?)r.DatabaseName, (string)r.QueryText))
            .ToList();
    }
}
