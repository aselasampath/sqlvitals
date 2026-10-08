/*PERIOD*/
SELECT i.start_time AS StartTime,
       rs.plan_id   AS PlanId,
       /*TOTALS*/,
       MIN(rs.first_execution_time) AS FirstExecution,
       MAX(rs.last_execution_time)  AS LastExecution
/*RUNTIME_IN_PERIOD*/
  AND p.query_id = @queryId
GROUP BY i.start_time, rs.plan_id
ORDER BY i.start_time, rs.plan_id
