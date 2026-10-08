SELECT TOP 40
    CASE
        WHEN tqp.query_plan LIKE '%RID Lookup%' THEN 'RID Lookup'
        ELSE 'Key Lookup'
    END                               AS LookupType,
    qs.execution_count                AS ExecutionCount,
    qs.total_logical_reads            AS TotalLogicalReads,
    CAST(qs.total_logical_reads AS FLOAT)/NULLIF(qs.execution_count,0) AS AvgLogicalReads,
    DB_NAME(st.dbid)                  AS DatabaseName,
    OBJECT_NAME(st.objectid, st.dbid) AS ObjectName,
    SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText
FROM (
    SELECT TOP 500 plan_handle, sql_handle, execution_count, total_logical_reads
    FROM sys.dm_exec_query_stats
    ORDER BY total_logical_reads DESC
) qs
CROSS APPLY sys.dm_exec_text_query_plan(qs.plan_handle, 0, -1) tqp
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE tqp.query_plan LIKE '%Key Lookup%'
   OR tqp.query_plan LIKE '%RID Lookup%'
ORDER BY qs.total_logical_reads DESC;
