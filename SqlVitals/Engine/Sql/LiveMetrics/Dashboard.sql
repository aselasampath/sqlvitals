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
        -- 8 KB data pages in the buffer pool: page life expectancy is judged per 4 GB of it (#173)
        (SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name='Database pages' AND object_name LIKE '%Buffer Manager%') AS BufferPoolPages,
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
        lg.LogUsedPct,
        lg.LogUsedDatabase,
        lg.LogSizeMb,
        lg.LogReuseWait,
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
    FROM (VALUES (1)) AS one (x)
    -- The fullest log, its size and why it can't be reused yet (#173). A small log SQL Server can
    -- reuse (nothing to wait for, or only a checkpoint) sorts last, so a log that is filling up
    -- for a reason isn't hidden behind one that is simply sized tight. A database sys.databases
    -- doesn't list (a hidden system one, as SQL Server 2025 has) sorts after the ones it does; on
    -- Azure SQL Database, whose counters name the physical database, none match and none move.
    OUTER APPLY (
        SELECT TOP (1)
            u.cntr_value                  AS LogUsedPct,
            RTRIM(u.instance_name)        AS LogUsedDatabase,
            s.cntr_value / 1024.0         AS LogSizeMb,
            d.log_reuse_wait_desc         AS LogReuseWait
        FROM sys.dm_os_performance_counters u
        JOIN sys.dm_os_performance_counters s
          ON s.object_name = u.object_name AND s.instance_name = u.instance_name
         AND s.counter_name = 'Log File(s) Size (KB)'
        LEFT JOIN sys.databases d ON d.name = RTRIM(u.instance_name)
        WHERE u.counter_name = 'Percent Log Used' AND u.object_name LIKE '%:Databases%'
          AND u.instance_name NOT IN ('_Total','mssqlsystemresource')
        ORDER BY CASE WHEN d.log_reuse_wait_desc IN ('NOTHING','CHECKPOINT') AND s.cntr_value < @SmallLogKb THEN 2
                      WHEN d.name IS NULL THEN 1 ELSE 0 END,
                 u.cntr_value DESC, u.instance_name
    ) AS lg
