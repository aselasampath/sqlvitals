SELECT TOP (1) CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan, x.IsRunning
FROM (
    SELECT r.plan_handle, CAST(1 AS BIT) AS IsRunning, 0 AS Ord
    FROM sys.dm_exec_requests r
    WHERE r.session_id = @SessionId AND r.plan_handle IS NOT NULL
    UNION ALL
    SELECT last.plan_handle, CAST(0 AS BIT), 1
    FROM (SELECT TOP (1) qs.plan_handle
          FROM sys.dm_exec_connections c
          JOIN sys.dm_exec_query_stats qs ON qs.sql_handle = c.most_recent_sql_handle
          WHERE c.session_id = @SessionId
          ORDER BY qs.last_execution_time DESC) last
) x
CROSS APPLY sys.dm_exec_query_plan(x.plan_handle) qp
WHERE qp.query_plan IS NOT NULL
ORDER BY x.Ord;
