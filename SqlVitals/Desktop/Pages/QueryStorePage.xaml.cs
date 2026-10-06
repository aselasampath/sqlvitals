using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Query Store for the connection's database (#111): its settings and checks, and the metrics of
/// SSMS's Query Store reports on five tabs. Opening the page reads the settings and the tab on
/// screen; another tab is read the first time it's opened, and again after Refresh or a new period.
/// </summary>
public partial class QueryStorePage : Page, IRefreshable, IPageContext, IFocusPage, IDetailsSource
{
    // ── The details drawer (#147): the selected query ─────────────────────────
    // A row selected in any tab: its figures there, its text and its plans in the period. It stays
    // until another row is selected, so a reload or another tab doesn't lose it.
    private const string SelectQueryHint = "Select a query in any tab to see its text, its figures and its plans.";

    private QueryDetailsView? _queryView;

    public DrawerContent Details { get; private set; } = new("Query details", null, SelectQueryHint);

    public event Action<bool>? DetailsChanged;

    /// <summary>The drawer's latest read of a query's plans, for tests.</summary>
    internal Task DetailsLoaded { get; private set; } = Task.CompletedTask;

    private void ShowQuery(object row, bool open)
    {
        if (QueryStoreDetails.QueryOf(row) is not { } query)
            return;
        _queryView ??= new QueryDetailsView(
            async queryId => (await _repo.GetQueryStoreQueryDetailsAsync(queryId, _period)).Summaries,
            (button, planId, queryId, text) => ShowPlanAsync(this, _repo, button, planId, queryId, text),
            OpenQueryDetails);
        DetailsLoaded = _queryView.ShowAsync(row, _period.Label);
        Details = new($"Query {query.QueryId}", _queryView, SelectQueryHint);
        DetailsChanged?.Invoke(open);
    }

    // Each tab's grid: a row selected shows it; a click on one also opens the drawer.
    private void FollowSelection(DataGrid grid)
    {
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is { } row)
                ShowQuery(row, open: false);
        };
        grid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(grid, source) is DataGridRow { Item: { } row })
                ShowQuery(row, open: true);
        }), handledEventsToo: true);
    }

    // ── One query, from a link that carries it (#145) ─────────────────────────
    // Top queries shows only it, and its details open in their own window once the settings are read.
    private long? _query;
    private bool  _queryOpened;
    private int?  _queryRowsShown;

    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter is not { Kind: FocusKind.Query, Id: { } id })
            return;
        _query            = id;
        TxtQueryId.Text   = id.ToString(CultureInfo.InvariantCulture);
        Tabs.SelectedItem = TabTop;
    }

    public void ClearFocus()
    {
        _query          = null;
        _queryRowsShown = null;
        _tabs[TabTop].IsCurrent = false;
        if (Tabs.SelectedItem == TabTop)
            _ = LoadTabAsync(TabTop);
        MainWindow.ReportContextChanged(this);
    }

    public string? FocusNote => _query is not { } id || _queryRowsShown is not { } shown ? null
        : shown == 0 ? $"query {id} isn't among the top queries of {_period.Label.ToLowerInvariant()}; its details are in their own window"
        : $"showing query {id} only; its details are in their own window";

    public string? ShowAllText => _query is null ? null : "Show all queries";

    // A period of the connection's database's Query Store (#140); the period box stays on the page.
    public PageKind Kind => PageKind.History;
    public bool UsesDatabase => true;
    public string? HistoryWindow => _period.Label;
    public DateTime? DataTo { get; private set; }

    // The period's end as of this read, for the top bar: now, for a period up to now.
    private void NoteDataTo()
    {
        var nowUtc = DateTime.UtcNow;
        var toUtc  = _period.Resolve(nowUtc).ToUtc;
        DataTo = (toUtc < nowUtc ? toUtc : nowUtc).ToLocalTime();
        MainWindow.ReportContextChanged(this);
    }

    private const int VariationTop   = 50;
    private const int WaitQueriesTop = 25;

    // Same colours as the trend pages' lines.
    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#26E7A6"), SKColor.Parse("#FF6B6B"), SKColor.Parse("#7C3AED"), SKColor.Parse("#FFA62B"),
        SKColor.Parse("#00D9FF"), SKColor.Parse("#FF4560"), SKColor.Parse("#FFC107"), SKColor.Parse("#9B59B6"),
        SKColor.Parse("#1ABC9C"), SKColor.Parse("#3498DB"),
    ];

    /// <summary>Whether a tab has been read for what's on screen now, and which read is the latest.</summary>
    private sealed class TabState
    {
        public bool IsCurrent;
        public int  Version;
    }

    /// <summary>An Over Time chart the user can turn on or off.</summary>
    public sealed class ChartMetricItem(QueryStoreMetricInfo info, bool isChecked)
    {
        public QueryStoreMetricInfo Info { get; } = info;
        public string Name => Info.Name;
        public bool IsChecked { get; set; } = isChecked;
    }

    /// <summary>One bar of the Waits chart.</summary>
    public sealed record WaitBar(QueryStoreWaitCategory Category, double Fraction)
    {
        public GridLength FillWidth => new(Math.Max(Fraction, 0.001), GridUnitType.Star);
        public GridLength RestWidth => new(Math.Max(1 - Fraction, 0.001), GridUnitType.Star);
        public string ValueText => string.Format(CultureInfo.CurrentCulture, "{0:N0} ms · {1:N1} %", Category.WaitMs, Category.PctOfAll);
        public string Explanation => Category.Explanation;
    }

    private readonly IWaitStatsRepository      _repo;
    private readonly ConnectionSettingsService _settings;
    private readonly Dictionary<TabItem, TabState> _tabs;

    private QueryStoreStatus? _status;
    private QueryStorePeriod  _period = QueryStorePeriod.Default;

    // The last Over Time read, so turning a chart on or off needs no new read.
    private IReadOnlyList<QueryStoreSlot> _slots = [];
    private TimeSpan       _slotLength;
    private DateTimeOffset _slotOrigin;
    private QueryStorePeriod? _slotsPeriod;

    private int  _waitQueriesVersion;
    private bool _ready;

    // ── The page template's verdict (#146) ────────────────────────────────────
    // The queries that got slower in the period against the 7 days before, as Query Regressions
    // finds them with its saved threshold, read beside the tab on screen.
    internal QueryStoreViewModel ViewModel { get; }
    private int _verdictVersion;

    /// <summary>The verdict's read, for tests; done once it has been shown.</summary>
    internal Task VerdictLoaded { get; private set; } = Task.CompletedTask;

    private void LoadVerdict() => VerdictLoaded = LoadVerdictAsync();

    private async Task LoadVerdictAsync()
    {
        if (_status is not { } status)
            return;
        var version    = ++_verdictVersion;
        var period     = _period;
        var criteria   = _settings.Load().RegressionCriteria;
        var comparison = $"{period.Label} compared with the 7 days before";

        // Query Store off, or a custom period: nothing to compare (the verdict says which).
        if (!status.IsAvailable || period.IsCustom)
        {
            ViewModel.Show(status, null, comparison, criteria);
            return;
        }

        try
        {
            var stats = await _repo.GetQueryStoreRegressionStatsAsync(new RegressionWindows(period.Length, RegressionBaselineKind.PreviousWeek));
            var found = stats.IsAvailable ? await Task.Run(() => QueryRegressionDetector.Detect(stats.Stats, criteria)) : null;
            if (version == _verdictVersion)
                ViewModel.Show(status, found, comparison, criteria,
                               stats.IsAvailable ? null : $"Query Store's runtime statistics for {stats.DatabaseName} can't be read: it is {stats.State.Replace('_', ' ').ToLowerInvariant()}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("QueryStore: the regression check for the verdict failed", ex);
            if (version == _verdictVersion)
                ViewModel.ShowError("the regression check", ex);
        }
    }

    public QueryStorePage(IWaitStatsRepository repo, ConnectionSettingsService settings)
    {
        _repo     = repo;
        _settings = settings;
        InitializeComponent();

        _tabs = new[] { TabTop, TabOverTime, TabVariation, TabWaits, TabForced }.ToDictionary(t => t, _ => new TabState());
        foreach (var grid in new[] { TopGrid, VariationGrid, WaitQueriesGrid, ForcedGrid, HintsGrid })
            FollowSelection(grid);

        foreach (var p in QueryStorePeriod.Presets)
            CmbPeriod.Items.Add(new ComboBoxItem { Content = p.Label, Tag = p });
        CmbPeriod.Items.Add(new ComboBoxItem { Content = "Custom…" });
        CmbPeriod.SelectedIndex = QueryStorePeriod.Presets.ToList().IndexOf(QueryStorePeriod.Default);

        foreach (var s in Enum.GetValues<QueryStoreStatistic>())
            CmbStatistic.Items.Add(new ComboBoxItem { Content = s == QueryStoreStatistic.Average ? "Average" : QueryStoreMetrics.Label(s), Tag = s });
        CmbStatistic.SelectedIndex = 0;

        foreach (var n in new[] { 20, 50, 100 })
            CmbTop.Items.Add(new ComboBoxItem { Content = $"Top {n}", Tag = n });
        CmbTop.SelectedIndex = 0;

        foreach (var m in QueryStoreMetrics.VariationMetrics)
            CmbVariationMetric.Items.Add(new ComboBoxItem { Content = QueryStoreMetrics.Info(m).Name, Tag = m });
        CmbVariationMetric.SelectedIndex = 0;

        FillMetricChoices(sql2017Metrics: false);

        ViewModel = new QueryStoreViewModel(openQuery: OpenQueryDetails, openRegressions: OpenRegressions);
        ViewModel.Attach(Shell);
        Shell.Folds = settings;

        _ready = true;
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>Reads the settings again, and the tab on screen; the others are read when next opened.</summary>
    public async Task RefreshAsync()
    {
        NoteDataTo();
        QueryStoreStatus status;
        try
        {
            status = await _repo.GetQueryStoreStatusAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("Query Store's settings", ex);
            throw;
        }
        _status = status;
        ShowStatus(status);
        LoadVerdict();

        // Opened on one query: its details, once (#145).
        if (_query is { } query && !_queryOpened)
        {
            _queryOpened = true;
            OpenQueryDetails(query);
        }

        foreach (var state in _tabs.Values)
            state.IsCurrent = false;
        await LoadSelectedTabAsync();
    }

    private Task LoadSelectedTabAsync() =>
        Tabs.SelectedItem is TabItem tab ? LoadTabAsync(tab) : Task.CompletedTask;

    // Reads one tab. A read that finishes after a newer one started is dropped; an error shows in
    // the tab's status line, so the rest of the page and the other tabs carry on.
    private async Task LoadTabAsync(TabItem tab)
    {
        if (_status is not { } status)
            return;

        var state   = _tabs[tab];
        var version = ++state.Version;
        state.IsCurrent = true;
        var statusLine = StatusLine(tab);

        if (!status.IsAvailable)
        {
            NoticeFor(tab).Show(status.UnavailableReason!, status.EnableScript);
            statusLine.Text = string.Empty;
            ClearTab(tab);
            return;
        }

        statusLine.Text = "Loading…";
        try
        {
            var show = await ReadTabAsync(tab, status);
            if (version == state.Version)
                show();
        }
        catch (Exception ex)
        {
            AppLog.Error($"QueryStore: reading the {tab.Header} tab failed", ex);
            if (version == state.Version)
            {
                statusLine.Text  = MainWindow.FormatErrorStatus(ex);
                state.IsCurrent = false;   // read again when next opened
            }
        }
    }

    // Reads a tab's data and returns what puts it on screen.
    private Task<Action> ReadTabAsync(TabItem tab, QueryStoreStatus status) =>
        tab == TabTop       ? ReadTopQueriesAsync()
      : tab == TabOverTime  ? ReadOverTimeAsync(status)
      : tab == TabVariation ? ReadVariationAsync()
      : tab == TabWaits     ? ReadWaitsAsync(status)
      :                       ReadForcedPlansAsync();

    private TextBlock StatusLine(TabItem tab) =>
        tab == TabTop ? TxtTopStatus : tab == TabOverTime ? TxtOverTimeStatus : tab == TabVariation ? TxtVariationStatus
      : tab == TabWaits ? TxtWaitsStatus : TxtForcedStatus;

    private Controls.ScriptNotice NoticeFor(TabItem tab) =>
        tab == TabTop ? TopNotice : tab == TabOverTime ? OverTimeNotice : tab == TabVariation ? VariationNotice
      : tab == TabWaits ? WaitsNotice : ForcedNotice;

    private void ClearTab(TabItem tab)
    {
        if (tab == TabTop)            TopGrid.ItemsSource = null;
        else if (tab == TabOverTime)  ChartsPanel.Children.Clear();
        else if (tab == TabVariation) VariationGrid.ItemsSource = null;
        else if (tab == TabWaits)     { WaitCategoriesList.ItemsSource = null; WaitQueriesGrid.ItemsSource = null; }
        else                          { ForcedGrid.ItemsSource = null; HintsGrid.Visibility = TxtHintsHeader.Visibility = Visibility.Collapsed; HintsNotice.Hide(); }
    }

    // Rows, or the reason there are none (a view or column this version lacks).
    private static void ShowRows<T>(DataGrid grid, Controls.ScriptNotice notice, QueryStoreRows<T> rows)
    {
        if (rows.Unavailable is { } why)
            notice.Show(why);
        else
            notice.Hide();
        DataGridRefresh.SetItemsSource(grid, rows.Rows);
    }

    private string Counted(int count, string one, string many) =>
        $"{count:N0} {(count == 1 ? one : many)} · {_period.Label}";

    // ── Settings and health ───────────────────────────────────────────────────

    private void ShowStatus(QueryStoreStatus status)
    {
        Shell.Title = status.DatabaseName.Length == 0 ? "Query Store" : $"Query Store — {status.DatabaseName}";
        FillMetricChoices(status.Features.Sql2017Metrics);

        HealthPanel.Children.Clear();
        var h = status.Health;
        if (h is null)
        {
            AddCard("Query Store", "Not supported", "#94A3B8", "Query Store came with SQL Server 2016.");
            ChecksList.ItemsSource = null;
            HealthSection.FoldedSummary = "Not supported: Query Store came with SQL Server 2016";
            TxtDataFrom.Text = string.Empty;
            return;
        }

        var checks = QueryStoreChecks.Run(h);
        var worst  = QueryStoreChecks.Worst(checks);

        AddCard("State", h.ActualState, h.IsEnabled ? (h.ActualState == "READ_WRITE" ? "#22C55E" : "#FBBF24") : "#EF4444",
                h.ReadOnlyReason is { } why ? $"Set to {h.DesiredState}; read-only because {why}." : $"Set to {h.DesiredState}.");
        AddCard("Storage used", $"{h.CurrentStorageSizeMB:N0} MB", h.StorageUsedPct > 90 ? "#EF4444" : h.StorageUsedPct > 75 ? "#FBBF24" : "#22C55E",
                $"Of MAX_STORAGE_SIZE_MB = {h.MaxStorageSizeMB:N0}.");
        AddCard("Storage %", $"{h.StorageUsedPct:N1} %", h.StorageUsedPct > 90 ? "#EF4444" : "#7C3AED",
                "Size-based cleanup (when AUTO) starts at 90 %; at 100 % Query Store turns read-only.");
        AddCard("Queries", $"{h.TotalQueries:N0}", "#7C3AED", "Queries Query Store holds.");
        AddCard("Plans", $"{h.TotalPlans:N0}", "#7C3AED", $"Plans Query Store holds; at most {h.MaxPlansPerQuery:N0} per query.");
        AddCard("Forced plans", $"{h.ForcedPlans:N0}", h.ForcedPlans > 0 ? "#FBBF24" : "#94A3B8",
                "Plans forced by hand or by automatic plan correction. Click to open the Forced Plans tab.",
                () => Tabs.SelectedItem = TabForced);
        AddCard("Capture mode", h.CaptureMode, h.CaptureMode == "ALL" ? "#60A5FA" : "#94A3B8",
                "Which queries Query Store records: ALL, AUTO (skips trivial ad hoc queries), CUSTOM or NONE.");
        AddCard("Size cleanup", h.SizeCleanupMode, h.SizeCleanupMode == "OFF" ? "#FBBF24" : "#94A3B8",
                "AUTO removes the oldest, cheapest queries as Query Store nears its maximum size.");
        AddCard("Stale after", $"{h.StaleQueryThresholdDays:N0} days", "#94A3B8", "How long Query Store keeps a query that hasn't run (STALE_QUERY_THRESHOLD_DAYS).");
        AddCard("Interval", $"{h.IntervalLengthMin:N0} min", "#94A3B8", "How long each runtime statistics interval is (INTERVAL_LENGTH_MINUTES).");
        if (h.WaitStatsCaptureMode is { } waits)
            AddCard("Wait stats capture", waits, waits == "OFF" ? "#60A5FA" : "#94A3B8", "Whether Query Store records waits per query (SQL Server 2017+).");
        AddCard("Status", worst switch { QueryStoreCheckLevel.Warning => "Warning", QueryStoreCheckLevel.Info => "Info", _ => "OK" },
                worst switch { QueryStoreCheckLevel.Warning => "#FBBF24", QueryStoreCheckLevel.Info => "#60A5FA", _ => "#22C55E" },
                "The worst of the settings checks listed below the cards.");

        ChecksList.ItemsSource = checks;

        // Folded, the section says it in one line (#146): "READ_WRITE · 41 % of storage · 2 warnings".
        var warnings = checks.Count(c => c.Level == QueryStoreCheckLevel.Warning);
        HealthSection.FoldedSummary = string.Format(CultureInfo.CurrentCulture, "{0} · {1:N1} % of storage · {2}",
            h.ActualState, h.StorageUsedPct,
            warnings switch { 0 => "settings OK", 1 => "1 settings warning", var n => $"{n} settings warnings" });
        ShowDataFrom();
    }

    // "Query Store has data from 2 Sep 14:00", and whether the period reaches further back.
    private void ShowDataFrom()
    {
        if (_status?.Health is not { } h)
            return;
        if (h.DataFromUtc is not { } from)
        {
            TxtDataFrom.Text = "Query Store holds no runtime statistics yet.";
            return;
        }

        var text = $"Query Store has data from {from.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture)} " +
                   $"(it keeps queries for {h.StaleQueryThresholdDays:N0} days).";
        if (_period.Resolve(DateTime.UtcNow).FromUtc < from)
            text += " The period starts earlier, so it shows only what exists.";
        TxtDataFrom.Text = text;
    }

    private void AddCard(string label, string value, string hexColor, string tooltip, Action? onClick = null)
    {
        var card = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 8, 8),
            Width  = 138,
            Padding = new Thickness(12, 8, 12, 8),
        };
        var tip = new StackPanel { MaxWidth = 340 };
        tip.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        tip.Children.Add(new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap });
        card.ToolTip = new ToolTip { Content = tip };

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = value, Style = (Style)Application.Current.FindResource("KpiValue"), FontSize = 16,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = sp;

        if (onClick is not null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => onClick();
        }
        HealthPanel.Children.Add(card);
    }

    private void CopyFix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryStoreCheck { Fix: { } fix } })
            ClipboardHelper.SetText(fix);
    }

    private void BtnRegressions_Click(object sender, RoutedEventArgs e) => OpenRegressions();

    private async void OpenRegressions()
    {
        if (Window.GetWindow(this) is MainWindow main)
            await main.OpenPageAsync("QueryRegressions");
    }

    // ── Period ────────────────────────────────────────────────────────────────

    private async void CmbPeriod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || CmbPeriod.SelectedItem is not ComboBoxItem item)
            return;

        if (item.Tag is QueryStorePeriod preset)
        {
            CustomPanel.Visibility    = Visibility.Collapsed;
            TxtPeriodError.Visibility = Visibility.Collapsed;
            if (preset != _period)
                await SetPeriodAsync(preset);
            return;
        }

        // Custom: start from the period on screen.
        var (fromUtc, toUtc) = _period.Resolve(DateTime.UtcNow);
        FromDate.SelectedDate = fromUtc.ToLocalTime().Date;
        FromTime.Text         = fromUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        ToDate.SelectedDate   = toUtc.ToLocalTime().Date;
        ToTime.Text           = toUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        CustomPanel.Visibility = Visibility.Visible;
        FromTime.Focus();
    }

    private async void BtnApplyCustom_Click(object sender, RoutedEventArgs e) => await ApplyCustomAsync();

    private async void CustomTime_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await ApplyCustomAsync();
    }

    private async Task ApplyCustomAsync()
    {
        if (!QueryStorePeriod.TryParseCustom(FromDate.SelectedDate, FromTime.Text, ToDate.SelectedDate, ToTime.Text,
                                             DateTime.UtcNow, out var period, out var error))
        {
            TxtPeriodError.Text       = error;
            TxtPeriodError.Visibility = Visibility.Visible;
            return;
        }
        TxtPeriodError.Visibility = Visibility.Collapsed;
        await SetPeriodAsync(period!);
    }

    // Every tab is read again for the new period: the one on screen now, the others when opened.
    private async Task SetPeriodAsync(QueryStorePeriod period)
    {
        _period = period;
        NoteDataTo();
        ShowDataFrom();
        LoadVerdict();
        foreach (var state in _tabs.Values)
            state.IsCurrent = false;
        await LoadSelectedTabAsync();
    }

    // Shows a custom period in the picker, as when a point on Over Time is clicked.
    private void ShowCustomPeriod(QueryStorePeriod period)
    {
        _ready = false;
        CmbPeriod.SelectedIndex = CmbPeriod.Items.Count - 1;
        _ready = true;
        FromDate.SelectedDate  = period.FromUtc!.Value.ToLocalTime().Date;
        FromTime.Text          = period.FromUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        ToDate.SelectedDate    = period.ToUtc!.Value.ToLocalTime().Date;
        ToTime.Text            = period.ToUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        CustomPanel.Visibility = Visibility.Visible;
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection changes inside a tab (a combo, a list, a grid) bubble up here too.
        if (!ReferenceEquals(e.OriginalSource, Tabs) || !_ready || Tabs.SelectedItem is not TabItem tab)
            return;
        if (!_tabs[tab].IsCurrent)
            await LoadTabAsync(tab);
    }

    // ── Top Queries ───────────────────────────────────────────────────────────

    // Log used and TempDB used only where the server records them.
    private void FillMetricChoices(bool sql2017Metrics)
    {
        var available = QueryStoreMetrics.Available(sql2017Metrics);
        if (CmbRank.Items.Count == available.Count)
            return;

        var wasReady = _ready;
        _ready = false;
        var selected = (CmbRank.SelectedItem as ComboBoxItem)?.Tag as QueryStoreMetric? ?? QueryStoreMetric.Cpu;
        CmbRank.Items.Clear();
        foreach (var m in available)
            CmbRank.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Metric });
        CmbRank.SelectedIndex = Math.Max(0, available.ToList().FindIndex(m => m.Metric == selected));

        var saved = _settings.Load().QueryStoreChartMetrics;
        ChartMetricsList.ItemsSource = available.Select(m => new ChartMetricItem(m, saved.Contains(m.Metric))).ToList();
        _ready = wasReady;
    }

    private QueryStoreMetric SelectedRank => (QueryStoreMetric)((ComboBoxItem)CmbRank.SelectedItem).Tag;
    private QueryStoreStatistic SelectedStatistic => (QueryStoreStatistic)((ComboBoxItem)CmbStatistic.SelectedItem).Tag;
    private int SelectedTop => (int)((ComboBoxItem)CmbTop.SelectedItem).Tag;

    private async void TopChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbRank.SelectedItem is null || CmbStatistic.SelectedItem is null)
            return;
        // Executions are a count: there is no average or std dev of it per execution.
        CmbStatistic.IsEnabled = SelectedRank != QueryStoreMetric.Executions;
        if (_ready && IsLoaded)
            await LoadTabAsync(TabTop);
    }

    private async Task<Action> ReadTopQueriesAsync()
    {
        var (metric, statistic, top) = (SelectedRank, SelectedStatistic, SelectedTop);
        var rows = await _repo.GetQueryStoreTopQueriesAsync(_period, metric, statistic, top);
        return () =>
        {
            var info = QueryStoreMetrics.Info(metric);
            TxtMetricHeader.Text = QueryStoreMetrics.ColumnHeader(metric, statistic);
            ColMetric.Binding    = new Binding(nameof(QueryStoreQueryRow.MetricValue)) { StringFormat = info.Format };
            // Ranked by total CPU, the ranked column already is the total CPU column.
            ColTotalCpu.Visibility = metric == QueryStoreMetric.Cpu && statistic == QueryStoreStatistic.Total ? Visibility.Collapsed : Visibility.Visible;
            if (_query is { } query)
            {
                rows            = rows with { Rows = rows.Rows.Where(r => r.QueryId == query).ToList() };
                _queryRowsShown = rows.Rows.Count;
                MainWindow.ReportContextChanged(this);
            }
            ShowRows(TopGrid, TopNotice, rows);
            TxtTopStatus.Text = rows.Unavailable is null ? Counted(rows.Rows.Count, "query", "queries") : string.Empty;
        };
    }

    // ── Over Time ─────────────────────────────────────────────────────────────

    private async Task<Action> ReadOverTimeAsync(QueryStoreStatus status)
    {
        var period = _period;
        var slot   = QueryStoreTimeSlots.SlotLength(period.Length, status.Health!.IntervalLengthMin);
        var origin = QueryStoreTimeSlots.Origin(slot, DateTimeOffset.Now.Offset);
        var rows   = await _repo.GetQueryStoreOverTimeAsync(period, slot, origin);
        return () =>
        {
            _slots = rows.Rows;
            (_slotLength, _slotOrigin, _slotsPeriod) = (slot, origin, period);
            if (rows.Unavailable is { } why)
                OverTimeNotice.Show(why);
            else
                OverTimeNotice.Hide();
            TxtOverTimeStatus.Text = $"The database's totals, each point {DescribeSlot(slot)} · {period.Label}. Click a point for its top queries.";
            DrawCharts();
        };
    }

    private static string DescribeSlot(TimeSpan slot) =>
        slot >= TimeSpan.FromDays(1) ? "a day" : slot >= TimeSpan.FromHours(1) ? (slot == TimeSpan.FromHours(1) ? "an hour" : $"{slot.TotalHours:0} hours")
        : $"{slot.TotalMinutes:0} minutes";

    // Also raised as the check boxes are first drawn, so it saves only a real change.
    private void ChartMetric_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || ChartMetricsList.ItemsSource is not IEnumerable<ChartMetricItem> items)
            return;

        var chosen = items.Where(i => i.IsChecked).Select(i => i.Info.Metric).ToList();
        try
        {
            // Metrics this server doesn't offer (log and TempDB before 2017) stay saved for the next one.
            var offered = items.Select(i => i.Info.Metric).ToHashSet();
            var saved   = _settings.Load().QueryStoreChartMetrics;
            if (chosen.Count > 0 && !chosen.ToHashSet().SetEquals(saved.Where(offered.Contains)))
                _settings.SetQueryStoreChartMetrics(chosen.Concat(saved.Where(m => !offered.Contains(m))).ToList());
        }
        catch (Exception ex)
        {
            // The charts still change; the choice just won't be remembered.
            AppLog.Error("QueryStore: saving the chart choice failed", ex);
        }
        DrawCharts();
    }

    // One chart per chosen metric, stacked, all on the same time axis. A slot where Query Store
    // recorded nothing is a gap, not a zero: it may not have been recording.
    private void DrawCharts()
    {
        ChartsPanel.Children.Clear();
        if (_slotsPeriod is null || ChartMetricsList.ItemsSource is not IEnumerable<ChartMetricItem> items)
            return;

        if (_slots.Count == 0)
        {
            ChartsPanel.Children.Add(new TextBlock
            {
                Text = $"Query Store recorded nothing in this period ({_slotsPeriod.Label.ToLower(CultureInfo.CurrentCulture)}).",
                Foreground = (Brush)FindResource("TextMuted"), FontSize = 14, Margin = new Thickness(0, 30, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        var (fromUtc, toUtc) = _slotsPeriod.Resolve(DateTime.UtcNow);
        var bySlot = _slots.ToDictionary(s => s.StartUtc);
        var starts = QueryStoreTimeSlots.SlotsBetween(_slotOrigin, _slotLength, fromUtc, toUtc)
            .Select(i => QueryStoreTimeSlots.SlotStartUtc(_slotOrigin, _slotLength, i))
            .Union(bySlot.Keys)
            .OrderBy(t => t)
            .ToList();
        var axisFormat = ConnectionHistory.AxisFormat(toUtc - fromUtc);
        var min = starts[0].ToLocalTime().Ticks;
        var max = (starts[^1] + _slotLength).ToLocalTime().Ticks;

        var index = 0;
        foreach (var item in items.Where(i => i.IsChecked))
        {
            var color  = Palette[index++ % Palette.Length];
            var metric = item.Info.Metric;
            var values = starts.Select(t => new DateTimePoint(t.ToLocalTime(),
                bySlot.TryGetValue(t, out var s) ? s.Totals.Total(metric) : null)).ToList();

            var chart = new LiveChartsCore.SkiaSharpView.WPF.CartesianChart
            {
                Height          = 170,
                Margin          = new Thickness(0, 0, 0, 6),
                Background      = Brushes.Transparent,
                TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Top,
                LegendPosition  = LiveChartsCore.Measure.LegendPosition.Hidden,
                AnimationsSpeed = TimeSpan.Zero,
                Cursor          = Cursors.Hand,
                Series =
                [
                    new LineSeries<DateTimePoint>
                    {
                        Values         = values,
                        Name           = metric == QueryStoreMetric.Executions ? item.Info.Name : $"Total {item.Info.NameWithUnit}",
                        Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.8f },
                        Fill           = new SolidColorPaint(color.WithAlpha(40)),
                        GeometrySize   = 4,
                        GeometryFill   = new SolidColorPaint(color),
                        GeometryStroke = null,
                        LineSmoothness = 0.3,
                    },
                ],
                XAxes =
                [
                    new DateTimeAxis(_slotLength, dt => dt.ToString(axisFormat, CultureInfo.CurrentCulture))
                    {
                        LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                        SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                        TextSize        = 11,
                        MinLimit        = min,
                        MaxLimit        = max,
                    },
                ],
                YAxes =
                [
                    new Axis
                    {
                        Name            = metric == QueryStoreMetric.Executions ? item.Info.Name : $"Total {item.Info.NameWithUnit}",
                        NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                        NameTextSize    = 11,
                        LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                        SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                        TextSize        = 11,
                        MinLimit        = 0,
                    },
                ],
            };
            chart.DataPointerDown += Chart_DataPointerDown;
            ChartsPanel.Children.Add(chart);
        }
    }

    // A point opens Top Queries for its time slot.
    private async void Chart_DataPointerDown(IChartView chart, IEnumerable<ChartPoint>? points)
    {
        if (points?.FirstOrDefault()?.Context.DataSource is not DateTimePoint { Value: not null } point)
            return;

        var fromUtc = DateTime.SpecifyKind(point.DateTime, DateTimeKind.Local).ToUniversalTime();
        var slot    = QueryStorePeriod.Custom(fromUtc, fromUtc + _slotLength);
        ShowCustomPeriod(slot);
        _period = slot;
        ShowDataFrom();
        LoadVerdict();
        foreach (var state in _tabs.Values)
            state.IsCurrent = false;

        if (!ReferenceEquals(Tabs.SelectedItem, TabTop))
            Tabs.SelectedItem = TabTop;   // loads it
        else
            await LoadTabAsync(TabTop);
    }

    // ── High Variation ────────────────────────────────────────────────────────

    private QueryStoreMetric SelectedVariationMetric => (QueryStoreMetric)((ComboBoxItem)CmbVariationMetric.SelectedItem).Tag;

    private async void CmbVariationMetric_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && IsLoaded)
            await LoadTabAsync(TabVariation);
    }

    private async Task<Action> ReadVariationAsync()
    {
        var metric = SelectedVariationMetric;
        var rows = await _repo.GetQueryStoreHighVariationAsync(_period, metric, QueryStoreMetrics.MinVariationExecutions, VariationTop);
        return () =>
        {
            var info = QueryStoreMetrics.Info(metric);
            var unit = info.Unit.Length == 0 ? string.Empty : $" {info.Unit}";
            foreach (var (column, name, path) in new[]
                     {
                         (ColVarAvg, "Avg", nameof(QueryStoreVariationRow.Avg)),
                         (ColVarMin, "Min", nameof(QueryStoreVariationRow.Min)),
                         (ColVarMax, "Max", nameof(QueryStoreVariationRow.Max)),
                     })
            {
                column.Header  = name + unit;
                column.Binding = new Binding(path) { StringFormat = info.Format };
            }
            ((TextBlock)ColVarStdDev.Header).Text = "Std dev" + unit;
            ColVarStdDev.Binding = new Binding(nameof(QueryStoreVariationRow.StdDev)) { StringFormat = info.Format };

            ShowRows(VariationGrid, VariationNotice, rows);
            TxtVariationStatus.Text = rows.Unavailable is null
                ? Counted(rows.Rows.Count, "query", "queries") + $" · at least {QueryStoreMetrics.MinVariationExecutions} executions each"
                : string.Empty;
        };
    }

    // ── Waits ─────────────────────────────────────────────────────────────────

    private async Task<Action> ReadWaitsAsync(QueryStoreStatus status)
    {
        if (!status.Features.WaitStats)
            return () =>
            {
                WaitsNotice.Show("This version of SQL Server doesn't record waits in Query Store: that came with SQL Server 2017.");
                TxtWaitsStatus.Text = string.Empty;
                ClearTab(TabWaits);
            };

        var rows = await _repo.GetQueryStoreWaitCategoriesAsync(_period);
        return () =>
        {
            var captureOff = status.Health?.WaitStatsCaptureMode == "OFF";
            if (rows.Unavailable is { } why)
                WaitsNotice.Show(why);
            else if (captureOff)
                WaitsNotice.Show($"Wait stats capture is off for {status.DatabaseName}, so Query Store records no new waits. Turn it on with:",
                                 $"ALTER DATABASE {Engine.Scripting.UnusedIndexDropScript.QuoteName(status.DatabaseName)} SET QUERY_STORE (WAIT_STATS_CAPTURE_MODE = ON);");
            else
                WaitsNotice.Hide();

            var max = rows.Rows.Count == 0 ? 0 : rows.Rows.Max(c => c.WaitMs);
            WaitCategoriesList.ItemsSource = rows.Rows.Select(c => new WaitBar(c, max > 0 ? c.WaitMs / max : 0)).ToList();
            WaitCategoriesList.Visibility  = rows.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            WaitQueriesGrid.ItemsSource    = null;
            TxtWaitQueriesHeader.Text      = "Click a wait category to see the queries that waited on it. Hover a category for what it means.";
            TxtWaitsStatus.Text = rows.Unavailable is not null ? string.Empty
                : rows.Rows.Count == 0 ? $"No waits recorded · {_period.Label}"
                : Counted(rows.Rows.Count, "wait category", "wait categories") + ", largest first";
        };
    }

    private async void WaitCategoriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;   // not a tab change
        if (WaitCategoriesList.SelectedItem is not WaitBar bar)
            return;

        var version = ++_waitQueriesVersion;
        var period  = _period;
        TxtWaitQueriesHeader.Text = $"Top queries waiting on {bar.Category.Name}: loading…";
        try
        {
            var rows = await _repo.GetQueryStoreWaitQueriesAsync(period, bar.Category.Category, WaitQueriesTop);
            if (version != _waitQueriesVersion)
                return;
            DataGridRefresh.SetItemsSource(WaitQueriesGrid, rows.Rows);
            TxtWaitQueriesHeader.Text = rows.Unavailable ?? $"Top queries waiting on {bar.Category.Name} · {period.Label}";
        }
        catch (Exception ex)
        {
            AppLog.Error("QueryStore: reading a wait category's queries failed", ex);
            if (version == _waitQueriesVersion)
                TxtWaitQueriesHeader.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    // ── Forced Plans ──────────────────────────────────────────────────────────

    private async Task<Action> ReadForcedPlansAsync()
    {
        var forced = await _repo.GetQueryStoreForcedPlansAsync(_period);
        return () =>
        {
            ShowRows(ForcedGrid, ForcedNotice, forced.Plans);
            var failing = forced.Plans.Rows.Count(p => p.ForceFailures > 0);
            TxtForcedStatus.Text = forced.Plans.Unavailable is not null ? string.Empty
                : forced.Plans.Rows.Count == 0 ? "No plan is forced. Executions and avg duration are for " + _period.Label.ToLower(CultureInfo.CurrentCulture) + "."
                : $"{forced.Plans.Rows.Count:N0} forced {(forced.Plans.Rows.Count == 1 ? "plan" : "plans")} as they are now" +
                  (failing > 0 ? $", {failing:N0} failing" : string.Empty) +
                  $" · executions and avg duration for {_period.Label.ToLower(CultureInfo.CurrentCulture)}";

            // Hints only exist on SQL Server 2022+ and Azure SQL.
            var hints = forced.Hints;
            TxtHintsHeader.Visibility = hints is null ? Visibility.Collapsed : Visibility.Visible;
            HintsGrid.Visibility      = hints is { Unavailable: null } ? Visibility.Visible : Visibility.Collapsed;
            if (hints is null)
                HintsNotice.Hide();
            else
                ShowRows(HintsGrid, HintsNotice, hints);
        };
    }

    private void CopyUnforce_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryStoreForcedPlan plan })
            ClipboardHelper.SetText(plan.UnforceScript);
    }

    // ── Plans and Query details ───────────────────────────────────────────────

    // The plan behind a row: the forced plan, or the one used most in the period.
    private static (long QueryId, long? PlanId, string QueryText)? RowQuery(object? row) => QueryStoreDetails.QueryOf(row);

    private async void ViewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || RowQuery(btn.Tag) is not { PlanId: { } planId } q)
            return;
        await ShowPlanAsync(this, _repo, btn, planId, q.QueryId, q.QueryText);
    }

    /// <summary>Opens a Query Store plan in the graphical plan window, as Query Regressions does.</summary>
    internal static async Task ShowPlanAsync(DependencyObject owner, IWaitStatsRepository repo, Button btn, long planId, long queryId, string? queryText)
    {
        var label = btn.Content;
        btn.IsEnabled = false;
        btn.Content   = "…";
        try
        {
            var planXml = await repo.GetQueryStorePlanAsync(planId);
            new QueryExecutionPlanWindow(planXml, queryText)
            {
                Owner = Window.GetWindow(owner),
                Title = $"Execution plan · Query Store plan {planId} · query {queryId}",
            }.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to retrieve execution plan:\n{ConnectionSettingsService.RedactSecrets(ex.Message)}",
                "Plan Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content   = label;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a row: not the header, the scroll bar or a button in a row.
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row
            || RowQuery(row.Item) is not { } q)
            return;
        OpenQueryDetails(q.QueryId);
    }

    private void BtnOpenQuery_Click(object sender, RoutedEventArgs e) => OpenQueryFromBox();

    private void TxtQueryId_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        OpenQueryFromBox();
    }

    private void OpenQueryFromBox()
    {
        if (!long.TryParse(TxtQueryId.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var queryId) || queryId <= 0)
        {
            TxtPeriodError.Text       = "Enter a query ID: a whole number such as 1234, from Query Store or an SSMS report.";
            TxtPeriodError.Visibility = Visibility.Visible;
            return;
        }
        TxtPeriodError.Visibility = Visibility.Collapsed;
        OpenQueryDetails(queryId);
    }

    // A window per query, so several can be open side by side.
    private void OpenQueryDetails(long queryId)
    {
        if (_status is not { IsSupported: true } status)
            return;
        new QueryStoreQueryWindow(_repo, queryId, _period, status)
        {
            Owner = Window.GetWindow(this),
        }.Show();
    }
}
