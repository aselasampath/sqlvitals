using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// SQL Server Agent jobs from msdb (#39, #186): each job's last run, duration against its average
/// and next run, with failed jobs and jobs running longer than usual first. Selecting a job reads
/// its runs, and the steps and messages of each. Read once and on Refresh, or every 30 s while
/// ticked. The verdict names failed and long-running jobs, or Agent stopped (#150). Not reachable
/// on Azure SQL Database, which has no Agent: the sidebar disables it, and the repository says so too.
/// </summary>
public sealed partial class AgentJobsViewModel(IAgentJobRepository repo, TimeProvider? time = null) : PageViewModel
{
    /// <summary>How often it reads while <see cref="AutoRefresh"/> is ticked.</summary>
    public static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>The filter the verdict's "Show only these" picks.</summary>
    internal const int NeedsAttentionFilter = 1;

    private static readonly (string Label, Func<AgentJob, bool> Keep, string Empty)[] Filters =
    [
        ("All jobs",                                _ => true,
            "There are no SQL Agent jobs on this server."),
        ("Needs attention: failed or running long", j => j.LastRunFailed || j.IsRunningLong,
            "Nothing needs attention: no job's last run failed, and no running job has gone past its average."),
        ("Running now",                             j => j.IsRunning,
            "No job is running now."),
        ("Last run failed",                         j => j.LastRunFailed,
            "No job's last run failed."),
        ("Enabled jobs",                            j => j.Enabled,
            "Every job on this server is disabled."),
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // What was read last; changing the filter only filters it again.
    private AgentJobList? _jobs;

    // While the rows are replaced: the grid clears its selection before the job is selected again.
    private bool _showing;

    // ── The list ──────────────────────────────────────────────────────────

    /// <summary>Which jobs to list: the combo's choices.</summary>
    public IReadOnlyList<string> FilterNames { get; } = Filters.Select(f => f.Label).ToList();

    [ObservableProperty]
    private int _filterIndex;

    partial void OnFilterIndexChanged(int value) => Show();

    /// <summary>Read again every 30 s while the page is shown (#140): the page is live then.</summary>
    [ObservableProperty]
    private bool _autoRefresh;

    partial void OnAutoRefreshChanged(bool value)
    {
        RefreshInterval = value ? AutoRefreshInterval : null;
        ReportContextChanged();
    }

    /// <summary>The jobs the filter keeps, the ones to act on first.</summary>
    public GridRows<AgentJob> Jobs { get; } = [];

    /// <summary>"14 jobs", "2 of 14 jobs".</summary>
    [ObservableProperty]
    private string _countText = "Jobs";

    /// <summary>Something that left jobs or history out, though the list was read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProblem))]
    private string? _problem;

    public bool ShowsProblem => Problem is not null;

    /// <summary>When it was read, Agent's state and how to read the times; "Reading…" the first time.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    /// <summary>Folded, Agent's state and when it was read in one line.</summary>
    [ObservableProperty]
    private string? _sourceSummary;

    /// <summary>Why no job is listed: none matches the filter, or msdb couldn't be read.</summary>
    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>A job to show: the verdict's Show its runs selected it. The view scrolls to it and opens the drawer.</summary>
    public event Action<AgentJob>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (_jobs is null)
            ReadNote = "Reading SQL Agent jobs from msdb…";
        _jobs   = await repo.GetAgentJobsAsync(cancel);
        Verdict = AgentJobsVerdict.From(_jobs);
        Show();
    }

    // Nothing read yet: the verdict says why. Later, the jobs read last stay shown, and the top bar says it failed.
    protected override void ShowLoadError(Exception exception)
    {
        if (_jobs is not null)
            return;
        ReadNote = string.Empty;
        ShowError("the SQL Agent jobs", exception);
    }

    private void Show()
    {
        if (_jobs is not { } list)
            return;

        var filter = Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)];
        var shown  = list.Jobs.Where(filter.Keep).ToList();

        // Keep the same job, and the run and step open in it, across a refresh; otherwise the first job, the most urgent.
        var selected = SelectedJob;
        var keep     = new Place(SelectedRun?.Start, SelectedStep?.StepId);
        _showing = true;
        try
        {
            Jobs.Replace(shown);
            SelectedJob = shown.FirstOrDefault(j => j.JobId == selected?.JobId) ?? shown.FirstOrDefault();
        }
        finally
        {
            _showing = false;
        }
        ShowJob(SelectedJob, SelectedJob is not null && SelectedJob.JobId == selected?.JobId ? keep : null);

        var now = _time.GetLocalNow();
        CountText = !list.Available ? "Jobs"
            : shown.Count == list.Jobs.Count ? $"{list.Jobs.Count:N0} job{(list.Jobs.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Jobs.Count:N0} jobs";
        SourceSummary = !list.Available ? "Not read"
            : $"Agent {AgentText(list.Agent)} · read at {now:HH:mm:ss}" + (list.Problem is null ? "" : " · see the note");
        Problem     = list.Available ? list.Problem : null;
        ReadNote    = StatusText(list, now);
        NoDataText  = !list.Available ? list.Problem : filter.Empty;
        ShowsNoData = shown.Count == 0;
    }

    private static string AgentText(AgentServiceState agent) => agent switch
    {
        AgentServiceState.Running      => "running",
        AgentServiceState.Stopped      => "stopped",
        AgentServiceState.NotInstalled => "not part of Express",
        _                              => "state unknown",
    };

    private static string StatusText(AgentJobList list, DateTimeOffset now)
    {
        if (!list.Available)
            return string.Empty;

        var agent = list.Agent == AgentServiceState.Unknown ? "state unknown (reading it needs VIEW SERVER STATE)" : AgentText(list.Agent);
        return $"Read at {now:HH:mm:ss}. SQL Server Agent: {agent}. Times are the server's local time, as msdb " +
               $"keeps them and the schedules are written" +
               (list.ServerNow is { } serverNow ? $" (it was {serverNow:yyyy-MM-dd HH:mm} there)." : ".") +
               " Averages and failure counts cover the runs msdb's job history still keeps.";
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case AgentJobsVerdict.ShowJob:        ShowSubject();                       break;
            case AgentJobsVerdict.NeedsAttention: FilterIndex = NeedsAttentionFilter; break;
        }
    }

    // The verdict's Show its runs: the job it names, selected (with every job listed if the filter
    // hides it), its runs below and its details in the drawer.
    private void ShowSubject()
    {
        if (_jobs is null || AgentJobsVerdict.Subject(_jobs) is not { } subject)
            return;
        if (!Jobs.Any(j => j.JobId == subject.JobId))
            FilterIndex = 0;
        if (Jobs.FirstOrDefault(j => j.JobId == subject.JobId) is not { } row)
            return;
        SelectedJob = row;
        ShowRow?.Invoke(row);
    }

    // ── The job selected: its runs, and the steps of the run selected ─────

    /// <summary>The job whose runs are listed; kept across a refresh by its id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJob))]
    private AgentJob? _selectedJob;

    public bool HasJob => SelectedJob is not null;

    // Replacing the rows clears the grid's selection; the job is selected again after.
    partial void OnSelectedJobChanged(AgentJob? value)
    {
        if (!_showing)
            ShowJob(value, keep: null);
    }

    private const string NoneSelected = "Select a job above to see its runs.";

    /// <summary>The job's name and state, over its runs.</summary>
    [ObservableProperty]
    private string _detailHeader = NoneSelected;

    /// <summary>Why its runs aren't listed, when they couldn't be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRunsProblem))]
    private string? _runsProblem;

    public bool ShowsRunsProblem => RunsProblem is not null;

    [ObservableProperty]
    private string _runsLabel = "Runs msdb keeps, newest first";

    /// <summary>The runs msdb keeps of the job selected, newest first.</summary>
    public GridRows<JobRun> Runs { get; } = [];

    /// <summary>The run whose steps are listed; the newest, or the one open before a refresh.</summary>
    [ObservableProperty]
    private JobRun? _selectedRun;

    partial void OnSelectedRunChanged(JobRun? value)
    {
        Steps.Replace(value?.Steps ?? []);
        // The failing step is the one to read; otherwise the last step that ran.
        SelectedStep = value is null ? null : value.FailedStep ?? (value.Steps.Count > 0 ? value.Steps[^1] : null);
    }

    /// <summary>The steps of the run selected.</summary>
    public GridRows<JobStepRun> Steps { get; } = [];

    /// <summary>The step whose full message is in the drawer.</summary>
    [ObservableProperty]
    private JobStepRun? _selectedStep;

    /// <summary>The runs' read for the job selected last; done when there's none.</summary>
    internal Task RunsRead { get; private set; } = Task.CompletedTask;

    // Cancels the runs' read of the job selected before: a new selection, or leaving the page.
    private Action? _cancelRuns;

    // Where the user was in a job's runs: the run open, and its step selected.
    private sealed record Place(DateTime? RunStart, int? StepId);

    /// <param name="keep">The same job again, after a refresh: its runs stay while they are read again,
    /// then the run and step open before are selected again.</param>
    private void ShowJob(AgentJob? job, Place? keep)
    {
        CancelRuns();
        RunsProblem = null;
        // Another job: the one before's runs and steps go, so the drawer can't show its message.
        if (keep is null)
        {
            Runs.Replace([]);
            SelectedRun = null;
        }

        if (job is null)
        {
            DetailHeader = NoneSelected;
            RunsRead     = Task.CompletedTask;
            return;
        }

        DetailHeader = $"{job.Name} · {job.StatusText}";
        RunsRead     = ReadRunsAsync(job, keep);
    }

    private void CancelRuns()
    {
        _cancelRuns?.Invoke();
        _cancelRuns = null;
    }

    // Never throws: a failure is said under the job's name, and logged.
    private async Task ReadRunsAsync(AgentJob job, Place? keep)
    {
        var read = new CancellationTokenSource();
        Action cancel = read.Cancel;
        _cancelRuns = cancel;
        IReadOnlyList<JobRun> runs;
        try
        {
            runs = await repo.GetAgentJobRunsAsync(job.JobId, job.RunningSince, read.Token);
        }
        catch (Exception) when (read.IsCancellationRequested)
        {
            // Another job was selected, or the page left: nothing to say.
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent Jobs: reading a job's runs failed", ex);
            RunsProblem = "Its runs could not be read: " + ErrorStatus.Format(ex);
            return;
        }
        finally
        {
            // Done: nothing cancels it any more.
            if (ReferenceEquals(_cancelRuns, cancel))
                _cancelRuns = null;
            read.Dispose();
        }
        if (read.IsCancellationRequested)
            return;

        Runs.Replace(runs);
        RunsLabel = runs.Count == 0
            ? "msdb keeps no runs of this job"
            : $"{runs.Count:N0} run{(runs.Count == 1 ? "" : "s")} msdb keeps, newest first";
        SelectedRun = (keep is null ? null : runs.FirstOrDefault(r => r.Start == keep.RunStart)) ?? (runs.Count > 0 ? runs[0] : null);
        if (keep?.StepId is { } step && SelectedRun?.Start == keep.RunStart && Steps.FirstOrDefault(s => s.StepId == step) is { } kept)
            SelectedStep = kept;
    }

    protected override void OnLeaving() => CancelRuns();
}
