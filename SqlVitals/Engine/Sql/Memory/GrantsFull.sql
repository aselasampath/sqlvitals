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
