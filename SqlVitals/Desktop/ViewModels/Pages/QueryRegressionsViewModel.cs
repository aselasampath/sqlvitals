using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Queries whose average duration or CPU rose past a threshold, recent period against a baseline
/// (#32, #187). Reads Query Store when the database has it on, the monitoring history otherwise.
/// Checked when the page is opened, on Check or Refresh, and when what is compared changes. The
/// verdict (#149) names the queries that got slower, the worst first.
/// </summary>
public sealed partial class QueryRegressionsViewModel : PageViewModel
{
    private enum Source { Auto, QueryStore, History }

    /// <summary>What one check found, and what to say about it.</summary>
    /// <param name="Compared">Both periods had queries, so the rows (or none) are a real comparison.</param>
    private sealed record CheckResult(IReadOnlyList<QueryRegression> Rows, bool FromQueryStore, string Status, string EmptyText, bool Compared = true);

    private readonly IQueryRegressionRepository _repo;
    private readonly ConnectionHistory?         _history;
    private readonly ConnectionSettingsService  _settings;
    private readonly INavigator                 _navigator;
    private readonly IDialogService             _dialogs;
    private readonly TimeProvider               _time;

    private RegressionCriteria _criteria;

    // A check has started: what is compared changing after it checks again.
    private bool _read;

    /// <param name="history">The connection's monitoring history; null for a connection that isn't saved.</param>
    /// <param name="settings">The criteria, read and saved.</param>
    public QueryRegressionsViewModel(IQueryRegressionRepository repo, ConnectionHistory? history, ConnectionSettingsService settings,
                                     INavigator navigator, IDialogService dialogs, TimeProvider? time = null)
    {
        _repo      = repo;
        _history   = history;
        _settings  = settings;
        _navigator = navigator;
        _dialogs   = dialogs;
        _time      = time ?? TimeProvider.System;

        RecentNames  = RegressionWindows.RecentChoices.Select(r => new RegressionWindows(r, RegressionBaselineKind.PreviousWeek).RecentLabel).ToList();
        _recentIndex = RegressionWindows.RecentChoices.ToList().IndexOf(RegressionWindows.Default.Recent);

        _criteria          = settings.Load().RegressionCriteria;
        _thresholdText     = _criteria.ThresholdPct.ToString("0.##", CultureInfo.CurrentCulture);
        _minExecutionsText = _criteria.MinExecutions.ToString("N0", CultureInfo.CurrentCulture);

        PropertyChanged += OnOwnPropertyChanged;
    }

    // Check waits while a check runs, as its button did.
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy))
            CheckCommand.NotifyCanExecuteChanged();
    }

    // ── What is compared ──────────────────────────────────────────────────

    /// <summary>The recent periods offered, ending now.</summary>
    public IReadOnlyList<string> RecentNames { get; }

    [ObservableProperty]
    private int _recentIndex;

    /// <summary>"The 7 days before", "The same time last week".</summary>
    [ObservableProperty]
    private int _baselineIndex;

    /// <summary>Auto, Query Store or Monitoring history.</summary>
    [ObservableProperty]
    private int _sourceIndex;

    // Other choices: checked again after a check in progress, which used the old ones (#169).
    partial void OnRecentIndexChanged(int value) => Recheck();
    partial void OnBaselineIndexChanged(int value) => Recheck();
    partial void OnSourceIndexChanged(int value) => Recheck();

    private void Recheck()
    {
        if (_read)
            _ = RereadAsync();
    }

    /// <summary>The boxes: the rise that flags a query, in percent, and the runs it needs in each period.</summary>
    [ObservableProperty]
    private string _thresholdText;

    [ObservableProperty]
    private string _minExecutionsText;

    /// <summary>Why the criteria typed can't be used; null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCriteriaError))]
    private string? _criteriaError;

    public bool ShowsCriteriaError => CriteriaError is not null;

    /// <summary>Folded, what is compared in one line (#149).</summary>
    [ObservableProperty]
    private string? _criteriaSummary;

    /// <summary>What was compared, and where from; "Checking: …" while it checks.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    /// <summary>Checks again with the choices and boxes as they are (or Enter in either box); the boxes are saved.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task Check() => RereadAsync();

    private bool CanCheck() => !IsBusy;

    private RegressionWindows SelectedWindows => new(
        RegressionWindows.RecentChoices[Math.Clamp(RecentIndex, 0, RegressionWindows.RecentChoices.Count - 1)],
        BaselineIndex == 1 ? RegressionBaselineKind.SameTimeLastWeek : RegressionBaselineKind.PreviousWeek);

    private Source SelectedSource => (Source)Math.Clamp(SourceIndex, 0, 2);

    // ── What was checked, for the top bar ─────────────────────────────────

    // The windows last checked; both boxes stay on the page.
    private RegressionWindows? _checked;

    /// <summary>The recent window last checked.</summary>
    public string? HistoryWindow => _checked?.RecentLabel;

    /// <summary>What it was compared with.</summary>
    public string? CompareWith => _checked?.BaselineLabel;

    /// <summary>When it was last checked, on this PC's clock.</summary>
    public DateTime? DataTo { get; private set; }

    /// <summary>The rows on screen came from Query Store, which reads the connection's database; and where the plans are looked for.</summary>
    public bool FromQueryStore { get; private set; }

    // ── The queries ───────────────────────────────────────────────────────

    /// <summary>The regressed queries, the costliest first.</summary>
    public GridRows<QueryRegression> Regressions { get; } = [];

    /// <summary>The query in the drawer; kept across a check by its key.</summary>
    [ObservableProperty]
    private QueryRegression? _selectedRegression;

    /// <summary>"Regressed queries", "1 regressed query", "The 50 costliest regressed queries".</summary>
    [ObservableProperty]
    private string _countText = "Regressed queries";

    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>A query to show: the verdict's Show its plans selected it. The view scrolls to it and opens the drawer.</summary>
    public event Action<QueryRegression>? ShowRow;

    // A week or more of runtime stats: its reads have a longer limit of their own.
    protected override TimeSpan? LongestQuery => ExtendedQueries;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        _read = true;
        if (!ReadCriteria())
            return;

        var windows  = SelectedWindows;
        var criteria = _criteria;
        ReadNote = $"Checking: {windows.Describe()}…";

        var result = await CheckAsync(windows, SelectedSource, criteria, cancel);
        cancel.ThrowIfCancellationRequested();

        Verdict = QueryRegressionsVerdict.From(result.Rows, result.Compared, windows.Describe(), result.EmptyText, criteria);
        CriteriaSummary = string.Format(CultureInfo.CurrentCulture,
            "{0} · {1} · more than {2:0.##} % slower, at least {3:N0} runs in each period",
            windows.Describe(), result.FromQueryStore ? "Query Store" : "monitoring history", criteria.ThresholdPct, criteria.MinExecutions);

        FromQueryStore = result.FromQueryStore;
        (_checked, DataTo) = (windows, _time.GetLocalNow().DateTime);
        ReportContextChanged();

        var kept = SelectedRegression?.QueryKey;
        Regressions.Replace(result.Rows);
        SelectedRegression = result.Rows.FirstOrDefault(r => r.QueryKey == kept);
        ReadNote  = result.Status;
        CountText = result.Rows.Count switch
        {
            0 => "Regressed queries",
            1 => "1 regressed query",
            var n when n >= QueryRegressionDetector.MaxResults => $"The {n:N0} costliest regressed queries",
            var n => $"{n:N0} regressed queries",
        };
        NoDataText  = result.EmptyText;
        ShowsNoData = result.Rows.Count == 0;
    }

    // The verdict says what failed; "Checking: …" would say it is still checking.
    protected override void ShowLoadError(Exception exception)
    {
        ReadNote = string.Empty;
        ShowError("the regression check", exception);
    }

    // Reads the two boxes, saving them when they changed. False, with the reason shown, when
    // they can't be used.
    private bool ReadCriteria()
    {
        if (!RegressionCriteria.TryParse(ThresholdText, MinExecutionsText, out var criteria, out var error))
        {
            CriteriaError = error;
            return false;
        }

        CriteriaError = null;
        if (criteria! != _criteria)
        {
            _criteria = criteria!;
            try
            {
                _settings.SetRegressionSettings(criteria!);
            }
            catch (Exception ex)
            {
                // The check still runs with what was typed; it just won't be remembered.
                AppLog.Error("QueryRegressions: saving the settings failed", ex);
            }
        }
        return true;
    }

    private async Task<CheckResult> CheckAsync(RegressionWindows windows, Source source, RegressionCriteria criteria, CancellationToken cancel)
    {
        QueryStoreRegressionStats? queryStore = null;
        if (source != Source.History)
        {
            queryStore = await _repo.GetQueryStoreRegressionStatsAsync(windows, cancel);
            if (queryStore.IsAvailable)
                return await FromQueryStoreAsync(queryStore, windows, criteria, cancel);

            if (source == Source.QueryStore)
                return new CheckResult([], true, windows.Describe(),
                    $"{Capitalize(QueryStoreMissing(queryStore))}. Choose Auto or Monitoring history to check SqlVitals' monitoring history instead.",
                    Compared: false);
        }

        return await FromHistoryAsync(windows, criteria, queryStore, cancel);
    }

    private async Task<CheckResult> FromQueryStoreAsync(
        QueryStoreRegressionStats queryStore, RegressionWindows windows, RegressionCriteria criteria, CancellationToken cancel)
    {
        var found = await Task.Run(() => QueryRegressionDetector.Detect(queryStore.Stats, criteria), cancel);

        var texts = await _repo.GetQueryStoreTextsAsync(found.Select(QueryId).ToList(), cancel);
        var rows  = found.Select(r => r with
        {
            QueryText    = texts.GetValueOrDefault(QueryId(r)),
            DatabaseName = queryStore.DatabaseName,
        }).ToList();

        var (hasRecent, hasBaseline) = (queryStore.Stats.Any(s => s.IsRecent), queryStore.Stats.Any(s => !s.IsRecent));
        return new CheckResult(rows, true,
            $"{windows.Describe()} · Query Store for {queryStore.DatabaseName} · periods on the server's clock, " +
            "in whole Query Store intervals",
            EmptyText("Query Store", windows, criteria, hasRecent, hasBaseline), hasRecent && hasBaseline);
    }

    private async Task<CheckResult> FromHistoryAsync(
        RegressionWindows windows, RegressionCriteria criteria, QueryStoreRegressionStats? queryStore, CancellationToken cancel)
    {
        var why = queryStore is null ? string.Empty : $", as {QueryStoreMissing(queryStore)}";

        if (_history is null)
            return new CheckResult([], false, windows.Describe(),
                (queryStore is null ? string.Empty : $"{Capitalize(QueryStoreMissing(queryStore))}. ") +
                ConnectionHistory.UnavailableReason, Compared: false);

        var (reader, connectionId) = (_history.Reader, _history.ConnectionId);
        var (recentFrom, recentTo, baselineFrom, baselineTo) = windows.Resolve(_time.GetUtcNow().UtcDateTime);

        // History reads open the file, so they stay off the UI thread like the trend pages' do.
        var (rows, hasRecent, hasBaseline) = await Task.Run(() =>
        {
            var recent   = reader.ReadQueryTotals(connectionId, recentFrom, recentTo);
            var baseline = reader.ReadQueryTotals(connectionId, baselineFrom, baselineTo);
            var found    = QueryRegressionDetector.Detect(QueryRegressionDetector.FromHistory(baseline, recent), criteria);
            var texts    = reader.ReadQueryTexts(connectionId, found.Select(r => r.QueryKey).ToList());

            IReadOnlyList<QueryRegression> withText = found
                .Select(r => texts.TryGetValue(r.QueryKey, out var t)
                    ? r with { QueryText = t.QueryText, DatabaseName = t.DatabaseName }
                    : r)
                .ToList();
            return (withText, recent.Count > 0, baseline.Count > 0);
        }, cancel);

        return new CheckResult(rows, false,
            $"{windows.Describe()} · from the monitoring history{why} · periods on this PC's clock · " +
            $"the server's top {HistoryRecorder.TopQueries} queries by CPU at each snapshot",
            EmptyText("The monitoring history", windows, criteria, hasRecent, hasBaseline), hasRecent && hasBaseline);
    }

    private static string EmptyText(string source, RegressionWindows windows, RegressionCriteria criteria,
                                    bool hasRecent, bool hasBaseline)
    {
        var culture = CultureInfo.CurrentCulture;
        if (!hasRecent)
            return $"{source} has no queries for the {windows.RecentLabel.ToLower(culture)}.";
        if (!hasBaseline)
            return $"{source} has none of these queries in {windows.BaselineLabel}, so there is nothing to compare them with yet.";
        return string.Format(culture,
            "No query got more than {0:0.##} % slower in average duration or CPU (with at least {1:N0} executions in each period).",
            criteria.ThresholdPct, criteria.MinExecutions);
    }

    // "Query Store is off for Sales" or "this SQL Server has no Query Store".
    private static string QueryStoreMissing(QueryStoreRegressionStats queryStore) =>
        queryStore.State == "NOT SUPPORTED"
            ? "this SQL Server has no Query Store (it came with SQL Server 2016)"
            : $"Query Store is {queryStore.State.Replace('_', ' ').ToLowerInvariant()} for {queryStore.DatabaseName}";

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static long QueryId(QueryRegression r) => long.Parse(r.QueryKey, CultureInfo.InvariantCulture);

    // ── The verdict, the query link and the plans ─────────────────────────

    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case QueryRegressionsVerdict.ShowPlans: ShowWorst(); break;
            case QueryRegressionsVerdict.OpenQuery when action.Id is { } id:
                return _navigator.GoToAsync("QueryStore", new FocusFilter(FocusKind.Query, id), "Query Regressions");
        }
        return Task.CompletedTask;
    }

    // The verdict's Show its plans: the worst query selected, in view, and in the drawer.
    private void ShowWorst()
    {
        if (Regressions.OrderByDescending(r => r.ExtraTimeMs).FirstOrDefault() is not { } worst)
            return;
        SelectedRegression = worst;
        ShowRow?.Invoke(worst);
    }

    /// <summary>A query_id opens Query Store on that query (#145).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenQuery(QueryRegression? row) =>
        row?.QueryStoreId is { } id
            ? ReportErrorsOf(_navigator.GoToAsync("QueryStore", new FocusFilter(FocusKind.Query, id), "Query Regressions"))
            : Task.CompletedTask;

    /// <summary>A plan the drawer offers for a query: its heading, which one, and the window's title.</summary>
    public sealed record PlanChoice(string Heading, bool Baseline, string WindowTitle);

    /// <summary>The plans of a query: now and, when it differs, before (Query Store); or the plan cache's now.</summary>
    public IReadOnlyList<PlanChoice> PlansOf(QueryRegression r)
    {
        if (!FromQueryStore)
            return [new("Plan in the plan cache now", false, $"Execution plan in the plan cache now · query {r.QueryKey}")];
        List<PlanChoice> plans = [];
        if (r.RecentPlanId is { } now)
            plans.Add(new($"Plan used most now (plan {now})", false, $"Execution plan used most now (Query Store plan {now}) · query {r.QueryKey}"));
        if (r.PlanDiffers && r.BaselinePlanId is { } before)
            plans.Add(new($"Plan used most before (plan {before})", true, $"Execution plan used most before (Query Store plan {before}) · query {r.QueryKey}"));
        return plans;
    }

    /// <summary>A query's plan: used most now or before from Query Store, or the plan cache's now; null when there is none.</summary>
    public async Task<string?> PlanOfAsync(QueryRegression r, bool baseline, CancellationToken cancel = default)
    {
        if (!FromQueryStore)
            return await _repo.GetCachedPlanForQueryHashAsync(r.QueryKey, cancel);
        return (baseline ? r.BaselinePlanId : r.RecentPlanId) is { } id ? await _repo.GetQueryStorePlanAsync(id, cancel) : null;
    }

    /// <summary>The plan used most now, in a window.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ViewRecentPlan(QueryRegression? row) => ViewPlanAsync(row, baseline: false);

    /// <summary>The plan used most before, in a window.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ViewBaselinePlan(QueryRegression? row) => ViewPlanAsync(row, baseline: true);

    // Cancels the plans still being read: the page left.
    private Action? _cancelPlans;

    // A plan still being read when the page is left is dropped, not opened over another page.
    private async Task ViewPlanAsync(QueryRegression? r, bool baseline)
    {
        if (r is null)
            return;
        using var read = new CancellationTokenSource();
        Action cancel = read.Cancel;
        _cancelPlans += cancel;
        try
        {
            var planXml = await PlanOfAsync(r, baseline, read.Token);
            var which   = FromQueryStore
                ? $"{(baseline ? "used most before" : "used most now")} (Query Store plan {(baseline ? r.BaselinePlanId : r.RecentPlanId)})"
                : "in the plan cache now";
            if (!read.IsCancellationRequested)
                _dialogs.ShowQueryPlan(planXml, r.QueryText, $"Execution plan {which} · query {r.QueryKey}");
        }
        catch (Exception) when (read.IsCancellationRequested)
        {
            // Left: nothing to show.
        }
        catch (Exception ex)
        {
            AppLog.Error("Query Regressions: reading a plan failed", ex);
            _dialogs.Warn($"Failed to retrieve execution plan:\n{ConnectionSettingsService.RedactSecrets(ex.Message)}", "Plan Error");
        }
        finally
        {
            _cancelPlans -= cancel;
        }
    }

    protected override void OnLeaving()
    {
        _cancelPlans?.Invoke();
        _cancelPlans = null;
    }
}
