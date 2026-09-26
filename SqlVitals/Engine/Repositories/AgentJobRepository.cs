using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads SQL Server Agent jobs from msdb (#39): the jobs, their outcome rows in sysjobhistory,
/// the current Agent session's sysjobactivity and the schedules. Read only; msdb keeps its
/// times in the server's local time, and so do the jobs listed.
/// </summary>
public class AgentJobRepository(IConfiguration configuration) : BaseRepository(configuration), IAgentJobRepository
{
    // SELECT denied on an msdb table (229), or no access to msdb at all (916).
    private static readonly int[] MsdbPermissionErrors = [229, 916];

    private const int AzureSqlDatabaseEdition = 5;

    // Kept to what every edition has, so Azure SQL Database gets its message rather than an error.
    internal const string ServerSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition,
               SYSDATETIME() AS ServerNow
        """;

    // Agent turns this option on as it starts and off as it stops.
    internal const string AgentXpsSql = """
        SELECT CAST(value_in_use AS INT) AS AgentXps
        FROM sys.configurations
        WHERE name = N'Agent XPs'
        """;

    // Needs VIEW SERVER STATE; without it the Agent XPs option above is used.
    internal const string AgentServiceSql = """
        SELECT TOP (1) status_desc AS Status
        FROM sys.dm_server_services
        WHERE servicename LIKE N'SQL Server Agent%'
        """;

    // One row per job. Step 0 rows in sysjobhistory are the job's outcome; run_duration is hhmmss.
    // sysjobactivity is read for the newest Agent session only: older sessions' rows are history.
    internal const string JobsSql = """
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
        """;

    internal const string RunsSql = """
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
        """;

    public async Task<AgentJobList> GetAgentJobsAsync()
    {
        using var conn = CreateConnection();

        var server  = await QFirst(conn, ServerSql);
        int edition = server is null ? 0 : Convert.ToInt32(server.Edition);
        DateTime serverNow = server?.ServerNow as DateTime? ?? DateTime.Now;
        if (edition == AzureSqlDatabaseEdition)
            return AgentJobList.Unavailable(
                "Azure SQL Database has no SQL Server Agent, so there are no jobs to monitor. Elastic Jobs or Azure " +
                "Automation run scheduled work there; SQL Server and Azure SQL Managed Instance are supported here.");

        string? serviceStatus = null;
        try
        {
            serviceStatus = (await QFirst(conn, AgentServiceSql))?.Status as string;
        }
        catch (WaitStatsException)
        {
            // No VIEW SERVER STATE, or a platform without the DMV: the Agent XPs option decides.
        }

        int? agentXps = null;
        try
        {
            agentXps = (await QFirst(conn, AgentXpsSql))?.AgentXps as int?;
        }
        catch (WaitStatsException)
        {
            // Then Agent's state is unknown, and the jobs are shown as msdb has them.
        }

        var agent = AgentJobList.AgentState(edition, agentXps, serviceStatus);

        List<dynamic> rows;
        try
        {
            rows = (await Q(conn, JobsSql)).ToList();
        }
        catch (WaitStatsException ex) when (MsdbPermissionErrors.Contains(ex.SqlErrorNumber))
        {
            return AgentJobList.Unavailable(
                "This login can't read SQL Server Agent's tables in msdb. Members of sysadmin can; for another login, " +
                "create a user for it in msdb and GRANT SELECT on dbo.sysjobs, sysjobhistory, sysjobactivity, " +
                "syssessions, sysjobschedules, sysschedules and syscategories.");
        }

        var jobs = rows.Select(r => (AgentJobRow)ToRow(r)).Select(row => AgentJob.From(row, serverNow, agent));
        return new AgentJobList(AgentJobList.Sort(jobs), agent, serverNow, AgentJobList.AgentProblem(agent));
    }

    public async Task<IReadOnlyList<JobRun>> GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince)
    {
        using var conn = CreateConnection();
        var rows = await Q(conn, RunsSql, new { jobId, maxRows = AgentJobHistory.MaxRows });
        return AgentJobHistory.GroupRuns(rows.Select(r => new JobHistoryRow(
            (int)r.InstanceId,
            (int)r.StepId,
            (string?)r.StepName,
            (int)r.RunStatus,
            (int)r.RunDate,
            (int)r.RunTime,
            (int)r.RunDuration,
            (string?)r.Message)), runningSince);
    }

    private static AgentJobRow ToRow(dynamic r) => new(
        (Guid)r.JobId,
        (string)r.Name,
        (int)r.Enabled == 1,
        (string?)r.Category,
        (string?)r.Owner,
        (string?)r.Description,
        (int?)r.LastRunStatus,
        (int?)r.LastRunDate ?? 0,
        (int?)r.LastRunTime ?? 0,
        (int?)r.LastRunDuration ?? 0,
        (string?)r.LastMessage,
        (int)r.Runs,
        (int)r.FailedRuns,
        (int)r.SucceededRuns,
        (double?)r.AverageSeconds,
        (DateTime?)r.StartExecution,
        (DateTime?)r.StopExecution,
        (DateTime?)r.ActivityNextRun,
        (long)r.ScheduleNextRun,
        (int)r.EnabledSchedules,
        (int)r.AgentStartSchedules,
        (int)r.IdleSchedules);
}
