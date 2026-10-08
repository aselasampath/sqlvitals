FROM sys.query_store_runtime_stats_interval i
JOIN sys.query_store_runtime_stats rs ON rs.runtime_stats_interval_id = i.runtime_stats_interval_id
JOIN sys.query_store_plan p           ON p.plan_id = rs.plan_id
WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
