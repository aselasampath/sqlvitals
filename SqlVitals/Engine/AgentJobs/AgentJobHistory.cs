namespace SqlVitals.Engine.AgentJobs;

/// <summary>One <c>msdb.dbo.sysjobhistory</c> row: a step's run, or with step 0 the job's outcome.</summary>
public sealed record JobHistoryRow(
    int     InstanceId,
    int     StepId,
    string? StepName,
    int     RunStatus,
    int     RunDate,
    int     RunTime,
    int     RunDuration,
    string? Message);

/// <summary>One step of a job run.</summary>
public sealed record JobStepRun(
    int         StepId,
    string      StepName,
    JobOutcome? Outcome,
    DateTime?   Start,
    TimeSpan    Duration,
    string?     Message)
{
    public string OutcomeText  => AgentJob.OutcomeText(Outcome);
    public string DurationText => MsdbTime.Format(Duration);
    public bool   IsFailed     => Outcome == JobOutcome.Failed;
}

/// <summary>
/// One run of a job: its outcome (the step 0 row) and the steps that ran in it. A run that has
/// no outcome yet is the one running now (<paramref name="IsRunning"/>), or one cut short when
/// SQL Server Agent stopped.
/// </summary>
public sealed record JobRun(
    DateTime?   Start,
    JobOutcome? Outcome,
    TimeSpan?   Duration,
    string?     Message,
    IReadOnlyList<JobStepRun> Steps,
    bool        IsRunning = false)
{
    public bool HasOutcome => Outcome is not null;

    public string OutcomeText  => HasOutcome ? AgentJob.OutcomeText(Outcome) : IsRunning ? "Running" : "No outcome";
    public string DurationText => MsdbTime.Format(Duration);
    public bool   IsFailed     => Outcome == JobOutcome.Failed;

    /// <summary>The first step that failed in this run, which says why the job failed.</summary>
    public JobStepRun? FailedStep => Steps.FirstOrDefault(s => s.IsFailed);

    /// <summary>For a failed run, the failing step and its error; else the job's own message.</summary>
    public string Detail => FailedStep is { } step
        ? $"Step {step.StepId} ({step.StepName}): {step.Message}"
        : Message ?? string.Empty;
}

/// <summary>Groups a job's history rows into runs (#39).</summary>
public static class AgentJobHistory
{
    /// <summary>Most history rows read for one job: its runs and their steps.</summary>
    public const int MaxRows = 2000;

    private const string CutShort =
        "No job outcome was recorded for these steps: the run was cut short, e.g. when SQL Server Agent stopped.";

    /// <summary>
    /// Agent writes each step's row as the step ends and the job's outcome (step 0) last, so the
    /// steps of a run are the rows since the previous outcome row that started no earlier than
    /// the run did; earlier ones were left by a run cut short. Rows after the newest outcome
    /// belong to <paramref name="runningSince"/>'s run while the job runs. Newest run first;
    /// steps in the order they ran.
    /// </summary>
    public static IReadOnlyList<JobRun> GroupRuns(IEnumerable<JobHistoryRow> rows, DateTime? runningSince = null)
    {
        var runs  = new List<JobRun>();
        var steps = new List<JobStepRun>();

        foreach (var row in rows.OrderBy(r => r.InstanceId))
        {
            var start = MsdbTime.ToDateTime(row.RunDate, row.RunTime);
            if (row.StepId != 0)
            {
                steps.Add(new JobStepRun(row.StepId, row.StepName ?? $"Step {row.StepId}",
                                         AgentJob.ToOutcome(row.RunStatus), start,
                                         MsdbTime.ToDuration(row.RunDuration), row.Message));
                continue;
            }

            var own = SplitOffCutShort(steps, start, runs);
            runs.Add(new JobRun(start, AgentJob.ToOutcome(row.RunStatus), MsdbTime.ToDuration(row.RunDuration),
                                row.Message, own));
            steps = [];
        }

        if (runningSince is not null)
            runs.Add(new JobRun(runningSince, null, null, "Running now.", SplitOffCutShort(steps, runningSince, runs), IsRunning: true));
        else if (steps.Count > 0)
            runs.Add(new JobRun(steps[0].Start, null, null, CutShort, steps));

        runs.Reverse();
        return runs;
    }

    // Steps that started before the run did can't be its own: they're added to runs as a cut-short run.
    private static List<JobStepRun> SplitOffCutShort(List<JobStepRun> steps, DateTime? runStart, List<JobRun> runs)
    {
        if (runStart is not { } start)
            return steps;

        var earlier = steps.TakeWhile(s => s.Start is { } t && t < start).ToList();
        if (earlier.Count == 0)
            return steps;

        runs.Add(new JobRun(earlier[0].Start, null, null, CutShort, earlier));
        return steps.Skip(earlier.Count).ToList();
    }
}
