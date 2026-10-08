/*PERIOD*/
SELECT p.query_id                     AS QueryId,
       p.plan_id                      AS PlanId,
       q.object_id                    AS ObjectId,
       /*OBJECT_NAME*/                AS ObjectName,
       qt.query_sql_text              AS QueryText,
       /*FORCING_TYPE*/               AS ForcingType,
       p.force_failure_count          AS ForceFailures,
       p.last_force_failure_reason_desc AS LastFailureReason,
       p.last_execution_time          AS LastExecution,
       s.n                            AS Executions,
       s.duration_us                  AS DurationUs
FROM sys.query_store_plan p
JOIN sys.query_store_query q       ON q.query_id = p.query_id
JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
OUTER APPLY (
    SELECT SUM(rs.count_executions)                    AS n,
           SUM(rs.avg_duration * rs.count_executions)  AS duration_us
    FROM sys.query_store_runtime_stats rs
    JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
    WHERE rs.plan_id = p.plan_id
      AND i.start_time >= @periodFrom AND i.start_time < @periodTo
) s
WHERE p.is_forced_plan = 1
ORDER BY p.force_failure_count DESC, p.query_id, p.plan_id
