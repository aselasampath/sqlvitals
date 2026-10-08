SELECT plan_id AS PlanId, is_forced_plan AS IsForced, force_failure_count AS ForceFailures,
       last_force_failure_reason_desc AS LastFailureReason
FROM sys.query_store_plan
WHERE query_id = @queryId
