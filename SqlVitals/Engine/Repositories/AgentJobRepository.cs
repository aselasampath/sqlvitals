using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads SQL Server Agent jobs from msdb (#39): the jobs, their outcome rows in sysjobhistory,
/// the current Agent session's sysjobactivity and the schedules. Read only; msdb keeps its
/// times in the server's local time, and so do the jobs listed.
/// </summary>
public class AgentJobRepository(SqlTarget target) : BaseRepository(target), IAgentJobRepository
{
    // SELECT denied on an msdb table (229), or no access to msdb at all (916).
    private static readonly int[] MsdbPermissionErrors = [229, 916];

    private const int AzureSqlDatabaseEdition = 5;

    // Kept to what every edition has, so Azure SQL Database gets its message rather than an error.
    internal static readonly string ServerSql = SqlScripts.Get("AgentJob/Server");

    // Agent turns this option on as it starts and off as it stops.
    internal static readonly string AgentXpsSql = SqlScripts.Get("AgentJob/AgentXps");

    // Needs VIEW SERVER STATE; without it the Agent XPs option above is used.
    internal static readonly string AgentServiceSql = SqlScripts.Get("AgentJob/AgentService");

    // One row per job. Step 0 rows in sysjobhistory are the job's outcome; run_duration is hhmmss.
    // sysjobactivity is read for the newest Agent session only: older sessions' rows are history.
    internal static readonly string JobsSql = SqlScripts.Get("AgentJob/Jobs");

    internal static readonly string RunsSql = SqlScripts.Get("AgentJob/Runs");

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
