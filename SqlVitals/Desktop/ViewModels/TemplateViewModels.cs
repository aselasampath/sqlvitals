using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

// The view models of the pages the template rolled out to (#149): each carries its page's verdict
// and what the verdict's actions do. The pages' grids and code-behind stay as they were.

/// <summary>Active Waits' verdict: the requests blocked, or what they wait on.</summary>
public sealed class ActiveWaitsViewModel(Action<int> openProcesses) : PageViewModel
{
    public void Show(IReadOnlyList<ActiveWait> waits, HealthThreshold? blocking) => Verdict = ActiveWaitsVerdict.From(waits, blocking);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == ActiveWaitsVerdict.Processes)
            openProcesses((int)(action.Id ?? 0));
    }
}

/// <summary>Deadlocks' verdict: how many in the range, and the object fought over most.</summary>
public sealed class DeadlocksViewModel(Action openNewest, Action showRecurring, Action showAll) : PageViewModel
{
    public void Show(DeadlockHistory history, IReadOnlyList<DeadlockReport> inRange, IReadOnlyList<DeadlockPattern> patterns, string during) =>
        Verdict = DeadlocksVerdict.From(history, inRange, patterns, during);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case DeadlocksVerdict.OpenNewest: openNewest();    break;
            case DeadlocksVerdict.Recurring:  showRecurring(); break;
            case DeadlocksVerdict.ShowAll:    showAll();       break;
        }
    }
}

/// <summary>App Connections' verdict: who holds the connections; it has no actions.</summary>
public sealed class AppConnectionsViewModel : PageViewModel
{
    public void Show(IReadOnlyList<ApplicationConnection> apps) => Verdict = AppConnectionsVerdict.From(apps);

    protected override void OnAction(VerdictAction action) { }
}

/// <summary>Top Waits' verdict: the top wait category and what it means.</summary>
public sealed class TopWaitsViewModel(Action<string> openPage) : PageViewModel
{
    private string _category = string.Empty;

    public void Show(IReadOnlyList<TopWaitType> waits)
    {
        Verdict   = TopWaitsVerdict.From(waits);
        _category = waits.Count == 0 ? string.Empty
            : waits.GroupBy(w => w.WaitCategory).OrderByDescending(g => g.Sum(w => w.WaitTimeMs)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case TopWaitsVerdict.Trend: openPage("WaitTrend"); break;
            case TopWaitsVerdict.Cause when TopWaitsVerdict.CausePage(_category) is { } page: openPage(page.Tag); break;
        }
    }
}

/// <summary>Wait Trend's verdict: the wait type that waits the most on the chart.</summary>
public sealed class WaitTrendViewModel(Action openTopWaits) : PageViewModel
{
    public void Show(IReadOnlyList<WaitTrendSeries> series, WaitTrendState state, string unit, string over, string? noData) =>
        Verdict = WaitTrendVerdict.From(series, state, unit, over, noData);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == WaitTrendVerdict.TopWaits)
            openTopWaits();
    }
}

/// <summary>Resource Queries' verdict: the top query by CPU, and by reads.</summary>
public sealed class ResourceQueriesViewModel(Action showCpu, Action showReads) : PageViewModel
{
    public void Show(IReadOnlyList<ResourceIntensiveQuery> byCpu, IReadOnlyList<ResourceIntensiveQuery> byReads) =>
        Verdict = ResourceQueriesVerdict.From(byCpu, byReads);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ResourceQueriesVerdict.ShowCpu:   showCpu();   break;
            case ResourceQueriesVerdict.ShowReads: showReads(); break;
        }
    }
}

/// <summary>Query Regressions' verdict: the queries that got slower, the worst first.</summary>
public sealed class QueryRegressionsViewModel(Action showPlans, Action<long> openQuery) : PageViewModel
{
    public void Show(IReadOnlyList<QueryRegression> rows, bool compared, string comparison, string? notCompared, RegressionCriteria criteria) =>
        Verdict = QueryRegressionsVerdict.From(rows, compared, comparison, notCompared, criteria);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case QueryRegressionsVerdict.ShowPlans:                       showPlans();   break;
            case QueryRegressionsVerdict.OpenQuery when action.Id is { } id: openQuery(id); break;
        }
    }
}

/// <summary>Plan Cache Health's verdict: the worst of what it checks.</summary>
public sealed class PlanCacheHealthViewModel(Action<int> showTab) : PageViewModel
{
    public void Show(PlanCacheHealthSummary summary, int keyLookups) => Verdict = PlanCacheHealthVerdict.From(summary, keyLookups);

    protected override void OnAction(VerdictAction action)
    {
        if (action is { Key: PlanCacheHealthVerdict.ShowTab, Id: { } tab })
            showTab((int)tab);
    }
}

/// <summary>Implicit Conversions' verdict: how many plans convert, the most used first.</summary>
public sealed class ImplicitConversionsViewModel(Action showWorst) : PageViewModel
{
    public void Show(IReadOnlyList<ImplicitConversion> rows) => Verdict = ImplicitConversionsVerdict.From(rows);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == ImplicitConversionsVerdict.ShowWorst)
            showWorst();
    }
}

/// <summary>SP Trace's verdict: what the capture is doing.</summary>
public sealed class SpTraceViewModel(Action start, Action stop, Action showSlowest) : PageViewModel
{
    public void Show(TraceSessionStatus status, IReadOnlyList<SpTraceEvent> calls, int procedures, int minDurationMs) =>
        Verdict = SpTraceVerdict.From(status, calls, procedures, minDurationMs);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case SpTraceVerdict.Start:       start();       break;
            case SpTraceVerdict.Stop:        stop();        break;
            case SpTraceVerdict.ShowSlowest: showSlowest(); break;
        }
    }
}
