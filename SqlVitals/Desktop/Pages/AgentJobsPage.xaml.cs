using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// SQL Server Agent jobs from msdb (#39): each job's last run, duration against its average and
/// next run, with failed jobs and jobs running longer than usual first. Selecting a job shows
/// its runs, and the steps and error messages of each. Not reachable on Azure SQL Database,
/// which has no Agent: MainWindow disables the button, and the repository says so too.
/// </summary>
public partial class AgentJobsPage : Page, IRefreshable
{
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

    private readonly IWaitStatsRepository _repo;

    // What was read last; changing the filter only filters it again.
    private AgentJobList? _jobs;

    // Set once the filter choices are filled in, so filling them doesn't filter.
    private readonly bool _ready;

    private bool _refreshing;

    // Bumped per job selection, so the runs of a job selected earlier don't land on a later one.
    private int _runsVersion;

    // While Show() replaces the rows: the job selected before, and the start of the run open in it.
    private bool _showing;
    private (Guid JobId, DateTime? RunStart)? _keep;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

    public AgentJobsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

        _timer.Tick += Timer_Tick;
        Loaded   += (_, _) => { if (ChkAutoRefresh.IsChecked == true) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();

        _ready = true;
    }

    /// <summary>Reads the jobs from msdb. Called on navigate, by the sidebar Refresh and every 30 s when chosen.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        BtnReload.IsEnabled = false;
        if (_jobs is null)
            TxtStatus.Text = "Reading SQL Agent jobs from msdb…";
        try
        {
            _jobs = await _repo.GetAgentJobsAsync();
            Show();
        }
        finally
        {
            _refreshing = false;
            BtnReload.IsEnabled = true;
        }
    }

    private void Show()
    {
        if (_jobs is not { } list) return;

        var filter  = Filters[Math.Max(0, CmbFilter.SelectedIndex)];
        var shown   = list.Jobs.Where(filter.Keep).ToList();

        // Keep the same job, and the run open in it, across a refresh; otherwise the first job, the most urgent.
        var selected = JobsGrid.SelectedItem as AgentJob;
        _keep = selected is null ? null : (selected.JobId, (RunsGrid.SelectedItem as JobRun)?.Start);
        _showing = true;
        try
        {
            DataGridRefresh.SetItemsSource(JobsGrid, shown);
            JobsGrid.SelectedItem = shown.FirstOrDefault(j => j.JobId == selected?.JobId) ?? shown.FirstOrDefault();
        }
        finally
        {
            _showing = false;
        }

        TxtCount.Text = !list.Available ? "Jobs"
            : shown.Count == list.Jobs.Count ? $"{list.Jobs.Count:N0} job{(list.Jobs.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Jobs.Count:N0} jobs";

        ShowSummary(list);

        var problem = list.Available ? list.Problem : null;
        TxtProblem.Text       = problem ?? string.Empty;
        TxtProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = StatusText(list);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // "42 jobs · 3 failed · 2 running, 1 longer than usual · 5 disabled", the counts to act on in colour.
    private void ShowSummary(AgentJobList list)
    {
        TxtSummary.Inlines.Clear();
        if (!list.Available)
        {
            TxtSummary.Visibility = Visibility.Collapsed;
            return;
        }
        TxtSummary.Visibility = Visibility.Visible;

        void Add(string text, Brush? brush = null, bool bold = false)
        {
            var run = new Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            if (brush is not null) run.Foreground = brush;
            TxtSummary.Inlines.Add(run);
        }
        var danger  = (Brush)FindResource("Danger");
        var warning = (Brush)FindResource("Warning");
        var muted   = (Brush)FindResource("TextMuted");

        Add($"{list.Jobs.Count:N0} job{(list.Jobs.Count == 1 ? "" : "s")}", bold: true);
        Add("  ·  ", muted);
        Add($"{list.FailedCount:N0} failed", list.FailedCount > 0 ? danger : null, list.FailedCount > 0);
        Add("  ·  ", muted);
        Add($"{list.RunningCount:N0} running");
        if (list.RunningLongCount > 0)
            Add($", {list.RunningLongCount:N0} longer than usual", warning, bold: true);
        Add("  ·  ", muted);
        Add($"{list.DisabledCount:N0} disabled", muted);
    }

    private static string StatusText(AgentJobList list)
    {
        if (!list.Available)
            return string.Empty;

        var agent = list.Agent switch
        {
            AgentServiceState.Running      => "running",
            AgentServiceState.Stopped      => "stopped",
            AgentServiceState.NotInstalled => "not part of Express",
            _                              => "state unknown (reading it needs VIEW SERVER STATE)",
        };
        return $"Read at {DateTime.Now:HH:mm:ss}. SQL Server Agent: {agent}. Times are the server's local time, as msdb " +
               $"keeps them and the schedules are written" +
               (list.ServerNow is { } now ? $" (it was {now:yyyy-MM-dd HH:mm} there)." : ".") +
               " Averages and failure counts cover the runs msdb's job history still keeps.";
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    // The sidebar Refresh reports errors through MainWindow; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e) => await ReadAgainAsync();

    private async void Timer_Tick(object? sender, EventArgs e) => await ReadAgainAsync();

    private async Task ReadAgainAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent Jobs: read failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    private void ChkAutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkAutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    // ── Selected job ──────────────────────────────────────────────────────────

    private async void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show() replaces the rows, which clears the selection before it selects the job again.
        if (_showing && JobsGrid.SelectedItem is null)
            return;

        var version = ++_runsVersion;

        if (JobsGrid.SelectedItem is not AgentJob job)
        {
            DetailPanel.IsEnabled = false;
            TxtDetailHeader.Text  = "Select a job above to see its runs.";
            TxtDetailSub.Text     = string.Empty;
            RunsGrid.ItemsSource  = null;
            return;
        }

        DetailPanel.IsEnabled = true;
        TxtDetailHeader.Text  = $"{job.Name} · {job.StatusText}";
        TxtDetailSub.Text     = DetailText(job);

        // The same job after a refresh: open the run that was open.
        DateTime? keepRun = _showing && _keep is { } keep && keep.JobId == job.JobId ? keep.RunStart : null;

        IReadOnlyList<JobRun> runs;
        try
        {
            runs = await _repo.GetAgentJobRunsAsync(job.JobId, job.RunningSince);
        }
        catch (Exception ex)
        {
            AppLog.Error("Agent Jobs: reading a job's runs failed", ex);
            if (version != _runsVersion) return;
            RunsGrid.ItemsSource = null;
            TxtDetailSub.Text    = "Its runs could not be read: " + MainWindow.FormatErrorStatus(ex);
            return;
        }
        if (version != _runsVersion) return;

        RunsGrid.ItemsSource = runs;
        TxtRunsLabel.Text    = runs.Count == 0
            ? "msdb keeps no runs of this job"
            : $"{runs.Count:N0} run{(runs.Count == 1 ? "" : "s")} msdb keeps, newest first";
        RunsGrid.SelectedItem = (keepRun is null ? null : runs.FirstOrDefault(r => r.Start == keepRun)) ?? runs.FirstOrDefault();
    }

    private static string DetailText(AgentJob job)
    {
        var parts = new List<string> { job.StatusDetail };
        parts.Add(job.NextRun is not null ? $"Next run: {job.NextRunText}." : $"{job.NextRunText}.");
        if (job.Owner is null)
            parts.Add("Its owner isn't a login on this server any more, which can stop the job from starting.");
        else
            parts.Add($"Owner {job.Owner}.");
        if (!string.IsNullOrWhiteSpace(job.Description) && job.Description != "No description available.")
            parts.Add(job.Description.Trim());
        return string.Join(" ", parts);
    }

    private void RunsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not JobRun run)
        {
            StepsGrid.ItemsSource = null;
            TxtMessage.Text       = string.Empty;
            return;
        }

        StepsGrid.ItemsSource  = run.Steps;
        // The failing step is the one to read; otherwise the last step that ran.
        StepsGrid.SelectedItem = run.FailedStep ?? run.Steps.LastOrDefault();
        if (StepsGrid.SelectedItem is null)
            TxtMessage.Text = run.Message ?? string.Empty;
    }

    private void StepsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepsGrid.SelectedItem is JobStepRun step)
            TxtMessage.Text = $"-- Step {step.StepId}: {step.StepName} ({step.OutcomeText}" +
                              (step.Start is { } start ? $", started {start:yyyy-MM-dd HH:mm:ss}" : "") +
                              $", took {step.DurationText})\n{step.Message}";
        else if (RunsGrid.SelectedItem is JobRun run)
            TxtMessage.Text = run.Message ?? string.Empty;
    }
}
