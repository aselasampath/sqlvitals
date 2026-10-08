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
