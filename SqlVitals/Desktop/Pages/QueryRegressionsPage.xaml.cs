using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Queries whose average duration or CPU rose past a threshold, recent period against a baseline
/// (#32). Reads Query Store when the database has it on, the monitoring history otherwise.
/// </summary>
public partial class QueryRegressionsPage : Page, IRefreshable
{
    private enum Source { Auto, QueryStore, History }

    /// <summary>What one check found, and what to say about it.</summary>
    private sealed record CheckResult(IReadOnlyList<QueryRegression> Rows, bool FromQueryStore, string Status, string EmptyText);

    private readonly IWaitStatsRepository      _repo;
    private readonly ConnectionHistory?        _history;
    private readonly ConnectionSettingsService _settings;

    private RegressionCriteria _criteria;

    // Set once the choices are filled in, so filling them doesn't start a check.
    private readonly bool _ready;

    // Bumped per check; a slower, earlier check's result is dropped.
    private int _loadVersion;

    // Where the rows on screen came from, which decides where the plan buttons look.
    private bool _shownFromQueryStore;

    public QueryRegressionsPage(IWaitStatsRepository repo, ConnectionHistory? history, ConnectionSettingsService settings)
    {
        _repo     = repo;
        _history  = history;
        _settings = settings;
        InitializeComponent();

        foreach (var recent in RegressionWindows.RecentChoices)
            CmbRecent.Items.Add(new ComboBoxItem
            {
                Content = new RegressionWindows(recent, RegressionBaselineKind.PreviousWeek).RecentLabel,
                Tag     = recent,
            });
        CmbRecent.SelectedIndex = RegressionWindows.RecentChoices.ToList().IndexOf(RegressionWindows.Default.Recent);

        _criteria = settings.Load().RegressionCriteria;
        TxtThreshold.Text     = _criteria.ThresholdPct.ToString("0.##", CultureInfo.CurrentCulture);
        TxtMinExecutions.Text = _criteria.MinExecutions.ToString("N0", CultureInfo.CurrentCulture);

        _ready = true;
    }

    private RegressionWindows SelectedWindows => new(
        (TimeSpan)((ComboBoxItem)CmbRecent.SelectedItem).Tag,
        CmbBaseline.SelectedIndex == 1 ? RegressionBaselineKind.SameTimeLastWeek : RegressionBaselineKind.PreviousWeek);

    private Source SelectedSource => (Source)Math.Max(0, CmbSource.SelectedIndex);

    public async Task RefreshAsync()
    {
        if (!ReadCriteria())
            return;

        var version  = ++_loadVersion;
        var windows  = SelectedWindows;
        var criteria = _criteria;

        BtnCheck.IsEnabled = false;
        TxtStatus.Text     = $"Checking: {windows.Describe()}…";
        try
        {
            var result = await CheckAsync(windows, SelectedSource, criteria);
            if (version != _loadVersion)
                return;

            _shownFromQueryStore = result.FromQueryStore;
            DataGridRefresh.SetItemsSource(RegressionsGrid, result.Rows);
            TxtStatus.Text = result.Status;
            TxtCount.Text  = result.Rows.Count switch
            {
                0 => "Regressed queries",
                1 => "1 regressed query",
                var n when n >= QueryRegressionDetector.MaxResults => $"The {n:N0} costliest regressed queries",
                var n => $"{n:N0} regressed queries",
            };
            TxtNoData.Text       = result.EmptyText;
            TxtNoData.Visibility = result.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            if (version == _loadVersion)
                BtnCheck.IsEnabled = true;
        }
    }

    // Reads the two boxes, saving them when they changed. False, with the reason shown, when
    // they can't be used.
    private bool ReadCriteria()
    {
        if (!RegressionCriteria.TryParse(TxtThreshold.Text, TxtMinExecutions.Text, out var criteria, out var error))
        {
            TxtError.Text       = error;
            TxtError.Visibility = Visibility.Visible;
            return false;
        }

        TxtError.Visibility = Visibility.Collapsed;
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

    private async Task<CheckResult> CheckAsync(RegressionWindows windows, Source source, RegressionCriteria criteria)
    {
        QueryStoreRegressionStats? queryStore = null;
        if (source != Source.History)
        {
            queryStore = await _repo.GetQueryStoreRegressionStatsAsync(windows);
            if (queryStore.IsAvailable)
                return await FromQueryStoreAsync(queryStore, windows, criteria);

            if (source == Source.QueryStore)
                return new CheckResult([], true, windows.Describe(),
                    $"{Capitalize(QueryStoreMissing(queryStore))}. Choose Auto or Monitoring history to check SqlVitals' monitoring history instead.");
        }

        return await FromHistoryAsync(windows, criteria, queryStore);
    }

    private async Task<CheckResult> FromQueryStoreAsync(
        QueryStoreRegressionStats queryStore, RegressionWindows windows, RegressionCriteria criteria)
    {
        var found = await Task.Run(() => QueryRegressionDetector.Detect(queryStore.Stats, criteria));

        var texts = await _repo.GetQueryStoreTextsAsync(found.Select(r => QueryId(r)).ToList());
        var rows  = found.Select(r => r with
        {
            QueryText    = texts.GetValueOrDefault(QueryId(r)),
            DatabaseName = queryStore.DatabaseName,
        }).ToList();

        return new CheckResult(rows, true,
            $"{windows.Describe()} · Query Store for {queryStore.DatabaseName} · periods on the server's clock, " +
            "in whole Query Store intervals",
            EmptyText("Query Store", windows, criteria,
                      queryStore.Stats.Any(s => s.IsRecent), queryStore.Stats.Any(s => !s.IsRecent)));
    }

    private async Task<CheckResult> FromHistoryAsync(
        RegressionWindows windows, RegressionCriteria criteria, QueryStoreRegressionStats? queryStore)
    {
        var why = queryStore is null ? string.Empty : $", as {QueryStoreMissing(queryStore)}";

        if (_history is null)
            return new CheckResult([], false, windows.Describe(),
                (queryStore is null ? string.Empty : $"{Capitalize(QueryStoreMissing(queryStore))}. ") +
                ConnectionHistory.UnavailableReason);

        var (reader, connectionId) = (_history.Reader, _history.ConnectionId);
        var (recentFrom, recentTo, baselineFrom, baselineTo) = windows.Resolve(DateTime.UtcNow);

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
        });

        return new CheckResult(rows, false,
            $"{windows.Describe()} · from the monitoring history{why} · periods on this PC's clock · " +
            $"the server's top {HistoryRecorder.TopQueries} queries by CPU at each snapshot",
            EmptyText("The monitoring history", windows, criteria, hasRecent, hasBaseline));
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

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private async void Choice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && IsLoaded)
            await CheckFromToolbarAsync();
    }

    private async void Criteria_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await CheckFromToolbarAsync();
    }

    private async void BtnCheck_Click(object sender, RoutedEventArgs e) => await CheckFromToolbarAsync();

    // The sidebar Refresh reports errors through MainWindow; a check started here reports its own.
    private async Task CheckFromToolbarAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("QueryRegressions: check failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    // ── Execution plans ───────────────────────────────────────────────────────

    private async void ViewRecentPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryRegression r } btn)
            await ShowPlanAsync(btn, r, baseline: false);
    }

    private async void ViewBaselinePlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryRegression r } btn)
            await ShowPlanAsync(btn, r, baseline: true);
    }

    private async Task ShowPlanAsync(Button btn, QueryRegression r, bool baseline)
    {
        var label = btn.Content;
        btn.IsEnabled = false;
        btn.Content   = "…";
        try
        {
            string? planXml;
            string  which;
            if (_shownFromQueryStore)
            {
                var planId = baseline ? r.BaselinePlanId : r.RecentPlanId;
                planXml = planId is { } id ? await _repo.GetQueryStorePlanAsync(id) : null;
                which   = $"{(baseline ? "used most before" : "used most now")} (Query Store plan {planId})";
            }
            else
            {
                planXml = await _repo.GetCachedPlanForQueryHashAsync(r.QueryKey);
                which   = "in the plan cache now";
            }

            new QueryExecutionPlanWindow(planXml, r.QueryText)
            {
                Owner = Window.GetWindow(this),
                Title = $"Execution plan {which} · query {r.QueryKey}",
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
}
