/*PERIOD*/
;WITH per_plan AS (
    SELECT p.query_id, rs.plan_id,
           SUM(rs.count_executions)                                                   AS n,
           SUM(CASE WHEN rs.execution_type <> 0 THEN rs.count_executions ELSE 0 END) AS failed,
           SUM(rs.avg_cpu_time         * rs.count_executions)                        AS cpu_us,
           SUM(rs.avg_duration         * rs.count_executions)                        AS duration_us,
           SUM(rs.avg_logical_io_reads * rs.count_executions)                        AS logical_reads,
           /*METRIC_SUMS*/,
           MAX(rs.last_execution_time)                                               AS last_execution
    /*RUNTIME_IN_PERIOD*/
    GROUP BY p.query_id, rs.plan_id
),
per_query AS (
    SELECT query_id,
           COUNT(*)            AS plans,
           SUM(n)              AS n,
           SUM(failed)         AS failed,
           SUM(cpu_us)         AS cpu_us,
           SUM(duration_us)    AS duration_us,
           SUM(logical_reads)  AS logical_reads,
           SUM(m_sum)          AS m_sum,
           SUM(m_sum_sq)       AS m_sum_sq,
           MAX(m_max)          AS m_max,
           MAX(last_execution) AS last_execution,
           MAX(CASE WHEN plan_rank = 1 THEN plan_id END) AS top_plan_id
    FROM /*RANKED_PLANS*/
    GROUP BY query_id
),
top_queries AS (
    SELECT TOP (@top) *, /*RANK*/ AS metric_value
    FROM per_query
    ORDER BY metric_value DESC
)
SELECT t.query_id          AS QueryId,
       q.object_id         AS ObjectId,
       /*OBJECT_NAME*/     AS ObjectName,
       qt.query_sql_text   AS QueryText,
       t.plans             AS Plans,
       t.n                 AS Executions,
       t.failed            AS Failed,
       t.metric_value      AS MetricValue,
       t.cpu_us            AS CpuUs,
       t.duration_us       AS DurationUs,
       t.logical_reads     AS LogicalReads,
       (SELECT MAX(fp.plan_id) FROM sys.query_store_plan fp
        WHERE fp.query_id = t.query_id AND fp.is_forced_plan = 1) AS ForcedPlanId,
       t.top_plan_id       AS TopPlanId,
       t.last_execution    AS LastExecution
FROM top_queries t
JOIN sys.query_store_query q       ON q.query_id = t.query_id
JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
ORDER BY t.metric_value DESC
