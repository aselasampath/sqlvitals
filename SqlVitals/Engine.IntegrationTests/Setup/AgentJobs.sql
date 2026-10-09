-- Two Agent jobs for the Agent Jobs page's queries (#46): one that succeeds, on a schedule, and
-- one that fails. Made afresh on each run; the fixture starts both and waits for their history.

USE msdb;
GO
DECLARE @name sysname;
DECLARE jobs CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM msdb.dbo.sysjobs WHERE name LIKE N'SqlVitalsIT %';
OPEN jobs;
FETCH NEXT FROM jobs INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC msdb.dbo.sp_delete_job @job_name = @name;
    FETCH NEXT FROM jobs INTO @name;
END
CLOSE jobs;
DEALLOCATE jobs;
GO
EXEC msdb.dbo.sp_add_job @job_name = N'SqlVitalsIT succeeds', @description = N'Integration tests (#46)';
EXEC msdb.dbo.sp_add_jobstep @job_name = N'SqlVitalsIT succeeds', @step_name = N'Count orders',
     @subsystem = N'TSQL', @database_name = N'SqlVitalsIT', @command = N'SELECT COUNT(*) FROM dbo.Orders;';
EXEC msdb.dbo.sp_add_jobschedule @job_name = N'SqlVitalsIT succeeds', @name = N'SqlVitalsIT daily',
     @freq_type = 4, @freq_interval = 1, @active_start_time = 20000;
EXEC msdb.dbo.sp_add_jobserver @job_name = N'SqlVitalsIT succeeds';

EXEC msdb.dbo.sp_add_job @job_name = N'SqlVitalsIT fails', @description = N'Integration tests (#46)';
EXEC msdb.dbo.sp_add_jobstep @job_name = N'SqlVitalsIT fails', @step_name = N'Raise an error',
     @subsystem = N'TSQL', @database_name = N'master', @command = N'RAISERROR(N''The integration tests asked for this failure.'', 16, 1);';
EXEC msdb.dbo.sp_add_jobserver @job_name = N'SqlVitalsIT fails';
GO
