namespace SqlVitals.Engine.AgentJobs;

/// <summary>A run's outcome as msdb records it in <c>sysjobhistory.run_status</c>.</summary>
public enum JobOutcome
{
    Failed     = 0,
    Succeeded  = 1,
    Retry      = 2,
    Canceled   = 3,
    InProgress = 4,
}

/// <summary>What the monitor says about a job, most urgent first: the grid sorts by it.</summary>
public enum JobStatus
{
    /// <summary>Running now, for longer than its average successful run.</summary>
    RunningLong,

    /// <summary>Its last run failed.</summary>
    Failed,

    Running,
    Canceled,
    Succeeded,

    /// <summary>A last outcome other than the usual ones, e.g. a job outcome written as Retry.</summary>
    Other,

    /// <summary>No run is left in the history: new, never started, or its history was purged.</summary>
    NeverRun,
}

/// <summary>Whether SQL Server Agent, which runs the jobs, is running.</summary>
public enum AgentServiceState
{
    /// <summary>The login couldn't find out; jobs are shown as msdb has them.</summary>
    Unknown,
    Running,
    Stopped,

    /// <summary>Express edition: there is no Agent to start.</summary>
    NotInstalled,
}

/// <summary>
/// One job as the jobs query reads it from msdb, before anything is worked out: the raw
/// integer dates and statuses. <see cref="AgentJob.From"/> makes an <see cref="AgentJob"/> of it.
/// </summary>
/// <param name="LastRunStatus">run_status of the newest job outcome (step 0) row; null when none is left.</param>
/// <param name="AverageSeconds">Average run_duration of the successful runs in the history, in seconds.</param>
/// <param name="StartExecution">sysjobactivity for the current Agent session: when the running run started.</param>
/// <param name="ScheduleNextRun">The earliest enabled schedule's next_run_date * 1000000 + next_run_time, 0 for none.</param>
/// <param name="AgentStartSchedules">Enabled schedules that start the job when Agent starts (freq_type 64).</param>
/// <param name="IdleSchedules">Enabled schedules that start the job when the CPU is idle (freq_type 128).</param>
public sealed record AgentJobRow(
    Guid      JobId,
    string    Name,
    bool      Enabled,
    string?   Category,
    string?   Owner,
    string?   Description,
    int?      LastRunStatus,
    int       LastRunDate,
    int       LastRunTime,
    int       LastRunDuration,
    string?   LastMessage,
    int       Runs,
    int       FailedRuns,
    int       SucceededRuns,
    double?   AverageSeconds,
    DateTime? StartExecution,
    DateTime? StopExecution,
    DateTime? ActivityNextRun,
    long      ScheduleNextRun,
    int       EnabledSchedules,
    int       AgentStartSchedules,
    int       IdleSchedules);

/// <summary>
/// A SQL Agent job for the monitor (#39): its last run, whether it's running and for how long
/// against its average, and when it runs next. All times are the server's local time, as msdb
/// keeps them and as the job schedules are written.
/// </summary>
public sealed record AgentJob(
    Guid      JobId,
    string    Name,
    bool      Enabled,
    string?   Category,
    string?   Owner,
    string?   Description,
    JobOutcome? LastOutcome,
    DateTime? LastRunStart,
    TimeSpan? LastDuration,
    string?   LastMessage,
    int       Runs,
    int       FailedRuns,
    int       SucceededRuns,
    TimeSpan? AverageDuration,
    DateTime? RunningSince,
    TimeSpan? RunningFor,
    DateTime? NextRun,
    string    NextRunText)
{
    /// <summary>
    /// How far past its average a running job has to be to count as running long, so a few
    /// seconds either side of the average, normal for any job, don't turn it amber.
    /// </summary>
    public static readonly TimeSpan LongRunningMargin = TimeSpan.FromMinutes(1);

    public bool IsRunning => RunningSince is not null;

    /// <summary>Running for longer than its average successful run (by more than <see cref="LongRunningMargin"/>).</summary>
    public bool IsRunningLong =>
        RunningFor is { } elapsed && AverageDuration is { } average && elapsed > average + LongRunningMargin;

    /// <summary>The last run that finished failed, whether or not the job is running again now.</summary>
    public bool LastRunFailed => LastOutcome == JobOutcome.Failed;

    public JobStatus Status =>
        IsRunningLong ? JobStatus.RunningLong
        : IsRunning   ? JobStatus.Running
        : LastOutcome switch
        {
            null                 => JobStatus.NeverRun,
            JobOutcome.Failed    => JobStatus.Failed,
            JobOutcome.Canceled  => JobStatus.Canceled,
            JobOutcome.Succeeded => JobStatus.Succeeded,
            _                    => JobStatus.Other,
        };

    public string StatusText => Status switch
    {
        JobStatus.RunningLong => "Running long",
        JobStatus.Running     => "Running",
        JobStatus.Failed      => "Failed",
        JobStatus.Canceled    => "Canceled",
        JobStatus.Succeeded   => "Succeeded",
        JobStatus.NeverRun    => "Never run",
        _                     => OutcomeText(LastOutcome),
    };

    /// <summary>The status in a sentence, for the tooltip and the detail header.</summary>
    public string StatusDetail => Status switch
    {
        JobStatus.RunningLong =>
            $"Running for {MsdbTime.Format(RunningFor)}, {Ratio:0.0}× its average successful run of {MsdbTime.Format(AverageDuration)} " +
            $"({SucceededRuns:N0} run{(SucceededRuns == 1 ? "" : "s")} in the history).",
        JobStatus.Running when AverageDuration is null =>
            $"Running for {MsdbTime.Format(RunningFor)}. No successful run in the history to compare it with.",
        JobStatus.Running =>
            $"Running for {MsdbTime.Format(RunningFor)}; its average successful run takes {MsdbTime.Format(AverageDuration)}.",
        JobStatus.NeverRun => "No run of this job is left in msdb's job history.",
        _ => $"Last run {OutcomeText(LastOutcome).ToLowerInvariant()}" +
             (LastRunStart is { } start ? $" at {start:yyyy-MM-dd HH:mm:ss}" : "") +
             (LastDuration is { } d ? $" after {MsdbTime.Format(d)}." : "."),
    } + (IsRunning && LastRunFailed ? " The run before this one failed." : "");

    public string LastOutcomeText => LastOutcome is null ? string.Empty : OutcomeText(LastOutcome);

    /// <summary>Time so far while it runs, else how long the last run took.</summary>
    public TimeSpan? Duration => RunningFor ?? LastDuration;

    public string DurationText => MsdbTime.Format(Duration);

    public string AverageText => MsdbTime.Format(AverageDuration);

    /// <summary><see cref="Duration"/> over the average; null without an average, or one of zero.</summary>
    public double? Ratio =>
        Duration is { } d && AverageDuration is { TotalSeconds: > 0 } avg ? d.TotalSeconds / avg.TotalSeconds : null;

    public string RatioText => Ratio is { } r ? $"×{r:0.0}" : string.Empty;

    /// <summary>"2 of 30": failed runs among the runs msdb still keeps.</summary>
    public string FailuresText => Runs == 0 ? string.Empty : $"{FailedRuns:N0} of {Runs:N0}";

    public string EnabledText => Enabled ? "Yes" : "No";

    /// <summary>The owner, or a note that its SID no longer maps to a login, a common reason a job can't start.</summary>
    public string OwnerText => Owner ?? "(no such login)";

    public static string OutcomeText(JobOutcome? outcome) => outcome switch
    {
        JobOutcome.Failed     => "Failed",
        JobOutcome.Succeeded  => "Succeeded",
        JobOutcome.Retry      => "Retry",
        JobOutcome.Canceled   => "Canceled",
        JobOutcome.InProgress => "In progress",
        _                     => string.Empty,
    };

    public static JobOutcome? ToOutcome(int? runStatus) =>
        runStatus is >= 0 and <= 4 ? (JobOutcome)runStatus.Value : null;

    /// <summary>
    /// Works out the job from what msdb holds. <paramref name="serverNow"/> is the server's local
    /// time, which msdb's times are in. A run sysjobactivity still shows as started while Agent is
    /// stopped was cut short when Agent stopped, so the job doesn't count as running then.
    /// </summary>
    public static AgentJob From(AgentJobRow row, DateTime serverNow, AgentServiceState agent)
    {
        var running = row.StartExecution is { } started && row.StopExecution is null
                      && agent is not (AgentServiceState.Stopped or AgentServiceState.NotInstalled)
            ? started
            : (DateTime?)null;
        var runningFor = running is { } since ? Max(serverNow - since, TimeSpan.Zero) : (TimeSpan?)null;

        var lastOutcome = ToOutcome(row.LastRunStatus);
        var lastStart   = lastOutcome is null ? null : MsdbTime.ToDateTime(row.LastRunDate, row.LastRunTime);

        var (nextRun, nextRunText) = NextRunOf(row, serverNow, running is not null);

        return new AgentJob(
            row.JobId, row.Name, row.Enabled, row.Category, row.Owner, row.Description,
            lastOutcome,
            lastStart,
            lastOutcome is null ? null : MsdbTime.ToDuration(row.LastRunDuration),
            row.LastMessage,
            row.Runs, row.FailedRuns, row.SucceededRuns,
            row.SucceededRuns > 0 && row.AverageSeconds is { } avg ? TimeSpan.FromSeconds(Math.Round(avg)) : null,
            running, runningFor,
            nextRun, nextRunText);
    }

    /// <summary>
    /// When the job runs next, and how the grid says it. Agent refreshes sysjobschedules only
    /// every 20 minutes, while sysjobactivity has what it planned after the last run, so the
    /// earliest of the two still ahead is taken. With neither ahead, the later one is shown as
    /// due: Agent is behind, or stopped.
    /// </summary>
    internal static (DateTime? NextRun, string Text) NextRunOf(AgentJobRow row, DateTime serverNow, bool running)
    {
        if (!row.Enabled)
            return (null, "Job disabled");

        if (row.EnabledSchedules == 0)
            return (null, "Not scheduled");

        DateTime?[] candidates = [row.ActivityNextRun, MsdbTime.FromDateTimeNumber(row.ScheduleNextRun)];
        var known  = candidates.OfType<DateTime>().ToList();
        var ahead  = known.Where(t => t >= serverNow).ToList();
        var next   = ahead.Count > 0 ? ahead.Min() : known.Count > 0 ? known.Max() : (DateTime?)null;

        if (next is { } t)
            return (t, $"{t:yyyy-MM-dd HH:mm:ss}" + (t < serverNow && !running ? " (due)" : ""));
        if (row.AgentStartSchedules > 0)
            return (null, "When Agent starts");
        if (row.IdleSchedules > 0)
            return (null, "When the CPU is idle");
        return (null, "No next run");
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>The jobs in msdb, or why they couldn't be read.</summary>
/// <param name="Jobs">Most urgent first (<see cref="JobStatus"/>), then by name.</param>
/// <param name="ServerNow">The server's local time when they were read.</param>
/// <param name="Problem">Why the list is empty, or something that makes it less than it seems (Agent stopped).</param>
public sealed record AgentJobList(
    IReadOnlyList<AgentJob> Jobs,
    AgentServiceState Agent,
    DateTime? ServerNow,
    string? Problem)
{
    /// <summary>False when nothing could be read (Azure SQL Database, no msdb permission).</summary>
    public bool Available { get; init; } = true;

    public static AgentJobList Unavailable(string problem) =>
        new([], AgentServiceState.Unknown, null, problem) { Available = false };

    public int FailedCount      => Jobs.Count(j => j.LastRunFailed);
    public int RunningCount     => Jobs.Count(j => j.IsRunning);
    public int RunningLongCount => Jobs.Count(j => j.IsRunningLong);
    public int DisabledCount    => Jobs.Count(j => !j.Enabled);

    /// <summary>Running long first, then failed, running, …; by name within each.</summary>
    public static IReadOnlyList<AgentJob> Sort(IEnumerable<AgentJob> jobs) => jobs
        .OrderBy(j => j.Status)
        .ThenBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>SERVERPROPERTY('EngineEdition') of SQL Server Express.</summary>
    internal const int ExpressEdition = 4;

    /// <summary>
    /// Whether Agent runs. <paramref name="serviceStatus"/> is sys.dm_server_services' status_desc,
    /// null when that isn't readable (no VIEW SERVER STATE, Managed Instance). Then the
    /// <c>Agent XPs</c> option, which Agent turns on as it starts and off as it stops, tells.
    /// </summary>
    internal static AgentServiceState AgentState(int edition, int? agentXps, string? serviceStatus)
    {
        if (edition == ExpressEdition)
            return AgentServiceState.NotInstalled;

        return serviceStatus switch
        {
            "Running" => AgentServiceState.Running,
            "Stopped" => AgentServiceState.Stopped,
            null      => agentXps switch
            {
                0 => AgentServiceState.Stopped,
                1 => AgentServiceState.Running,
                _ => AgentServiceState.Unknown,
            },
            // Start pending, stop pending, paused.
            _ => AgentServiceState.Unknown,
        };
    }

    /// <summary>What a stopped or missing Agent means for the jobs listed; null when it's running.</summary>
    internal static string? AgentProblem(AgentServiceState agent) => agent switch
    {
        AgentServiceState.Stopped =>
            "SQL Server Agent isn't running, so no job starts on its schedule until it's started again " +
            "(SQL Server Configuration Manager, or SQL Server Agent in SSMS Object Explorer).",
        AgentServiceState.NotInstalled =>
            "SQL Server Express has no SQL Server Agent: jobs in msdb never run on a schedule here. " +
            "Windows Task Scheduler running sqlcmd is the usual stand-in.",
        _ => null,
    };
}
