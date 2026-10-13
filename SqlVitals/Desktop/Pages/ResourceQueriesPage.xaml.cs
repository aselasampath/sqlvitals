using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Resource Queries, bound to <see cref="ResourceQueriesViewModel"/> (#186): the code-behind fills
/// the details drawer with the query selected on any tab, scrolls the one the verdict shows into
/// view, and hands a CPU alert's focus (#145) to the view model.
/// </summary>
public partial class ResourceQueriesPage : Page, IPageContext, IFocusPage, IDetailsSource
{
    // ── The details drawer (#149): the selected query ─────────────────────────
    // A row selected on any tab: its figures, its text and its plan, drawn here or in a window as
    // View Plan always opened it. It stays until another row is selected.
    private const string SelectHint = "Select a query on any tab to see its figures, its text and its plan.";

    private RowDetailsView? _detailsView;

    public DrawerContent Details { get; private set; } = new("Query details", null, SelectHint);

    public event Action<bool>? DetailsChanged;

    internal RowDetailsView? DetailsView => _detailsView;

    private void ShowQuery(object row, bool open)
    {
        if (row is not ResourceIntensiveQuery q)
            return;
        var name = string.IsNullOrWhiteSpace(q.ObjectName) ? "Ad hoc query" : q.ObjectName;
        _detailsView ??= new RowDetailsView();
        _detailsView.Show(RowDetails.Rows(q), "Query text", q.QueryText,
                          [new DrawerPlan("Execution plan", () => ViewModel.PlanOfAsync(q), $"Execution plan · {name}")]);
        Details = new(name, _detailsView, SelectHint);
        DetailsChanged?.Invoke(open);
    }

    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal ResourceQueriesViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public ResourceQueriesPage(ResourceQueriesViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        foreach (var grid in new[] { ReadsGrid, CpuGrid, HighestReadsGrid })
            DrawerParts.FollowSelection(grid, ShowQuery);
        ViewModel.ShowRow += ShowTop;
    }

    // The verdict's Show it: the tab's top row in view, and in the drawer.
    private void ShowTop(ResourceIntensiveQuery top)
    {
        var grid = ViewModel.SelectedTab == (int)ResourceQueriesViewModel.Tab.Cpu ? CpuGrid : ReadsGrid;
        grid.ScrollIntoView(top);
        ShowQuery(top, open: true);
    }

    // ── A CPU alert's evidence (#145): the view model lists the queries run since its window started ──

    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter.Kind == FocusKind.TopCpu)
            ViewModel.FocusOnCpuSince(focus.FromUtc);
    }

    public void ClearFocus() => ViewModel.ClearFocus();

    public string? FocusNote => ViewModel.FocusNote;

    public string? ShowAllText => ViewModel.ShowAllText;
}
