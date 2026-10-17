using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore.Defaults;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.ViewModels.Windows;

/// <summary>
/// One Query Store query (#111, SSMS's Tracked Queries; #191): its full text, a metric per interval
/// with one line per plan and the forced plan marked, and its plans. <c>QueryStoreQueryWindow</c>
/// shows it, opened by <see cref="IDialogService.ShowQueryStoreQuery"/>; several can be open at once.
/// </summary>
public sealed partial class QueryStoreQueryViewModel : ObservableObject
{
    private readonly IWaitStatsRepository _repo;
    private readonly long _queryId;
    private readonly QueryStorePeriod _period;
    private readonly QueryStoreStatus _store;
    private readonly IDialogService _dialogs;
    private readonly IClipboard _clipboard;
    private readonly TimeProvider _clock;

    private QueryStoreQueryDetails? _details;

    public QueryStoreQueryViewModel(IWaitStatsRepository repo, long queryId, QueryStorePeriod period, QueryStoreStatus status,
                                    IDialogService dialogs, IClipboard clipboard, TimeProvider? clock = null)
    {
        _repo      = repo;
        _queryId   = queryId;
        _period    = period;
        _store     = status;
        _dialogs   = dialogs;
        _clipboard = clipboard;
        _clock     = clock ?? TimeProvider.System;

        Metrics = QueryStoreMetrics.Available(status.Features.Sql2017Metrics)
            .Select(m => new Choice<QueryStoreMetric>(m.Metric, m.Metric == QueryStoreMetric.Executions ? "Executions" : $"Avg {m.NameWithUnit}"))
            .ToList();
        _metric = QueryStoreMetric.Duration;
    }

    /// <summary>The window's title: "Query 42 · Query Store · SalesDb".</summary>
    public string Title => $"Query {_queryId} · Query Store · {_store.DatabaseName}";

    /// <summary>"Query 42".</summary>
    public string Heading => $"Query {_queryId}";

    /// <summary>Its object, database and period; or that Query Store has no such query.</summary>
    [ObservableProperty]
    private string _subheading = string.Empty;

    /// <summary>The whole statement.</summary>
    [ObservableProperty]
    private string _queryText = string.Empty;

    /// <summary>Loading, how many plans it used, or what went wrong.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The metrics the chart can show: those this server's Query Store keeps.</summary>
    public IReadOnlyList<Choice<QueryStoreMetric>> Metrics { get; }

    /// <summary>The metric charted: its average per execution in each interval.</summary>
    [ObservableProperty]
    private QueryStoreMetric _metric;

    partial void OnMetricChanged(QueryStoreMetric value) => ShowChart();

    /// <summary>The plans used in the period, and any forced plan, each with its View Plan.</summary>
    [ObservableProperty]
    private IReadOnlyList<QueryStorePlanRow> _plans = [];

    /// <summary>One line per plan, one point per interval; the forced plan's line emphasised.</summary>
    [ObservableProperty]
    private TrendChart _chart = TrendChart.Empty;

    /// <summary>Over the chart when the query didn't run in the period; null otherwise.</summary>
    [ObservableProperty]
    private string? _noData;

    /// <summary>Reads the query's text, intervals and plans; a failure goes to <see cref="Status"/> and the log.</summary>
    [ReportsItsErrors]
    public async Task LoadAsync()
    {
        Status = "Loading…";
        try
        {
            _details = await _repo.GetQueryStoreQueryDetailsAsync(_queryId, _period);
        }
        catch (Exception ex)
        {
            AppLog.Error($"QueryStore: reading query {_queryId} failed", ex);
            Status = ErrorStatus.Format(ex);
            return;
        }
        // Copy follows whether there is a query, whatever its text.
        CopyTextCommand.NotifyCanExecuteChanged();

        if (_details.Query is not { } query)
        {
            Subheading = $"Query Store for {_store.DatabaseName} has no query {_queryId}. It may have been cleaned up, or the ID is from another database.";
            Status     = string.Empty;
            return;
        }

        Subheading = $"{query.ObjectText} · {_store.DatabaseName} · {_period.Label}";
        QueryText  = query.QueryText.Trim();
        Plans      = _details.Summaries.Select(plan => new QueryStorePlanRow(plan, ViewPlanAsync)).ToList();
        var plans = _details.Intervals.Select(i => i.PlanId).Distinct().Count();
        Status = $"{plans:N0} {(plans == 1 ? "plan" : "plans")} used in {_period.Label.ToLower(CultureInfo.CurrentCulture)} · one point per Query Store interval";
        ShowChart();
    }

    // One line per plan: the metric's average per execution in each interval. The forced plan's
    // line is thicker, and "(forced)" in the legend.
    private void ShowChart()
    {
        if (_details is not { Query: not null } details)
            return;

        if (details.Intervals.Count == 0)
        {
            Chart  = TrendChart.Empty;
            NoData = $"The query didn't run in {_period.Label.ToLower(CultureInfo.CurrentCulture)}.";
            return;
        }
        NoData = null;

        var metric = Metric;
        var info   = QueryStoreMetrics.Info(metric);
        var forced = details.Plans.Where(p => p.IsForced).Select(p => p.PlanId).ToHashSet();
        var lines  = details.Intervals
            .GroupBy(i => i.PlanId)
            .OrderBy(g => g.Key)
            .Select((g, index) =>
            {
                var isForced = forced.Contains(g.Key);
                var points   = g.OrderBy(i => i.StartUtc).Select(i => new DateTimePoint(i.StartUtc.ToLocalTime(), i.Totals.Average(metric)));
                return new TrendLine(isForced ? $"Plan {g.Key} (forced)" : $"Plan {g.Key}", new ObservableCollection<DateTimePoint>(points), index, isForced);
            })
            .ToList();

        var (fromUtc, toUtc) = _period.Resolve(_clock.GetUtcNow().UtcDateTime);
        var first = details.Intervals.Min(i => i.StartUtc);
        Chart = new TrendChart(lines, [],
                               (fromUtc < first ? fromUtc : first).ToLocalTime(), toUtc.ToLocalTime(),
                               ConnectionHistory.AxisFormat(toUtc - fromUtc),
                               metric == QueryStoreMetric.Executions ? info.Name : $"Avg {info.NameWithUnit}");
    }

    [RelayCommand(CanExecute = nameof(HasText))]
    private void CopyText() => _clipboard.SetText(_details!.Query!.QueryText);

    private bool HasText() => _details?.Query is not null;

    // A plan's View Plan: drawn in the plan window; a failure says why.
    private Task ViewPlanAsync(long planId) => QueryStorePlans.ShowAsync(_repo, _dialogs, planId, _queryId, _details?.Query?.QueryText);
}
