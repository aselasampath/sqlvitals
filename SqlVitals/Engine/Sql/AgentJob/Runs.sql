SELECT TOP (@maxRows)
       h.instance_id  AS InstanceId,
       h.step_id      AS StepId,
       h.step_name    AS StepName,
       h.run_status   AS RunStatus,
       h.run_date     AS RunDate,
       h.run_time     AS RunTime,
       h.run_duration AS RunDuration,
       h.message      AS Message
FROM msdb.dbo.sysjobhistory h
WHERE h.job_id = @jobId
ORDER BY h.instance_id DESC
