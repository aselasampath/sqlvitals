/*PERIOD*/
SELECT ws.wait_category                 AS Category,
       MAX(ws.wait_category_desc)       AS CategoryName,
       SUM(ws.total_query_wait_time_ms) AS WaitMs
FROM sys.query_store_runtime_stats_interval i
JOIN sys.query_store_wait_stats ws ON ws.runtime_stats_interval_id = i.runtime_stats_interval_id
WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
GROUP BY ws.wait_category
HAVING SUM(ws.total_query_wait_time_ms) > 0
ORDER BY WaitMs DESC
