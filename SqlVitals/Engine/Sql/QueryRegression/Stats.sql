DECLARE @now          datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @recentFrom   datetimeoffset = DATEADD(MINUTE, -@recentMinutes,        @now);
DECLARE @baselineFrom datetimeoffset = DATEADD(MINUTE, -@baselineStartMinutes, @now);
DECLARE @baselineTo   datetimeoffset = DATEADD(MINUTE, -@baselineEndMinutes,   @now);

;WITH s AS (
    SELECT p.query_id, rs.plan_id,
           CASE WHEN i.start_time >= @recentFrom THEN 1 ELSE 0 END AS is_recent,
           rs.count_executions, rs.avg_duration, rs.avg_cpu_time
    FROM sys.query_store_runtime_stats_interval i
    JOIN sys.query_store_runtime_stats rs ON rs.runtime_stats_interval_id = i.runtime_stats_interval_id
    JOIN sys.query_store_plan p           ON p.plan_id = rs.plan_id
    JOIN sys.query_store_query q          ON q.query_id = p.query_id
    WHERE q.is_internal_query = 0
      AND (i.start_time >= @recentFrom
           OR (i.start_time >= @baselineFrom AND i.start_time < @baselineTo))
)
SELECT query_id AS QueryId, plan_id AS PlanId, is_recent AS IsRecent,
       SUM(count_executions)                AS Executions,
       SUM(avg_duration * count_executions) AS DurationUs,
       SUM(avg_cpu_time * count_executions) AS CpuUs
FROM s
WHERE query_id IN (SELECT query_id FROM s WHERE is_recent = 1)
GROUP BY query_id, plan_id, is_recent
