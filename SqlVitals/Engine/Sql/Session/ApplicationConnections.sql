SELECT
    ISNULL(s.program_name, '') AS ApplicationName,
    COUNT(*)                   AS CurrentConnections,
    SUM(CASE WHEN r.session_id IS NOT NULL THEN 1 ELSE 0 END) AS ActiveRequests,
    SUM(CAST(s.cpu_time AS BIGINT))         AS TotalCpuTimeMs,
    SUM(CAST(s.memory_usage AS BIGINT)) * 8 AS TotalMemoryKB,
    SUM(s.logical_reads)       AS TotalLogicalReads,
    SUM(s.reads)               AS TotalReads,
    SUM(s.writes)              AS TotalWrites,
    MIN(s.login_time)          AS FirstLoginTime,
    MAX(s.last_request_start_time) AS LastRequestTime,
    GETDATE()                  AS CaptureTime
FROM sys.dm_exec_sessions s
LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
WHERE s.is_user_process = 1
GROUP BY ISNULL(s.program_name, '')
ORDER BY COUNT(*) DESC
