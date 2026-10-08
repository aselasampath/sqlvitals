SELECT TOP 50
    DB_NAME(qt.dbid) AS DatabaseName,
    ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
    cp.usecounts AS ExecutionCount,
    cp.size_in_bytes/1024 AS PlanSizeKB,
    SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
    GETDATE() AS CaptureTime
FROM sys.dm_exec_cached_plans cp
CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) qt
WHERE CAST(qp.query_plan AS NVARCHAR(MAX)) LIKE '%CONVERT_IMPLICIT%'
ORDER BY cp.usecounts DESC
