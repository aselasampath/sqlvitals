SELECT j.name                              AS Name,
       CAST(MAX(h.instance_id) AS BIGINT)  AS LastInstanceId
FROM msdb.dbo.sysjobhistory h
JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
WHERE h.step_id = 0
  AND h.run_status = 0
  AND h.instance_id <= @upToInstanceId
  AND (h.instance_id > @afterInstanceId
       OR (@afterInstanceId IS NULL
           AND DATEADD(SECOND,
                       CASE WHEN h.run_duration > 0
                            THEN h.run_duration / 10000 * 3600 + h.run_duration / 100 % 100 * 60 + h.run_duration % 100
                            ELSE 0 END,
                       TRY_CONVERT(DATETIME2(0),
                                   CONCAT(h.run_date, ' ', STUFF(STUFF(RIGHT(CONCAT('000000', h.run_time), 6), 5, 0, ':'), 3, 0, ':'))))
               >= DATEADD(SECOND, -@lookBackSeconds, GETDATE())))
GROUP BY j.name
ORDER BY MAX(h.instance_id) DESC
