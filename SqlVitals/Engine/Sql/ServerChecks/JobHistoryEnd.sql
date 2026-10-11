SELECT CAST(MAX(h.instance_id) AS BIGINT) AS LastInstanceId,
       @@SERVERNAME                       AS ServerName
FROM msdb.dbo.sysjobhistory h
