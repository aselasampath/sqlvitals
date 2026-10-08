SELECT TOP 1 CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan
FROM (
    SELECT plan_handle FROM sys.dm_exec_procedure_stats
    WHERE object_id = OBJECT_ID(@ObjectName) AND database_id = DB_ID()
    UNION ALL
    SELECT plan_handle FROM sys.dm_exec_function_stats
    WHERE object_id = OBJECT_ID(@ObjectName) AND database_id = DB_ID()
) x
CROSS APPLY sys.dm_exec_query_plan(x.plan_handle) qp
WHERE qp.query_plan IS NOT NULL
