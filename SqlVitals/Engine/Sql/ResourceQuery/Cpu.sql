SELECT TOP (@TopN)
    SUBSTRING(qt.text,(qs.statement_start_offset/2)+1,
        ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END
          - qs.statement_start_offset)/2)+1) AS QueryText,
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
    DB_NAME(qt.dbid) AS DatabaseName,
    ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
    GETDATE() AS CaptureTime,
    CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
    CASE
        WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
        ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                      WHEN 'FN' THEN 'Scalar Func'
                                      WHEN 'TF' THEN 'Table Func'
                                      WHEN 'IF' THEN 'Inline Func'
                                      WHEN 'V'  THEN 'View'
                                      WHEN 'TR' THEN 'Trigger'
                                      ELSE type_desc END
                     FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
    END AS ObjectType
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
WHERE qt.dbid = DB_ID()
ORDER BY qs.total_worker_time DESC
