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
WHERE s.is_user_process = 1
  AND (su.user_objects_alloc_page_count+su.internal_objects_alloc_page_count) > 0
ORDER BY NetTempDBUsageKB DESC
