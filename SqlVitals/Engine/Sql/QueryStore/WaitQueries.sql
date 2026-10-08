/*PERIOD*/
;WITH waits AS (
    SELECT p.query_id, SUM(ws.total_query_wait_time_ms) AS wait_ms
    FROM sys.query_store_runtime_stats_interval i
    JOIN sys.query_store_wait_stats ws ON ws.runtime_stats_interval_id = i.runtime_stats_interval_id
    JOIN sys.query_store_plan p        ON p.plan_id = ws.plan_id
    WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
      AND ws.wait_category = @category
    GROUP BY p.query_id
),
top_queries AS (
    SELECT TOP (@top) query_id, wait_ms, SUM(wait_ms) OVER () AS category_ms
    FROM waits
    ORDER BY wait_ms DESC
),
per_plan AS (
    SELECT p.query_id, rs.plan_id, SUM(rs.count_executions) AS n
    /*RUNTIME_IN_PERIOD*/
      AND p.query_id IN (SELECT query_id FROM top_queries)
    GROUP BY p.query_id, rs.plan_id
),
executions AS (
    SELECT query_id, SUM(n) AS n, MAX(CASE WHEN plan_rank = 1 THEN plan_id END) AS top_plan_id
    FROM /*RANKED_PLANS*/
    GROUP BY query_id
)
SELECT t.query_id        AS QueryId,
       q.object_id       AS ObjectId,
       /*OBJECT_NAME*/ AS ObjectName,
       qt.query_sql_text AS QueryText,
       t.wait_ms         AS WaitMs,
       t.category_ms     AS CategoryMs,
       ISNULL(e.n, 0)    AS Executions,
       e.top_plan_id     AS TopPlanId
FROM top_queries t
JOIN sys.query_store_query q       ON q.query_id = t.query_id
JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
LEFT JOIN executions e             ON e.query_id = t.query_id
ORDER BY t.wait_ms DESC
