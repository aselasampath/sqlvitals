/*PERIOD*/
;WITH per_plan AS (
    SELECT p.query_id, rs.plan_id,
           SUM(rs.count_executions)                                                                  AS n,
           SUM(rs.avg_/*COLUMN*/ * rs.count_executions)                                             AS m_sum,
           SUM(rs.count_executions * (SQUARE(rs.stdev_/*COLUMN*/) + SQUARE(rs.avg_/*COLUMN*/)))    AS m_sum_sq,
           MIN(CAST(rs.min_/*COLUMN*/ AS float))                                                    AS m_min,
           MAX(CAST(rs.max_/*COLUMN*/ AS float))                                                    AS m_max
    /*RUNTIME_IN_PERIOD*/
    GROUP BY p.query_id, rs.plan_id
),
per_query AS (
    SELECT query_id,
           COUNT(*)      AS plans,
           SUM(n)        AS n,
           SUM(m_sum)    AS m_sum,
           SUM(m_sum_sq) AS m_sum_sq,
           MIN(m_min)    AS m_min,
           MAX(m_max)    AS m_max,
           MAX(CASE WHEN plan_rank = 1 THEN plan_id END) AS top_plan_id
    FROM /*RANKED_PLANS*/
    GROUP BY query_id
    HAVING SUM(n) >= @minExecutions
),
top_queries AS (
    SELECT TOP (@top) *,
           m_sum / NULLIF(n, 0) AS m_avg,
           /*STDEV*/            AS m_stdev
    FROM per_query
    ORDER BY m_stdev DESC
)
SELECT t.query_id        AS QueryId,
       q.object_id       AS ObjectId,
       /*OBJECT_NAME*/   AS ObjectName,
       qt.query_sql_text AS QueryText,
       t.plans           AS Plans,
       t.n               AS Executions,
       t.m_avg           AS AvgValue,
       t.m_min           AS MinValue,
       t.m_max           AS MaxValue,
       t.m_stdev         AS StdDevValue,
       t.top_plan_id     AS TopPlanId
FROM top_queries t
JOIN sys.query_store_query q       ON q.query_id = t.query_id
JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
ORDER BY t.m_stdev DESC
