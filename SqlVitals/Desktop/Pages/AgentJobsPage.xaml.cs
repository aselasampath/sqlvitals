using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// SQL Server Agent jobs from msdb (#39): each job's last run, duration against its average and
/// next run, with failed jobs and jobs running longer than usual first. Selecting a job shows
/// its runs, and the steps and error messages of each. Not reachable on Azure SQL Database,
/// which has no Agent: MainWindow disables the button, and the repository says so too. On the page
/// template (#150): the verdict names failed and long-running jobs; a job's details and the
/// selected step's message are in the drawer.
/// </summary>
public partial class AgentJobsPage : Page, IRefreshable, IPageContext, IDetailsSource
{
    // ── The details drawer (#150): the selected job, and its selected step's message ──
    private const string SelectHint = "Select a job to see what happened, its next run and owner, and the message of the step selected below.";

    private readonly RowDrawer _drawer = new("Job details", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowJobDetails(bool open)
    {
        if (JobsGrid.SelectedItem is not AgentJob job)
        {
            _drawer.Clear();
            return;
        }
        var run  = RunsGrid.SelectedItem as JobRun;
        var step = StepsGrid.SelectedItem as JobStepRun;
        _drawer.Show(job.Name, RowDetails.Rows(job), open,
                     step is not null ? "Message of the selected step" : "Message of the selected run",
                     RowDetails.Message(run, step), noText: run is null ? null : "(no message)");
    }

    // The page template's verdict (#150): failed and long-running jobs, or Agent stopped.
    internal AgentJobsViewModel ViewModel { get; }

    private const int NeedsAttentionFilter = 1;

    // A snapshot, or live while "Auto-refresh every 30 s" is ticked (#140).
    public PageKind Kind => ChkAutoRefresh.IsChecked == true ? PageKind.Live : PageKind.Snapshot;
    public TimeSpan? LiveInterval => _timer.Interval;

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

    /// <param name="folds">Where the verdict's and the source's folds are remembered; none in some tests.</param>
    public AgentJobsPage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new AgentJobsViewModel(showJob: ShowSubject, needsAttention: () => CmbFilter.SelectedIndex = NeedsAttentionFilter);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;

        // A click on a job opens a closed drawer; a selection moved by the keyboard or a refresh only fills it.
        JobsGrid.AddHandler(MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(JobsGrid, source) is DataGridRow)
                ShowJobDetails(open: true);
        }), handledEventsToo: true);

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

        _timer.Tick += Timer_Tick;
        Loaded   += (_, _) => { if (ChkAutoRefresh.IsChecked == true) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();

        _ready = true;
    }

    /// <summary>Reads the jobs from msdb. Called on navigate, by the top bar's Refresh and every 30 s when chosen.</summary>
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
        catch (Exception ex) when (_jobs is null)
        {
            // Nothing read yet: the verdict says why. Later, the jobs read last stay shown.
            ViewModel.ShowError("the SQL Agent jobs", ex);
            throw;
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

        ViewModel.Show(list);
        SourceSection.FoldedSummary = !list.Available ? "Not read"
            : $"Agent {AgentText(list.Agent)} · read at {DateTime.Now:HH:mm:ss}" + (list.Problem is null ? "" : " · see the note");

        var problem = list.Available ? list.Problem : null;
        TxtProblem.Text       = problem ?? string.Empty;
        TxtProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = StatusText(list);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The verdict's Show its runs: the job it names, selected (with every job listed if a filter
    // hides it), its runs below and its details in the drawer.
    private void ShowSubject()
    {
        if (_jobs is null || AgentJobsVerdict.Subject(_jobs) is not { } subject)
            return;
        if (!JobsGrid.Items.OfType<AgentJob>().Any(j => j.JobId == subject.JobId))
        {
            CmbFilter.SelectedIndex = 0;
            JobsFilter.Clear();
        }
        if (JobsGrid.Items.OfType<AgentJob>().FirstOrDefault(j => j.JobId == subject.JobId) is not { } row)
            return;
        JobsGrid.SelectedItem = row;
        JobsGrid.ScrollIntoView(row);
        ShowJobDetails(open: true);
    }

    private static string AgentText(AgentServiceState agent) => agent switch
    {
        AgentServiceState.Running      => "running",
        AgentServiceState.Stopped      => "stopped",
        AgentServiceState.NotInstalled => "not part of Express",
        _                              => "state unknown",
    };

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

    // The top bar's Refresh reports errors through MainWindow; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e) => await ReadAgainAsync();

    private async void Timer_Tick(object? sender, EventArgs e) => await ReadAgainAsync();

    private async Task ReadAgainAsync()
    {
        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
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
        MainWindow.ReportContextChanged(this);
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
            ShowRunsProblem(null);
            RunsGrid.ItemsSource  = null;
            ShowJobDetails(open: false);
            return;
        }

        DetailPanel.IsEnabled = true;
        TxtDetailHeader.Text  = $"{job.Name} · {job.StatusText}";
        ShowRunsProblem(null);
        RunsGrid.ItemsSource  = null;   // the job selected before's runs and steps go, so the drawer can't show its message
        ShowJobDetails(open: false);

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
            ShowRunsProblem("Its runs could not be read: " + MainWindow.FormatErrorStatus(ex));
            return;
        }
        if (version != _runsVersion) return;

        RunsGrid.ItemsSource = runs;
        TxtRunsLabel.Text    = runs.Count == 0
            ? "msdb keeps no runs of this job"
            : $"{runs.Count:N0} run{(runs.Count == 1 ? "" : "s")} msdb keeps, newest first";
        RunsGrid.SelectedItem = (keepRun is null ? null : runs.FirstOrDefault(r => r.Start == keepRun)) ?? runs.FirstOrDefault();
    }

    // Under the job's name: why its runs aren't listed, when they couldn't be read.
    private void ShowRunsProblem(string? problem)
    {
        TxtDetailSub.Text       = problem ?? string.Empty;
        TxtDetailSub.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RunsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunsGrid.SelectedItem is not JobRun run)
        {
            StepsGrid.ItemsSource = null;
            ShowJobDetails(open: false);
            return;
        }

        StepsGrid.ItemsSource  = run.Steps;
        // The failing step is the one to read; otherwise the last step that ran.
        StepsGrid.SelectedItem = run.FailedStep ?? run.Steps.LastOrDefault();
        ShowJobDetails(open: false);
    }

    // The step's full message, in the drawer under the job (#150).
    private void StepsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowJobDetails(open: false);
}
