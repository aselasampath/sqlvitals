using SqlVitals.Engine.AgentJobs;

namespace SqlVitals.Engine.Repositories;

/// <summary>SQL Server Agent jobs and their runs, read from msdb (#39).</summary>
public interface IAgentJobRepository
{
    /// <summary>
    /// Every job with its last run, its average successful run, whether it's running and when it
    /// runs next. Never throws for Azure SQL Database or a login that can't read msdb's job
    /// tables: the list then carries the reason.
    /// </summary>
    Task<AgentJobList> GetAgentJobsAsync();

    /// <summary>
    /// The runs of one job msdb still keeps, newest first, each with its steps.
    /// <paramref name="runningSince"/> is when the run in progress started, if the job is running.
    /// </summary>
    Task<IReadOnlyList<JobRun>> GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince);
}
