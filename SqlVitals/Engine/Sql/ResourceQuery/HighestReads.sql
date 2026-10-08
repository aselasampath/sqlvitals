SELECT TOP (@TopN)
    st.text AS QueryText,
    qs.execution_count AS ExecutionCount,
    qs.total_logical_reads AS TotalLogicalReads,
    CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
    qs.total_worker_time AS TotalCPUTime,
    CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
    qs.total_elapsed_time AS TotalElapsedTime,
    CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
    qs.total_physical_reads AS TotalPhysicalReads,
    qs.total_logical_writes AS TotalLogicalWrites,
    qs.last_execution_time AS LastExecutionTime,
    qs.creation_time AS PlanCreationTime,
    DB_NAME(st.dbid) AS DatabaseName,
    ISNULL(OBJECT_NAME(st.objectid,st.dbid),'') AS ObjectName,
    GETDATE() AS CaptureTime,
    CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
    qs.last_logical_reads AS LastLogicalReads,
    CASE
        WHEN st.objectid IS NULL THEN 'Ad-Hoc'
        ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                      WHEN 'FN' THEN 'Scalar Func'
                                      WHEN 'TF' THEN 'Table Func'
                                      WHEN 'IF' THEN 'Inline Func'
                                      WHEN 'V'  THEN 'View'
                                      WHEN 'TR' THEN 'Trigger'
                                      ELSE type_desc END
                     FROM sys.objects WHERE object_id = st.objectid), 'Ad-Hoc')
    END AS ObjectType
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
WHERE st.dbid = DB_ID()
ORDER BY qs.total_logical_reads DESC
