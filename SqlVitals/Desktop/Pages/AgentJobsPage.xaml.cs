using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// SQL Agent Jobs, bound to <see cref="AgentJobsViewModel"/> (#39, #186): the code-behind fills
/// the details drawer with the job selected and its selected step's message, and scrolls the job
/// the verdict shows into view.
/// </summary>
public partial class AgentJobsPage : Page, IPageContext, IDetailsSource
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
        if (ViewModel.SelectedJob is not { } job)
        {
            _drawer.Clear();
            return;
        }
        var run  = ViewModel.SelectedRun;
        var step = ViewModel.SelectedStep;
        _drawer.Show(job.Name, RowDetails.Rows(job), open,
                     step is not null ? "Message of the selected step" : "Message of the selected run",
                     RowDetails.Message(run, step), noText: run is null ? null : "(no message)");
    }

    // A snapshot, or live while "Every 30 s" is ticked (#140).
    public PageKind Kind => ViewModel.AutoRefresh ? PageKind.Live : PageKind.Snapshot;
    public TimeSpan? LiveInterval => AgentJobsViewModel.AutoRefreshInterval;

    internal AgentJobsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the source's folds are remembered; none in some tests.</param>
    public AgentJobsPage(AgentJobsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ShowRow += ShowSubject;

        // A click on a job opens a closed drawer; a selection moved by the keyboard or a refresh only fills it.
        JobsGrid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(JobsGrid, source) is DataGridRow)
                ShowJobDetails(open: true);
        }), handledEventsToo: true);
    }

    // The job, its run or its step changed: the drawer follows, without opening.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentJobsViewModel.SelectedJob) or nameof(AgentJobsViewModel.SelectedRun) or nameof(AgentJobsViewModel.SelectedStep))
            ShowJobDetails(open: false);
    }

    // The verdict's Show its runs: past a search that hides it, in view and in the drawer.
    private void ShowSubject(AgentJob job)
    {
        if (!JobsGrid.Items.Contains(job))
            JobsFilter.Clear();
        JobsGrid.SelectedItem = job;
        JobsGrid.ScrollIntoView(job);
        ShowJobDetails(open: true);
    }
}
