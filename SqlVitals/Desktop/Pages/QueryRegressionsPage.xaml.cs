using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Query Regressions, bound to <see cref="QueryRegressionsViewModel"/> (#32, #187): the code-behind
/// fills the details drawer with the query selected and its plans, turns a click on a query_id
/// into the view model's Open query, and scrolls the query the verdict shows into view.
/// </summary>
public partial class QueryRegressionsPage : Page, IPageContext, IDetailsSource
{
    // ── The details drawer (#149): the selected query ─────────────────────────
    // Its figures before and now, its text, and its plans now and before, drawn here or in a window
    // as the Now and Before buttons open them.
    private const string SelectHint = "Select a query to see its figures before and now, its text and its plans.";

    private RowDetailsView? _detailsView;

    public DrawerContent Details { get; private set; } = new("Query details", null, SelectHint);

    public event Action<bool>? DetailsChanged;

    internal RowDetailsView? DetailsView => _detailsView;

    private void ShowRegression(object row, bool open)
    {
        if (row is not QueryRegression r)
            return;
        var plans = ViewModel.PlansOf(r)
            .Select(p => new DrawerPlan(p.Heading, () => ViewModel.PlanOfAsync(r, p.Baseline), p.WindowTitle))
            .ToList();
        _detailsView ??= new RowDetailsView();
        _detailsView.Show(RowDetails.Rows(r), "Query text", r.QueryText, plans);
        Details = new($"Query {r.QueryKey}", _detailsView, SelectHint);
        DetailsChanged?.Invoke(open);
    }

    internal QueryRegressionsViewModel ViewModel { get; }

    // A recent window against a baseline (#140), as last checked; both boxes stay on the page.
    // Query Store reads the connection's database, the monitoring history the whole server.
    public PageKind Kind => PageKind.History;
    public bool UsesDatabase => ViewModel.FromQueryStore;
    public string? HistoryWindow => ViewModel.HistoryWindow;
    public string? CompareWith => ViewModel.CompareWith;
    public DateTime? DataTo => ViewModel.DataTo;

    /// <param name="folds">Where the verdict's and the criteria's folds are remembered; none in some tests.</param>
    public QueryRegressionsPage(QueryRegressionsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(RegressionsGrid, ShowRegression);
        ViewModel.ShowRow += ShowWorst;

        // A query_id opens Query Store on that query (#145). The grid handles the mouse itself, so
        // the link needs the clicks it has handled too.
        RegressionsGrid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(RegressionsGrid_MouseLeftButtonUp), handledEventsToo: true);
    }

    // The verdict's Show its plans: the worst query, past a search that hides it, in view and in the drawer.
    private void ShowWorst(QueryRegression worst)
    {
        if (!RegressionsGrid.Items.Contains(worst))
            RegressionsFilter.Clear();
        RegressionsGrid.SelectedItem = worst;
        RegressionsGrid.ScrollIntoView(worst);
        ShowRegression(worst, open: true);
    }

    private void RegressionsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBlock { DataContext: QueryRegression row } text)
            return;
        DependencyObject? cell = text;
        while (cell is not null and not DataGridCell)
            cell = VisualTreeHelper.GetParent(cell);
        if ((cell as DataGridCell)?.Column == QueryColumn)
            ViewModel.OpenQueryCommand.Execute(row);
    }
}
