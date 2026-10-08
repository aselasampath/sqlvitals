SELECT j.job_id                    AS JobId,
       j.name                      AS Name,
       CAST(j.enabled AS INT)      AS Enabled,
       c.name                      AS Category,
       SUSER_SNAME(j.owner_sid)    AS Owner,
       j.description               AS Description,
       lr.run_status               AS LastRunStatus,
       lr.run_date                 AS LastRunDate,
       lr.run_time                 AS LastRunTime,
       lr.run_duration             AS LastRunDuration,
       lr.message                  AS LastMessage,
       st.Runs,
       ISNULL(st.FailedRuns, 0)    AS FailedRuns,
       ISNULL(st.SucceededRuns, 0) AS SucceededRuns,
       st.AverageSeconds,
       a.start_execution_date      AS StartExecution,
       a.stop_execution_date       AS StopExecution,
       a.next_scheduled_run_date   AS ActivityNextRun,
       ISNULL(sc.NextRun, 0)             AS ScheduleNextRun,
       sc.EnabledSchedules,
       ISNULL(sc.AgentStartSchedules, 0) AS AgentStartSchedules,
       ISNULL(sc.IdleSchedules, 0)       AS IdleSchedules
FROM msdb.dbo.sysjobs j
LEFT JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
OUTER APPLY (
    SELECT TOP (1) h.run_status, h.run_date, h.run_time, h.run_duration, h.message
    FROM msdb.dbo.sysjobhistory h
    WHERE h.job_id = j.job_id AND h.step_id = 0
    ORDER BY h.instance_id DESC) lr
OUTER APPLY (
    SELECT COUNT(*) AS Runs,
           SUM(CASE WHEN h.run_status = 0 THEN 1 ELSE 0 END) AS FailedRuns,
           SUM(CASE WHEN h.run_status = 1 THEN 1 ELSE 0 END) AS SucceededRuns,
           AVG(CASE WHEN h.run_status = 1
                    THEN CAST(CASE WHEN h.run_duration > 0
                                   THEN h.run_duration / 10000 * 3600 + h.run_duration / 100 % 100 * 60 + h.run_duration % 100
                                   ELSE 0 END AS FLOAT)
               END) AS AverageSeconds
    FROM msdb.dbo.sysjobhistory h
    WHERE h.job_id = j.job_id AND h.step_id = 0) st
LEFT JOIN msdb.dbo.sysjobactivity a
       ON a.job_id = j.job_id
      AND a.session_id = (SELECT MAX(s.session_id) FROM msdb.dbo.syssessions s)
OUTER APPLY (
    SELECT MIN(CASE WHEN js.next_run_date > 0
                    THEN CAST(js.next_run_date AS BIGINT) * 1000000 + js.next_run_time END) AS NextRun,
           COUNT(*) AS EnabledSchedules,
           SUM(CASE WHEN s.freq_type = 64  THEN 1 ELSE 0 END) AS AgentStartSchedules,
           SUM(CASE WHEN s.freq_type = 128 THEN 1 ELSE 0 END) AS IdleSchedules
    FROM msdb.dbo.sysjobschedules js
    JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
    WHERE js.job_id = j.job_id AND s.enabled = 1) sc
