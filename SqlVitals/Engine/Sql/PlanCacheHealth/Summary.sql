SELECT
    -- Cache erased: server restarted < 24h ago OR oldest multi-use plan < 1h old
    CAST(
        CASE
            WHEN DATEDIFF(HOUR, si.sqlserver_start_time, GETDATE()) < 24 THEN 1
            WHEN (
                SELECT MIN(qs2.creation_time)
                FROM sys.dm_exec_query_stats qs2
                WHERE qs2.execution_count > 1
            ) > DATEADD(MINUTE, -60, GETDATE()) THEN 1
            ELSE 0
        END
    AS BIT)                                                      AS PlanCacheErasedRecently,

    -- Parameterisation failures: same query_hash with > 5 distinct plans
    (SELECT COUNT(*)
     FROM (
         SELECT query_hash
         FROM sys.dm_exec_query_stats
         WHERE query_hash <> 0x0000000000000000
         GROUP BY query_hash
         HAVING COUNT(DISTINCT plan_handle) > 5
     ) t)                                                        AS MultiPlanQueryCount,

    -- Total cached plans
    (SELECT COUNT(*) FROM sys.dm_exec_cached_plans)             AS TotalCachedPlans,

    -- Active plan guides
    (SELECT COUNT(*) FROM sys.plan_guides WHERE is_disabled = 0) AS PlanGuidesEnabled,

    -- Join hints: scan SQL text for OPTION (...JOIN) keywords — fast, no XML needed
    (SELECT COUNT(DISTINCT qs3.query_hash)
     FROM sys.dm_exec_query_stats qs3
     CROSS APPLY sys.dm_exec_sql_text(qs3.sql_handle) st3
     WHERE st3.text LIKE '%OPTION%'
       AND (  st3.text LIKE '%LOOP JOIN%'
           OR st3.text LIKE '%HASH JOIN%'
           OR st3.text LIKE '%MERGE JOIN%')
    )                                                            AS JoinHintCount
FROM sys.dm_os_sys_info si;
