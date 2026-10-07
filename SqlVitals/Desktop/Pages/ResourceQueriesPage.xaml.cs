using System.Text;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

public partial class ResourceQueriesPage : Page, IRefreshable, IPageContext, IFocusPage, IDetailsSource
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
                          [new DrawerPlan("Execution plan", () => PlanOf(q), $"Execution plan · {name}")]);
        Details = new(name, _detailsView, SelectHint);
        DetailsChanged?.Invoke(open);
    }

    // The procedure's plan when the row names one, else the statement's cached plan, as View Plan reads it.
    private async Task<string?> PlanOf(ResourceIntensiveQuery q) =>
        !string.IsNullOrEmpty(q.ObjectName) ? await _repo.GetObjectQueryPlanAsync(q.ObjectName) ?? q.QueryPlan : q.QueryPlan;

    // The verdict's Show it: the tab, its top row selected, in view, and in the drawer.
    private void ShowTop(TabItem tab, DataGrid grid, Func<ResourceIntensiveQuery, long> by)
    {
        Tabs.SelectedItem = tab;
        if (grid.ItemsSource is not IEnumerable<ResourceIntensiveQuery> rows || rows.OrderByDescending(by).FirstOrDefault() is not { } top)
            return;
        grid.SelectedItem = top;
        grid.ScrollIntoView(top);
        ShowQuery(top, open: true);
    }

    // The page template's verdict (#149): the query that uses the most CPU, and the one that reads the most.
    internal ResourceQueriesViewModel ViewModel { get; }

    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;
    private int _topN = 20;

    // A CPU alert's evidence (#145): the By CPU tab, with only the queries last run since the
    // window's start, on the server's clock like the plan cache's times.
    private readonly TimeSpan _serverOffset;
    private bool _focused;
    private DateTime? _cpuSinceUtc;
    private IReadOnlyList<ResourceIntensiveQuery>? _byCpu;

    /// <param name="serverOffset">The server's clock from UTC, to compare the plan cache's last
    /// execution times with an alert's; this PC's when not known.</param>
    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public ResourceQueriesPage(IWaitStatsRepository repo, TimeSpan? serverOffset = null, ISectionFolds? folds = null)
    {
        _repo         = repo;
        _serverOffset = serverOffset ?? TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        InitializeComponent();

        foreach (var grid in new[] { ReadsGrid, CpuGrid, HighestReadsGrid })
            DrawerParts.FollowSelection(grid, ShowQuery);

        ViewModel = new ResourceQueriesViewModel(
            showCpu:   () => ShowTop(TabCpu, CpuGrid, q => q.TotalCPUTime),
            showReads: () => ShowTop(TabReads, ReadsGrid, q => q.TotalLogicalReads));
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    public async Task RefreshAsync()
    {
        IEnumerable<ResourceIntensiveQuery> byReads, byCpu, byHighestReads;
        try
        {
            (byReads, byCpu, byHighestReads) = await _repo.GetResourceIntensiveQueriesAsync(_topN);
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the plan cache's query statistics", ex);
            throw;
        }
        var readsList = byReads.ToList();
        ViewModel.Show(byCpu.ToList(), readsList);

        DataGridRefresh.SetItemsSource(ReadsGrid,        readsList);
        DataGridRefresh.SetItemsSource(HighestReadsGrid, byHighestReads.ToList());
        _byCpu = byCpu.ToList();
        ShowCpu();
    }

    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter.Kind != FocusKind.TopCpu)
            return;
        _focused          = true;
        _cpuSinceUtc      = focus.FromUtc;
        Tabs.SelectedItem = TabCpu;
        ShowCpu();
    }

    public void ClearFocus()
    {
        _cpuSinceUtc = null;
        ShowCpu();
    }

    public string? FocusNote => !_focused || _byCpu is null ? null
        : _cpuSinceUtc is { } since
            ? $"showing the {CpuShown.Count} of the top {_byCpu.Count} queries by CPU last run since {since.ToLocalTime():HH:mm}"
            : "showing the queries that used the most CPU";

    public string? ShowAllText => _cpuSinceUtc is null ? null : _byCpu is null ? "Show all" : $"Show all {_byCpu.Count} queries";

    // The By CPU tab's queries: those last run since the window's start, while it is set.
    private List<ResourceIntensiveQuery> CpuShown =>
        _cpuSinceUtc is { } sinceUtc
            ? (_byCpu ?? []).Where(q => q.LastExecutionTime >= DateTime.SpecifyKind(sinceUtc + _serverOffset, DateTimeKind.Unspecified)).ToList()
            : (_byCpu ?? []).ToList();

    private void ShowCpu()
    {
        if (_byCpu is null)
            return;
        DataGridRefresh.SetItemsSource(CpuGrid, CpuShown);
        MainWindow.ReportContextChanged(this);
    }

    private async void CmbTopN_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cmb || cmb.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Content.ToString(), out int val)) return;

        _topN = val;
        if (!IsLoaded) return;

        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            CompositionRoot.Get<IDialogService>().Warn($"Failed to reload queries:\n{ex.Message}", "Refresh Error");
        }
    }

    private async void ViewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ResourceIntensiveQuery q) return;

        btn.IsEnabled = false;
        btn.Content   = "Loading…";
        try
        {
            string? planXml;
            if (!string.IsNullOrEmpty(q.ObjectName))
            {
                planXml  = await _repo.GetObjectQueryPlanAsync(q.ObjectName);
                planXml ??= q.QueryPlan;
            }
            else
            {
                planXml = q.QueryPlan;
            }

            CompositionRoot.Get<IDialogService>().ShowQueryPlan(planXml, q.QueryText);
        }
        catch (Exception ex)
        {
            CompositionRoot.Get<IDialogService>().Warn($"Failed to retrieve execution plan:\n{ex.Message}", "Plan Error");
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content   = "View Plan";
        }
    }

    private void CopyReads_Click(object sender, RoutedEventArgs e)        => CopyGrid(ReadsGrid);
    private void CopyCpu_Click(object sender, RoutedEventArgs e)          => CopyGrid(CpuGrid);
    private void CopyHighestReads_Click(object sender, RoutedEventArgs e) => CopyGrid(HighestReadsGrid);

    // Copies what the grid shows: the filtered rows, in the current sort order.
    private static void CopyGrid(DataGrid grid)
    {
        if (grid.ItemsSource == null) return;

        try
        {
            var sb = new StringBuilder();

            foreach (var col in grid.Columns)
                sb.Append(col.Header + "\t");
            sb.AppendLine();

            foreach (var item in grid.Items)
            {
                var props = item.GetType().GetProperties();
                foreach (var col in grid.Columns)
                {
                    if (col is DataGridTextColumn textCol &&
                        textCol.Binding is System.Windows.Data.Binding binding)
                    {
                        var prop = props.FirstOrDefault(p => p.Name == binding.Path.Path);
                        var val  = prop?.GetValue(item)?.ToString() ?? "";
                        sb.Append(val.Replace("\t", " ").Replace("\n", " ") + "\t");
                    }
                    else
                    {
                        sb.Append("\t");
                    }
                }
                sb.AppendLine();
            }

            ClipboardHelper.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            CompositionRoot.Get<IDialogService>().Warn($"Failed to copy data:\n{ex.Message}", "Copy Failed");
        }
    }
}
