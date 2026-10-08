SELECT TOP 1 CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
WHERE qs.query_hash = CONVERT(binary(8), @queryHash, 1) AND qp.query_plan IS NOT NULL
ORDER BY qs.last_execution_time DESC
