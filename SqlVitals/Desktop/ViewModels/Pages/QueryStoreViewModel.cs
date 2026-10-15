using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.Defaults;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Query Store for the connection's database (#111, #189): its settings and checks, and the metrics
/// of SSMS's Query Store reports on five tabs. Opening the page reads the settings and the tab on
/// screen; another tab is read the first time it's opened, and again after Refresh or a new period.
/// A tab that can't be read says so in its own status line, and the rest of the page carries on.
/// The verdict (#146) names the queries that got slower in the period against the 7 days before.
/// </summary>
public sealed partial class QueryStoreViewModel : PageViewModel
{
    private const int VariationTop   = 50;
    private const int WaitQueriesTop = 25;

    /// <summary>The tabs, in their order on the page.</summary>
    public enum Tab { Top, OverTime, Variation, Waits, Forced }

    private static readonly Tab[] Tabs = Enum.GetValues<Tab>();

    private readonly IWaitStatsRepository      _repo;
    private readonly ConnectionSettingsService _settings;
    private readonly INavigator                _navigator;
    private readonly IDialogService            _dialogs;
    private readonly IClipboard                _clipboard;
    private readonly TimeProvider              _time;

    private QueryStoreStatus? _storeStatus;
    private QueryStorePeriod  _period = QueryStorePeriod.Default;

    // Whether each tab has been read for what's on screen now, and which read of it is the latest.
    private readonly bool[] _tabCurrent = new bool[Tabs.Length];
    private readonly int[]  _tabVersion = new int[Tabs.Length];

    // The last Over Time read, so turning a chart on or off needs no new read.
    private IReadOnlyList<QueryStoreSlot> _slots = [];
    private TimeSpan          _slotLength;
    private DateTimeOffset    _slotOrigin;
    private QueryStorePeriod? _slotsPeriod;

    private int  _waitQueriesVersion;
    private int  _verdictVersion;
    private bool _choosing;   // choices set by the page itself, not the user

    /// <param name="repo">The connection's database's Query Store; also what a query's window reads.</param>
    /// <param name="settings">Which Over Time charts are shown, and the regression check's thresholds.</param>
    /// <param name="navigator">Where Regressed queries goes.</param>
    public QueryStoreViewModel(IWaitStatsRepository repo, ConnectionSettingsService settings, INavigator navigator,
                               IDialogService dialogs, IClipboard clipboard, TimeProvider? time = null)
    {
        _repo      = repo;
        _settings  = settings;
        _navigator = navigator;
        _dialogs   = dialogs;
        _clipboard = clipboard;
        _time      = time ?? TimeProvider.System;

        PeriodNames  = [.. QueryStorePeriod.Presets.Select(p => p.Label), "Custom…"];
        _periodIndex = QueryStorePeriod.Presets.ToList().IndexOf(QueryStorePeriod.Default);
        StatisticNames = Enum.GetValues<QueryStoreStatistic>()
            .Select(s => s == QueryStoreStatistic.Average ? "Average" : QueryStoreMetrics.Label(s)).ToList();
        VariationMetricNames = QueryStoreMetrics.VariationMetrics.Select(m => QueryStoreMetrics.Info(m).Name).ToList();
        FillMetricChoices(sql2017Metrics: false);
    }

    private DateTime NowUtc => _time.GetUtcNow().UtcDateTime;

    // ── What the top bar shows (#140) ─────────────────────────────────────

    /// <summary>The period shown, as the top bar names it.</summary>
    public string PeriodLabel => _period.Label;

    /// <summary>The period's end as of the last read, on this PC's clock: now, for a period up to now.</summary>
    public DateTime? DataTo { get; private set; }

    private void NoteDataTo()
    {
        var nowUtc = NowUtc;
        var toUtc  = _period.Resolve(nowUtc).ToUtc;
        DataTo = (toUtc < nowUtc ? toUtc : nowUtc).ToLocalTime();
        OnPropertyChanged(nameof(PeriodLabel));
        ReportContextChanged();
    }

    // ── One query, from a link that carries it (#145) ─────────────────────
    // Top Queries shows only it, and its details open in their own window once the settings are read.

    private long? _query;
    private bool  _queryOpened;
    private int?  _queryRowsShown;

    /// <summary>Opened on one query: Top Queries shows only it, from the first read.</summary>
    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter is not { Kind: FocusKind.Query, Id: { } id })
            return;
        _query      = id;
        QueryIdText = id.ToString(CultureInfo.InvariantCulture);
        SelectedTab = (int)Tab.Top;
    }

    /// <summary>Show all: every top query again.</summary>
    public void ClearFocus()
    {
        _query          = null;
        _queryRowsShown = null;
        _tabCurrent[(int)Tab.Top] = false;
        if (SelectedTab == (int)Tab.Top)
            _ = ReportErrorsOf(LoadTabAsync(Tab.Top));
        ReportContextChanged();
    }

    public string? FocusNote => _query is not { } id || _queryRowsShown is not { } shown ? null
        : shown == 0 ? $"query {id} isn't among the top queries of {_period.Label.ToLowerInvariant()}; its details are in their own window"
        : $"showing query {id} only; its details are in their own window";

    public string? ShowAllText => _query is null ? null : "Show all queries";

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>Reads the settings again, and the tab on screen; the others are read when next opened.</summary>
    protected override async Task LoadAsync(CancellationToken cancel)
    {
        NoteDataTo();
        var status = await _repo.GetQueryStoreStatusAsync(cancel);
        _storeStatus = status;
        ShowStatus(status);
        LoadVerdict();

        // Opened on one query: its details, once (#145).
        if (_query is { } query && !_queryOpened)
        {
            _queryOpened = true;
            OpenQuery(query);
        }

        Array.Fill(_tabCurrent, false);
        await LoadTabAsync((Tab)SelectedTab);
    }

    // Only the settings read throws: a tab's failure stays in its status line.
    protected override void ShowLoadError(Exception exception) => ShowError("Query Store's settings", exception);

    /// <summary>The tab on screen; another tab is read when it is opened, if what's on screen changed since.</summary>
    [ObservableProperty]
    private int _selectedTab;

    partial void OnSelectedTabChanged(int value)
    {
        if (_storeStatus is not null && !_tabCurrent[value])
            _ = ReportErrorsOf(LoadTabAsync((Tab)value));
    }

    /// <summary>The latest read of a tab; for tests.</summary>
    internal Task TabLoaded { get; private set; } = Task.CompletedTask;

    // Reads one tab. A read that finishes after a newer one started is dropped; an error shows in
    // the tab's status line, so the rest of the page and the other tabs carry on.
    private Task LoadTabAsync(Tab tab) => TabLoaded = ReadTabAsync(tab);

    private async Task ReadTabAsync(Tab tab)
    {
        if (_storeStatus is not { } status)
            return;

        var version = ++_tabVersion[(int)tab];
        _tabCurrent[(int)tab] = true;

        if (!status.IsAvailable)
        {
            SetNotice(tab, new Notice(status.UnavailableReason!, status.EnableScript));
            SetTabStatus(tab, string.Empty);
            ClearTab(tab);
            return;
        }

        SetTabStatus(tab, "Loading…");
        try
        {
            var show = await ReadAsync(tab, status);
            if (version == _tabVersion[(int)tab])
                show();
        }
        catch (OperationCanceledException)
        {
            // Left: nothing on screen is about it any more. Read again when shown, unless a newer read has.
            if (version == _tabVersion[(int)tab])
                _tabCurrent[(int)tab] = false;
        }
        catch (Exception ex)
        {
            AppLog.Error($"QueryStore: reading the {tab} tab failed", ex);
            if (version == _tabVersion[(int)tab])
            {
                SetTabStatus(tab, ErrorStatus.Format(ex));
                _tabCurrent[(int)tab] = false;   // read again when next opened
            }
        }
    }

    // Reads a tab's data and returns what puts it on screen.
    private Task<Action> ReadAsync(Tab tab, QueryStoreStatus status) => tab switch
    {
        Tab.Top       => ReadTopQueriesAsync(),
        Tab.OverTime  => ReadOverTimeAsync(status),
        Tab.Variation => ReadVariationAsync(),
        Tab.Waits     => ReadWaitsAsync(status),
        _             => ReadForcedPlansAsync(),
    };

    private void SetTabStatus(Tab tab, string text)
    {
        switch (tab)
        {
            case Tab.Top:       TopStatus       = text; break;
            case Tab.OverTime:  OverTimeStatus  = text; break;
            case Tab.Variation: VariationStatus = text; break;
            case Tab.Waits:     WaitsStatus     = text; break;
            default:            ForcedStatus    = text; break;
        }
    }

    private void SetNotice(Tab tab, Notice? notice)
    {
        switch (tab)
        {
            case Tab.Top:       TopNotice       = notice; break;
            case Tab.OverTime:  OverTimeNotice  = notice; break;
            case Tab.Variation: VariationNotice = notice; break;
            case Tab.Waits:     WaitsNotice     = notice; break;
            default:            ForcedNotice    = notice; break;
        }
    }

    private void ClearTab(Tab tab)
    {
        switch (tab)
        {
            case Tab.Top:       TopRows.Replace([]); break;
            case Tab.OverTime:  _slotsPeriod = null; ShowCharts(); break;
            case Tab.Variation: VariationRows.Replace([]); break;
            case Tab.Waits:     WaitBars = []; WaitQueryRows.Replace([]); break;
            default:
                ForcedRows.Replace([]);
                ShowsHints = ShowsHintsHeader = false;
                HintsNotice = null;
                break;
        }
    }

    /// <summary>What a tab says instead of rows, with the statement that fixes it to copy; null hides it.</summary>
    public sealed record Notice(string Message, string? Script = null);

    // Rows, or the reason there are none (a view or column this version lacks).
    private static Notice? NoticeOf<T>(QueryStoreRows<T> rows) => rows.Unavailable is { } why ? new Notice(why) : null;

    private string Counted(int count, string one, string many) =>
        $"{count:N0} {(count == 1 ? one : many)} · {_period.Label}";

    // ── Settings and health ───────────────────────────────────────────────

    /// <summary>"Query Store — Sales".</summary>
    [ObservableProperty]
    private string _title = "Query Store";

    /// <summary>The settings and health as cards.</summary>
    [ObservableProperty]
    private IReadOnlyList<KpiCard> _cards = [];

    /// <summary>The settings checks: why, and the fix to copy.</summary>
    [ObservableProperty]
    private IReadOnlyList<QueryStoreCheck> _checks = [];

    /// <summary>The health section folded, in one line (#146): "READ_WRITE · 41 % of storage · 2 warnings".</summary>
    [ObservableProperty]
    private string? _healthSummary;

    /// <summary>"Query Store has data from 2 Sep 14:00", and whether the period reaches further back.</summary>
    [ObservableProperty]
    private string _dataFromText = string.Empty;

    /// <summary>The card that opens the Forced Plans tab.</summary>
    internal const string ForcedPlansCard = "Forced plans";

    private void ShowStatus(QueryStoreStatus status)
    {
        Title = status.DatabaseName.Length == 0 ? "Query Store" : $"Query Store — {status.DatabaseName}";
        FillMetricChoices(status.Features.Sql2017Metrics);

        var h = status.Health;
        if (h is null)
        {
            Cards         = [new("Query Store", "Not supported", KpiCard.Tones.Muted, "Query Store came with SQL Server 2016.")];
            Checks        = [];
            HealthSummary = "Not supported: Query Store came with SQL Server 2016";
            DataFromText  = string.Empty;
            return;
        }

        var checks = QueryStoreChecks.Run(h);
        var worst  = QueryStoreChecks.Worst(checks);
        var cards  = new List<KpiCard>
        {
            new("State", h.ActualState, h.IsEnabled ? (h.ActualState == "READ_WRITE" ? KpiCard.Tones.Success : KpiCard.Tones.Warning) : KpiCard.Tones.Danger,
                h.ReadOnlyReason is { } why ? $"Set to {h.DesiredState}; read-only because {why}." : $"Set to {h.DesiredState}."),
            new("Storage used", $"{h.CurrentStorageSizeMB:N0} MB",
                h.StorageUsedPct > 90 ? KpiCard.Tones.Danger : h.StorageUsedPct > 75 ? KpiCard.Tones.Warning : KpiCard.Tones.Success,
                $"Of MAX_STORAGE_SIZE_MB = {h.MaxStorageSizeMB:N0}."),
            new("Storage %", $"{h.StorageUsedPct:N1} %", h.StorageUsedPct > 90 ? KpiCard.Tones.Danger : KpiCard.Tones.Accent,
                "Size-based cleanup (when AUTO) starts at 90 %; at 100 % Query Store turns read-only."),
            new("Queries", $"{h.TotalQueries:N0}", KpiCard.Tones.Accent, "Queries Query Store holds."),
            new("Plans", $"{h.TotalPlans:N0}", KpiCard.Tones.Accent, $"Plans Query Store holds; at most {h.MaxPlansPerQuery:N0} per query."),
            new(ForcedPlansCard, $"{h.ForcedPlans:N0}", h.ForcedPlans > 0 ? KpiCard.Tones.Warning : KpiCard.Tones.Muted,
                "Plans forced by hand or by automatic plan correction. Click to open the Forced Plans tab."),
            new("Capture mode", h.CaptureMode, h.CaptureMode == "ALL" ? KpiCard.Tones.Info : KpiCard.Tones.Muted,
                "Which queries Query Store records: ALL, AUTO (skips trivial ad hoc queries), CUSTOM or NONE."),
            new("Size cleanup", h.SizeCleanupMode, h.SizeCleanupMode == "OFF" ? KpiCard.Tones.Warning : KpiCard.Tones.Muted,
                "AUTO removes the oldest, cheapest queries as Query Store nears its maximum size."),
            new("Stale after", $"{h.StaleQueryThresholdDays:N0} days", KpiCard.Tones.Muted,
                "How long Query Store keeps a query that hasn't run (STALE_QUERY_THRESHOLD_DAYS)."),
            new("Interval", $"{h.IntervalLengthMin:N0} min", KpiCard.Tones.Muted, "How long each runtime statistics interval is (INTERVAL_LENGTH_MINUTES)."),
        };
        if (h.WaitStatsCaptureMode is { } waits)
            cards.Add(new("Wait stats capture", waits, waits == "OFF" ? KpiCard.Tones.Info : KpiCard.Tones.Muted,
                          "Whether Query Store records waits per query (SQL Server 2017+)."));
        cards.Add(new("Status", worst switch { QueryStoreCheckLevel.Warning => "Warning", QueryStoreCheckLevel.Info => "Info", _ => "OK" },
                      worst switch { QueryStoreCheckLevel.Warning => KpiCard.Tones.Warning, QueryStoreCheckLevel.Info => KpiCard.Tones.Info, _ => KpiCard.Tones.Success },
                      "The worst of the settings checks listed below the cards."));
        Cards  = cards;
        Checks = checks;

        var warnings = checks.Count(c => c.Level == QueryStoreCheckLevel.Warning);
        HealthSummary = string.Format(CultureInfo.CurrentCulture, "{0} · {1:N1} % of storage · {2}",
            h.ActualState, h.StorageUsedPct,
            warnings switch { 0 => "settings OK", 1 => "1 settings warning", var n => $"{n} settings warnings" });
        ShowDataFrom();
    }

    private void ShowDataFrom()
    {
        if (_storeStatus?.Health is not { } h)
            return;
        if (h.DataFromUtc is not { } from)
        {
            DataFromText = "Query Store holds no runtime statistics yet.";
            return;
        }

        var text = $"Query Store has data from {from.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture)} " +
                   $"(it keeps queries for {h.StaleQueryThresholdDays:N0} days).";
        if (_period.Resolve(NowUtc).FromUtc < from)
            text += " The period starts earlier, so it shows only what exists.";
        DataFromText = text;
    }

    // A card pressed: Forced plans opens its tab.
    [RelayCommand]
    private void OpenCard(KpiCard? card)
    {
        if (card?.Label == ForcedPlansCard)
            SelectedTab = (int)Tab.Forced;
    }

    [RelayCommand]
    private void CopyFix(QueryStoreCheck? check)
    {
        if (check?.Fix is { } fix)
            _clipboard.SetText(fix);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Regressions() => ReportErrorsOf(_navigator.GoToAsync("QueryRegressions"));

    // ── The verdict (#146) ────────────────────────────────────────────────
    // The queries that got slower in the period against the 7 days before, as Query Regressions
    // finds them with its saved threshold, read beside the tab on screen.

    /// <summary>The verdict's read, for tests; done once it has been shown.</summary>
    internal Task VerdictLoaded { get; private set; } = Task.CompletedTask;

    private void LoadVerdict() => VerdictLoaded = LoadVerdictAsync();

    private async Task LoadVerdictAsync()
    {
        if (_storeStatus is not { } status)
            return;
        var version    = ++_verdictVersion;
        var period     = _period;
        var criteria   = _settings.Load().RegressionCriteria;
        var comparison = $"{period.Label} compared with the 7 days before";

        // Query Store off, or a custom period: nothing to compare (the verdict says which).
        if (!status.IsAvailable || period.IsCustom)
        {
            Verdict = QueryStoreVerdict.From(status, null, comparison, criteria);
            return;
        }

        try
        {
            var stats = await _repo.GetQueryStoreRegressionStatsAsync(new RegressionWindows(period.Length, RegressionBaselineKind.PreviousWeek));
            var found = stats.IsAvailable ? await Task.Run(() => QueryRegressionDetector.Detect(stats.Stats, criteria)) : null;
            if (version == _verdictVersion)
                Verdict = QueryStoreVerdict.From(status, found, comparison, criteria,
                    stats.IsAvailable ? null : $"Query Store's runtime statistics for {stats.DatabaseName} can't be read: it is {stats.State.Replace('_', ' ').ToLowerInvariant()}.");
        }
        catch (OperationCanceledException)
        {
            // Left: nothing to say.
        }
        catch (Exception ex)
        {
            AppLog.Error("QueryStore: the regression check for the verdict failed", ex);
            if (version == _verdictVersion)
                ShowError("the regression check", ex);
        }
    }

    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case QueryStoreVerdict.OpenQuery when action.Id is { } id: OpenQuery(id); break;
            case QueryStoreVerdict.Regressions:                       return _navigator.GoToAsync("QueryRegressions");
        }
        return Task.CompletedTask;
    }

    // ── Period ────────────────────────────────────────────────────────────

    /// <summary>The presets, then "Custom…".</summary>
    public IReadOnlyList<string> PeriodNames { get; }

    private int CustomIndex => PeriodNames.Count - 1;

    /// <summary>What every tab but Forced Plans reads.</summary>
    [ObservableProperty]
    private int _periodIndex;

    /// <summary>Custom… was picked in the period box: the view puts the cursor in its From time.</summary>
    public event Action? CustomPeriodOpened;

    partial void OnPeriodIndexChanged(int value)
    {
        if (_choosing || value < 0)
            return;

        if (value < QueryStorePeriod.Presets.Count)
        {
            ShowsCustom = false;
            PeriodError = null;
            var preset = QueryStorePeriod.Presets[value];
            if (preset != _period)
                _ = ReportErrorsOf(SetPeriodAsync(preset));
            return;
        }

        // Custom: start from the period on screen.
        ShowCustom(_period.Resolve(NowUtc));
        CustomPeriodOpened?.Invoke();
    }

    [ObservableProperty] private DateTime? _customFromDate;
    [ObservableProperty] private string _customFromTime = string.Empty;
    [ObservableProperty] private DateTime? _customToDate;
    [ObservableProperty] private string _customToTime = string.Empty;

    /// <summary>The custom period's boxes show.</summary>
    [ObservableProperty]
    private bool _showsCustom;

    /// <summary>What is wrong with the custom period or the query ID typed; null when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPeriodError))]
    private string? _periodError;

    public bool ShowsPeriodError => PeriodError is not null;

    private void ShowCustom((DateTime FromUtc, DateTime ToUtc) period)
    {
        CustomFromDate = period.FromUtc.ToLocalTime().Date;
        CustomFromTime = period.FromUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        CustomToDate   = period.ToUtc.ToLocalTime().Date;
        CustomToTime   = period.ToUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        ShowsCustom    = true;
    }

    /// <summary>Show, or Enter in a time box: reads the custom period typed.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ApplyCustom()
    {
        if (!QueryStorePeriod.TryParseCustom(CustomFromDate, CustomFromTime, CustomToDate, CustomToTime, NowUtc, out var period, out var error))
        {
            PeriodError = error;
            return Task.CompletedTask;
        }
        PeriodError = null;
        return ReportErrorsOf(SetPeriodAsync(period!));
    }

    // Every tab is read again for the new period: the one on screen now, the others when opened.
    private Task SetPeriodAsync(QueryStorePeriod period)
    {
        _period = period;
        NoteDataTo();
        ShowDataFrom();
        LoadVerdict();
        Array.Fill(_tabCurrent, false);
        return LoadTabAsync((Tab)SelectedTab);
    }

    // ── Top Queries ───────────────────────────────────────────────────────

    /// <summary>What a query can be ranked by; Log and TempDB used only where the server records them.</summary>
    [ObservableProperty]
    private IReadOnlyList<QueryStoreMetricInfo> _rankChoices = [];

    [ObservableProperty]
    private int _rankIndex;

    public IReadOnlyList<string> StatisticNames { get; }

    [ObservableProperty]
    private int _statisticIndex;

    public IReadOnlyList<string> TopNames { get; } = ["Top 20", "Top 50", "Top 100"];

    private static readonly int[] TopCounts = [20, 50, 100];

    [ObservableProperty]
    private int _topIndex;

    /// <summary>Executions are a count: there is no average or std dev of it per execution.</summary>
    public bool StatisticEnabled => SelectedRank != QueryStoreMetric.Executions;

    private QueryStoreMetric SelectedRank => RankChoices.Count == 0 ? QueryStoreMetric.Cpu : RankChoices[Math.Clamp(RankIndex, 0, RankChoices.Count - 1)].Metric;
    private QueryStoreStatistic SelectedStatistic => (QueryStoreStatistic)Math.Clamp(StatisticIndex, 0, StatisticNames.Count - 1);
    private int SelectedTop => TopCounts[Math.Clamp(TopIndex, 0, TopCounts.Length - 1)];

    partial void OnRankIndexChanged(int value) => TopChoiceChanged();
    partial void OnStatisticIndexChanged(int value) => TopChoiceChanged();
    partial void OnTopIndexChanged(int value) => TopChoiceChanged();

    private void TopChoiceChanged()
    {
        OnPropertyChanged(nameof(StatisticEnabled));
        if (!_choosing && _storeStatus is not null)
            _ = ReportErrorsOf(LoadTabAsync(Tab.Top));
    }

    public GridRows<QueryStoreQueryRow> TopRows { get; } = [];

    [ObservableProperty] private string _topStatus = string.Empty;
    [ObservableProperty] private Notice? _topNotice;

    /// <summary>The ranked column: its header ("Total CPU ms"), its number format, and whether Total CPU repeats it.</summary>
    [ObservableProperty] private string _topMetricHeader = "Total CPU ms";
    [ObservableProperty] private string _topMetricFormat = "N1";
    [ObservableProperty] private bool _showsTotalCpu;

    [ObservableProperty] private QueryStoreQueryRow? _selectedTopRow;

    private async Task<Action> ReadTopQueriesAsync()
    {
        var (metric, statistic, top) = (SelectedRank, SelectedStatistic, SelectedTop);
        var rows = await _repo.GetQueryStoreTopQueriesAsync(_period, metric, statistic, top);
        return () =>
        {
            TopMetricHeader = QueryStoreMetrics.ColumnHeader(metric, statistic);
            TopMetricFormat = QueryStoreMetrics.Info(metric).Format;
            // Ranked by total CPU, the ranked column already is the total CPU column.
            ShowsTotalCpu = !(metric == QueryStoreMetric.Cpu && statistic == QueryStoreStatistic.Total);
            if (_query is { } query)
            {
                rows            = rows with { Rows = rows.Rows.Where(r => r.QueryId == query).ToList() };
                _queryRowsShown = rows.Rows.Count;
                ReportContextChanged();
            }
            TopNotice      = NoticeOf(rows);
            Refill(TopRows, rows.Rows, SelectedTopRow, r => SelectedTopRow = r);
            TopStatus      = rows.Unavailable is null ? Counted(rows.Rows.Count, "query", "queries") : string.Empty;
        };
    }

    // Log used and TempDB used only where the server records them.
    private void FillMetricChoices(bool sql2017Metrics)
    {
        var available = QueryStoreMetrics.Available(sql2017Metrics);
        if (RankChoices.Count == available.Count)
            return;

        _choosing = true;
        try
        {
            var selected = SelectedRank;
            RankChoices = available;
            RankIndex   = Math.Max(0, available.ToList().FindIndex(m => m.Metric == selected));

            var saved = _settings.Load().QueryStoreChartMetrics;
            foreach (var item in ChartMetrics)
                item.PropertyChanged -= OnChartMetricChanged;
            ChartMetrics = available.Select(m => new ChartMetricItem(m, saved.Contains(m.Metric))).ToList();
            foreach (var item in ChartMetrics)
                item.PropertyChanged += OnChartMetricChanged;
        }
        finally
        {
            _choosing = false;
        }
    }

    // ── Over Time ─────────────────────────────────────────────────────────

    /// <summary>An Over Time chart the user can turn on or off.</summary>
    public sealed partial class ChartMetricItem(QueryStoreMetricInfo info, bool isChecked) : ObservableObject
    {
        public QueryStoreMetricInfo Info { get; } = info;
        public string Name => Info.Name;

        [ObservableProperty]
        private bool _isChecked = isChecked;
    }

    [ObservableProperty]
    private IReadOnlyList<ChartMetricItem> _chartMetrics = [];

    [ObservableProperty] private string _overTimeStatus = string.Empty;
    [ObservableProperty] private Notice? _overTimeNotice;

    /// <summary>What Over Time draws: a chart per metric ticked on one time axis, or why there is none.</summary>
    [ObservableProperty]
    private OverTimeCharts _overTime = OverTimeCharts.None;

    /// <summary>The charts of Over Time; the view draws each in the theme's series colours.</summary>
    /// <param name="Empty">Why there is no chart, e.g. nothing recorded in the period; null otherwise.</param>
    /// <param name="From">The time axis' start on this PC's clock.</param>
    /// <param name="AxisFormat">How the time axis labels its ticks.</param>
    public sealed record OverTimeCharts(IReadOnlyList<OverTimeChart> Charts, string? Empty, DateTime From, DateTime To, TimeSpan Slot, string AxisFormat)
    {
        public static readonly OverTimeCharts None = new([], null, default, default, TimeSpan.FromHours(1), "HH:mm");
    }

    /// <summary>One metric's totals per slot; a null point is a slot with nothing recorded, a gap rather than a zero.</summary>
    /// <param name="Colour">Its place in the chart palette.</param>
    public sealed record OverTimeChart(string Name, IReadOnlyList<DateTimePoint> Points, int Colour);

    private async Task<Action> ReadOverTimeAsync(QueryStoreStatus status)
    {
        var period = _period;
        var slot   = QueryStoreTimeSlots.SlotLength(period.Length, status.Health!.IntervalLengthMin);
        var origin = QueryStoreTimeSlots.Origin(slot, _time.GetLocalNow().Offset);
        var rows   = await _repo.GetQueryStoreOverTimeAsync(period, slot, origin);
        return () =>
        {
            _slots = rows.Rows;
            (_slotLength, _slotOrigin, _slotsPeriod) = (slot, origin, period);
            OverTimeNotice = NoticeOf(rows);
            OverTimeStatus = $"The database's totals, each point {DescribeSlot(slot)} · {period.Label}. Click a point for its top queries.";
            ShowCharts();
        };
    }

    private static string DescribeSlot(TimeSpan slot) =>
        slot >= TimeSpan.FromDays(1) ? "a day" : slot >= TimeSpan.FromHours(1) ? (slot == TimeSpan.FromHours(1) ? "an hour" : $"{slot.TotalHours:0} hours")
        : $"{slot.TotalMinutes:0} minutes";

    // A chart turned on or off: saved, and drawn from what was read.
    private void OnChartMetricChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_choosing || e.PropertyName != nameof(ChartMetricItem.IsChecked))
            return;

        var chosen = ChartMetrics.Where(i => i.IsChecked).Select(i => i.Info.Metric).ToList();
        try
        {
            // Metrics this server doesn't offer (log and TempDB before 2017) stay saved for the next one.
            var offered = ChartMetrics.Select(i => i.Info.Metric).ToHashSet();
            var saved   = _settings.Load().QueryStoreChartMetrics;
            if (chosen.Count > 0 && !chosen.ToHashSet().SetEquals(saved.Where(offered.Contains)))
                _settings.SetQueryStoreChartMetrics(chosen.Concat(saved.Where(m => !offered.Contains(m))).ToList());
        }
        catch (Exception ex)
        {
            // The charts still change; the choice just won't be remembered.
            AppLog.Error("QueryStore: saving the chart choice failed", ex);
        }
        ShowCharts();
    }

    // One chart per chosen metric, all on the same time axis. A slot where Query Store recorded
    // nothing is a gap, not a zero: it may not have been recording.
    private void ShowCharts()
    {
        if (_slotsPeriod is null)
        {
            OverTime = OverTimeCharts.None;
            return;
        }
        if (_slots.Count == 0)
        {
            OverTime = OverTimeCharts.None with
            {
                Empty = $"Query Store recorded nothing in this period ({_slotsPeriod.Label.ToLower(CultureInfo.CurrentCulture)}).",
            };
            return;
        }

        var (fromUtc, toUtc) = _slotsPeriod.Resolve(NowUtc);
        var bySlot = _slots.ToDictionary(s => s.StartUtc);
        var starts = QueryStoreTimeSlots.SlotsBetween(_slotOrigin, _slotLength, fromUtc, toUtc)
            .Select(i => QueryStoreTimeSlots.SlotStartUtc(_slotOrigin, _slotLength, i))
            .Union(bySlot.Keys)
            .OrderBy(t => t)
            .ToList();

        var charts = ChartMetrics.Where(i => i.IsChecked).Select((item, index) =>
        {
            var metric = item.Info.Metric;
            var points = starts.Select(t => new DateTimePoint(t.ToLocalTime(), bySlot.TryGetValue(t, out var s) ? s.Totals.Total(metric) : null)).ToList();
            return new OverTimeChart(metric == QueryStoreMetric.Executions ? item.Info.Name : $"Total {item.Info.NameWithUnit}", points, index);
        }).ToList();

        OverTime = new OverTimeCharts(charts, null, starts[0].ToLocalTime(), (starts[^1] + _slotLength).ToLocalTime(), _slotLength,
                                      ConnectionHistory.AxisFormat(toUtc - fromUtc));
    }

    /// <summary>A point on an Over Time chart was clicked: Top Queries for its time slot.</summary>
    /// <param name="slotStart">The point's time, on this PC's clock.</param>
    public Task PickSlotAsync(DateTime slotStart)
    {
        var fromUtc = DateTime.SpecifyKind(slotStart, DateTimeKind.Local).ToUniversalTime();
        var slot    = QueryStorePeriod.Custom(fromUtc, fromUtc + _slotLength);

        // The period box says Custom, with the slot in it.
        _choosing = true;
        PeriodIndex = CustomIndex;
        _choosing = false;
        ShowCustom((slot.FromUtc!.Value, slot.ToUtc!.Value));

        _period = slot;
        NoteDataTo();
        ShowDataFrom();
        LoadVerdict();
        Array.Fill(_tabCurrent, false);

        if (SelectedTab != (int)Tab.Top)
        {
            SelectedTab = (int)Tab.Top;   // reads it
            return TabLoaded;
        }
        return ReportErrorsOf(LoadTabAsync(Tab.Top));
    }

    // ── High Variation ────────────────────────────────────────────────────

    public IReadOnlyList<string> VariationMetricNames { get; }

    [ObservableProperty]
    private int _variationMetricIndex;

    partial void OnVariationMetricIndexChanged(int value)
    {
        if (_storeStatus is not null)
            _ = ReportErrorsOf(LoadTabAsync(Tab.Variation));
    }

    private QueryStoreMetric SelectedVariationMetric =>
        QueryStoreMetrics.VariationMetrics[Math.Clamp(VariationMetricIndex, 0, QueryStoreMetrics.VariationMetrics.Count - 1)];

    /// <summary>The metric the rows were read for, whose unit and format the columns take.</summary>
    [ObservableProperty]
    private QueryStoreMetricInfo _variationMetric = QueryStoreMetrics.Info(QueryStoreMetrics.VariationMetrics[0]);

    public GridRows<QueryStoreVariationRow> VariationRows { get; } = [];

    [ObservableProperty] private string _variationStatus = string.Empty;
    [ObservableProperty] private Notice? _variationNotice;
    [ObservableProperty] private QueryStoreVariationRow? _selectedVariationRow;

    private async Task<Action> ReadVariationAsync()
    {
        var metric = SelectedVariationMetric;
        var rows = await _repo.GetQueryStoreHighVariationAsync(_period, metric, QueryStoreMetrics.MinVariationExecutions, VariationTop);
        return () =>
        {
            VariationMetric      = QueryStoreMetrics.Info(metric);
            VariationNotice      = NoticeOf(rows);
            Refill(VariationRows, rows.Rows, SelectedVariationRow, r => SelectedVariationRow = r);
            VariationStatus      = rows.Unavailable is null
                ? Counted(rows.Rows.Count, "query", "queries") + $" · at least {QueryStoreMetrics.MinVariationExecutions} executions each"
                : string.Empty;
        };
    }

    // ── Waits ─────────────────────────────────────────────────────────────

    /// <summary>One bar of the Waits chart.</summary>
    public sealed record WaitBar(QueryStoreWaitCategory Category, double Fraction)
    {
        public GridLength FillWidth => new(Math.Max(Fraction, 0.001), GridUnitType.Star);
        public GridLength RestWidth => new(Math.Max(1 - Fraction, 0.001), GridUnitType.Star);
        public string ValueText => string.Format(CultureInfo.CurrentCulture, "{0:N0} ms · {1:N1} %", Category.WaitMs, Category.PctOfAll);
        public string Explanation => Category.Explanation;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWaitBars))]
    private IReadOnlyList<WaitBar> _waitBars = [];

    public bool ShowsWaitBars => WaitBars.Count > 0;

    [ObservableProperty] private string _waitsStatus = string.Empty;
    [ObservableProperty] private Notice? _waitsNotice;

    /// <summary>The wait category picked: its queries are read.</summary>
    [ObservableProperty]
    private WaitBar? _selectedWaitBar;

    [ObservableProperty]
    private string _waitQueriesHeader = "Click a wait category to see the queries that waited on it.";

    public GridRows<QueryStoreWaitQuery> WaitQueryRows { get; } = [];

    [ObservableProperty] private QueryStoreWaitQuery? _selectedWaitQueryRow;

    /// <summary>The latest read of a wait category's queries; for tests.</summary>
    internal Task WaitQueriesLoaded { get; private set; } = Task.CompletedTask;

    private async Task<Action> ReadWaitsAsync(QueryStoreStatus status)
    {
        if (!status.Features.WaitStats)
            return () =>
            {
                WaitsNotice = new("This version of SQL Server doesn't record waits in Query Store: that came with SQL Server 2017.");
                WaitsStatus = string.Empty;
                ClearTab(Tab.Waits);
            };

        var rows = await _repo.GetQueryStoreWaitCategoriesAsync(_period);
        return () =>
        {
            var captureOff = status.Health?.WaitStatsCaptureMode == "OFF";
            WaitsNotice = rows.Unavailable is { } why ? new Notice(why)
                : captureOff ? new Notice($"Wait stats capture is off for {status.DatabaseName}, so Query Store records no new waits. Turn it on with:",
                                          $"ALTER DATABASE {Engine.Scripting.UnusedIndexDropScript.QuoteName(status.DatabaseName)} SET QUERY_STORE (WAIT_STATS_CAPTURE_MODE = ON);")
                : null;

            var max = rows.Rows.Count == 0 ? 0 : rows.Rows.Max(c => c.WaitMs);
            _waitQueriesVersion++;   // a category's read in flight is about the bars before
            // None picked among the new bars: a bar equal to the one picked before reads its queries again when picked.
            SelectedWaitBar = null;
            WaitBars = rows.Rows.Select(c => new WaitBar(c, max > 0 ? c.WaitMs / max : 0)).ToList();
            WaitQueryRows.Replace([]);
            WaitQueriesHeader = "Click a wait category to see the queries that waited on it. Hover a category for what it means.";
            WaitsStatus = rows.Unavailable is not null ? string.Empty
                : rows.Rows.Count == 0 ? $"No waits recorded · {_period.Label}"
                : Counted(rows.Rows.Count, "wait category", "wait categories") + ", largest first";
        };
    }

    partial void OnSelectedWaitBarChanged(WaitBar? value)
    {
        if (value is not null)
            WaitQueriesLoaded = ReportErrorsOf(ReadWaitQueriesAsync(value));
    }

    private async Task ReadWaitQueriesAsync(WaitBar bar)
    {
        var version = ++_waitQueriesVersion;
        var period  = _period;
        WaitQueriesHeader = $"Top queries waiting on {bar.Category.Name}: loading…";
        try
        {
            var rows = await _repo.GetQueryStoreWaitQueriesAsync(period, bar.Category.Category, WaitQueriesTop);
            if (version != _waitQueriesVersion)
                return;
            Refill(WaitQueryRows, rows.Rows, SelectedWaitQueryRow, r => SelectedWaitQueryRow = r);
            WaitQueriesHeader    = rows.Unavailable ?? $"Top queries waiting on {bar.Category.Name} · {period.Label}";
        }
        catch (OperationCanceledException)
        {
            // Left: nothing to say.
        }
        catch (Exception ex)
        {
            AppLog.Error("QueryStore: reading a wait category's queries failed", ex);
            if (version == _waitQueriesVersion)
                WaitQueriesHeader = ErrorStatus.Format(ex);
        }
    }

    // ── Forced Plans ──────────────────────────────────────────────────────

    public GridRows<QueryStoreForcedPlan> ForcedRows { get; } = [];

    [ObservableProperty] private string _forcedStatus = string.Empty;
    [ObservableProperty] private Notice? _forcedNotice;
    [ObservableProperty] private QueryStoreForcedPlan? _selectedForcedRow;

    /// <summary>Query Store hints: SQL Server 2022+ and Azure SQL only.</summary>
    public GridRows<QueryStoreHint> HintRows { get; } = [];

    [ObservableProperty] private bool _showsHintsHeader;
    [ObservableProperty] private bool _showsHints;
    [ObservableProperty] private Notice? _hintsNotice;
    [ObservableProperty] private QueryStoreHint? _selectedHintRow;

    private async Task<Action> ReadForcedPlansAsync()
    {
        var forced = await _repo.GetQueryStoreForcedPlansAsync(_period);
        return () =>
        {
            ForcedNotice      = NoticeOf(forced.Plans);
            Refill(ForcedRows, forced.Plans.Rows, SelectedForcedRow, r => SelectedForcedRow = r);
            var count   = forced.Plans.Rows.Count;
            var failing = forced.Plans.Rows.Count(p => p.ForceFailures > 0);
            var period  = _period.Label.ToLower(CultureInfo.CurrentCulture);
            ForcedStatus = forced.Plans.Unavailable is not null ? string.Empty
                : count == 0 ? $"No plan is forced. Executions and avg duration are for {period}."
                : $"{count:N0} forced {(count == 1 ? "plan" : "plans")} as they are now" +
                  (failing > 0 ? $", {failing:N0} failing" : string.Empty) +
                  $" · executions and avg duration for {period}";

            var hints = forced.Hints;
            ShowsHintsHeader = hints is not null;
            ShowsHints       = hints is { Unavailable: null };
            HintsNotice      = hints is null ? null : NoticeOf(hints);
            Refill(HintRows, hints?.Rows ?? [], SelectedHintRow, r => SelectedHintRow = r);
        };
    }

    [RelayCommand]
    private void CopyUnforce(QueryStoreForcedPlan? plan)
    {
        if (plan is not null)
            _clipboard.SetText(plan.UnforceScript);
    }

    // ── The query selected, its plans, and its window ─────────────────────

    /// <summary>
    /// The row selected last in any tab, whose query the drawer shows (#147). It stays until
    /// another row is selected, so a read or another tab doesn't lose it.
    /// </summary>
    [ObservableProperty]
    private object? _selected;

    partial void OnSelectedTopRowChanged(QueryStoreQueryRow? value) => Select(value);
    partial void OnSelectedVariationRowChanged(QueryStoreVariationRow? value) => Select(value);
    partial void OnSelectedWaitQueryRowChanged(QueryStoreWaitQuery? value) => Select(value);
    partial void OnSelectedForcedRowChanged(QueryStoreForcedPlan? value) => Select(value);
    partial void OnSelectedHintRowChanged(QueryStoreHint? value) => Select(value);

    // A row the user selected is the drawer's. One a read selected again is only when it is the
    // drawer's query already (its figures as read now): the drawer stays on the row picked last,
    // on whichever tab (#147).
    private void Select(object? row)
    {
        if (row is null)
            return;
        if (ReferenceEquals(row, _reselected)
            && (Selected?.GetType() != row.GetType() || QueryStoreDetails.QueryOf(Selected) != QueryStoreDetails.QueryOf(row)))
            return;
        Selected = row;
    }

    private object? _reselected;

    // Refills a grid, and selects again the row with the same query and plan as before, if it's still there.
    private void Refill<T>(GridRows<T> grid, IReadOnlyList<T> rows, T? selected, Action<T?> select) where T : class
    {
        var key = QueryStoreDetails.QueryOf(selected);
        grid.Replace(rows);
        var again = key is null ? null : rows.FirstOrDefault(r => QueryStoreDetails.QueryOf(r) == key);
        _reselected = again;
        try
        {
            select(again);
        }
        finally
        {
            _reselected = null;
        }
    }

    /// <summary>The period's label, for the drawer's figures.</summary>
    public string PeriodName => _period.Label;

    /// <summary>A query's plans in the period, for the drawer.</summary>
    public async Task<IReadOnlyList<QueryStorePlanSummary>> ReadPlansAsync(long queryId) =>
        (await _repo.GetQueryStoreQueryDetailsAsync(queryId, _period)).Summaries;

    /// <summary>A row's View Plan: the forced plan, or the one used most in the period.</summary>
    [RelayCommand]
    private Task ViewPlan(object? row) =>
        QueryStoreDetails.QueryOf(row) is { PlanId: { } planId } q ? ShowPlanAsync(planId, q.QueryId, q.QueryText) : Task.CompletedTask;

    /// <summary>Opens a Query Store plan in the graphical plan window, as Query Regressions does; a failure says why.</summary>
    public Task ShowPlanAsync(long planId, long queryId, string? queryText) => QueryStorePlans.ShowAsync(_repo, _dialogs, planId, queryId, queryText);

    /// <summary>Any Query Store query ID, typed: Open details or Enter opens its window.</summary>
    [ObservableProperty]
    private string _queryIdText = string.Empty;

    [RelayCommand]
    private void OpenQueryId()
    {
        if (!long.TryParse(QueryIdText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var queryId) || queryId <= 0)
        {
            PeriodError = "Enter a query ID: a whole number such as 1234, from Query Store or an SSMS report.";
            return;
        }
        PeriodError = null;
        OpenQuery(queryId);
    }

    /// <summary>A row double-clicked: its query's window.</summary>
    [RelayCommand]
    private void OpenRow(object? row)
    {
        if (QueryStoreDetails.QueryOf(row) is { } q)
            OpenQuery(q.QueryId);
    }

    /// <summary>A window per query, so several can be open side by side; once the settings are read.</summary>
    public void OpenQuery(long queryId)
    {
        if (_storeStatus is not { IsSupported: true } status)
            return;
        _dialogs.ShowQueryStoreQuery(_repo, queryId, _period, status);
    }
}
