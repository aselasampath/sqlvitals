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
