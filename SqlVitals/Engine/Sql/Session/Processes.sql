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
    ISNULL(r.open_transaction_count, s.open_transaction_count) AS OpenTran,
    r.command AS Command,
    DB_NAME(ISNULL(r.database_id, s.database_id)) AS DatabaseName,
    ISNULL(qt.text, '') AS QueryText,
    tx.TransactionBegin,
    SYSDATETIME() AS ServerNow
FROM sys.dm_exec_sessions s
LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
-- When its oldest open transaction began, for the details drawer's transaction age (#147).
OUTER APPLY (SELECT MIN(at.transaction_begin_time) AS TransactionBegin
             FROM sys.dm_tran_session_transactions st
             JOIN sys.dm_tran_active_transactions at ON at.transaction_id = st.transaction_id
             WHERE st.session_id = s.session_id) tx
OUTER APPLY (SELECT TOP (1) c.most_recent_sql_handle
             FROM sys.dm_exec_connections c
             WHERE c.session_id = s.session_id
             ORDER BY c.last_read DESC) c
OUTER APPLY sys.dm_exec_sql_text(ISNULL(r.sql_handle, c.most_recent_sql_handle)) qt
WHERE s.is_user_process = 1
   OR s.session_id IN (SELECT blocking_session_id FROM sys.dm_exec_requests
                       WHERE blocking_session_id > 0)
