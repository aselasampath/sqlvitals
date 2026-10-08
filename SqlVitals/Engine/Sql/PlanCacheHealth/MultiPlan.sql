SELECT TOP 30
    pc                         AS PlanCount,
    total_exec                 AS TotalExecutions,
    CONVERT(VARCHAR(18), query_hash, 1) AS QueryHash,
    query_text                 AS QueryText,
    db_name                    AS DatabaseName
FROM (
    SELECT
        COUNT(DISTINCT qs.plan_handle)          AS pc,
        SUM(qs.execution_count)                 AS total_exec,
        qs.query_hash,
        MIN(SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400)) AS query_text,
        MIN(DB_NAME(st.dbid))                   AS db_name
    FROM sys.dm_exec_query_stats qs
    CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
    WHERE qs.query_hash <> 0x0000000000000000
    GROUP BY qs.query_hash
    HAVING COUNT(DISTINCT qs.plan_handle) > 5
) t
ORDER BY pc DESC;
