using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The verdict actions that open another page (#182): through <see cref="INavigator"/>, so a view
/// model is tested without a window. Actions that stay on the page still call the page.
/// </summary>
public sealed class VerdictNavigationTests
{
    private readonly FakeNavigator _navigator = new();

    private static Task Press(PageViewModel model, string key, long? id = null) =>
        model.ActCommand.ExecuteAsync(new VerdictAction(key, "pressed", id));

    [Fact]
    public async Task Processes_Waiting_ShowsTheChainOnTheMap_WithoutLeavingThePage()
    {
        // #178: the Waiting view is part of Processes, so the chain is on the same page.
        var shown = new List<string>();
        var model = new ProcessesViewModel(goToSession: id => shown.Add($"session {id}"), chainsOnly: () => shown.Add("chains"),
                                           chainOnMap: id => shown.Add($"chain of {id}"));

        await Press(model, ActiveWaitsVerdict.Processes, id: 87);
        await Press(model, ProcessesVerdict.GoToHead, id: 51);
        await Press(model, ProcessesVerdict.ChainsOnly);

        Assert.Equal(["chain of 87", "session 51", "chains"], shown);
        Assert.Empty(_navigator.Moves);
    }

    [Fact]
    public async Task Alerts_Investigate_OpensTheWorstAlertsEvidence()
    {
        var activeOnly = 0;
        var model = new AlertsViewModel(_navigator, () => activeOnly++);
        var now   = DateTime.UtcNow;
        var conn  = Guid.NewGuid();
        Alert Cpu(HealthLevel level, int minutesAgo) =>
            new(Guid.NewGuid(), conn, HealthIndicator.Cpu, level, now.AddMinutes(-minutesAgo), null, 99, 90, "CPU at 99 %");
        var warning  = Cpu(HealthLevel.Warning, 30);
        var critical = Cpu(HealthLevel.Critical, 5);

        // Nothing read yet: nothing to investigate.
        await Press(model, AlertsVerdict.Investigate);
        Assert.Empty(_navigator.Moves);

        model.Show([new(warning, "Hot"), new(critical, "Hot")], "in the last 7 days", now, monitored: 1, notMonitored: []);
        await Press(model, AlertsVerdict.Investigate);
        await Press(model, AlertsVerdict.ActiveOnly);

        Assert.Equal((critical, ActivitySources.AlertsPage), Assert.Single(_navigator.Investigated));
        Assert.Equal(1, activeOnly);
    }

    [Fact]
    public async Task Perfmon_Cause_OpensThePageOfTheCounterPastItsLimit()
    {
        var model = new PerfmonViewModel(_navigator);

        model.Show([new PerfmonSeries("Page life expectancy", [300, 310])], WaitTrendState.Live, noData: null);
        await Press(model, PerfmonVerdict.Cause);
        Assert.Empty(_navigator.Moves);

        model.Show([new PerfmonSeries("Memory Grants Pending", [0, 3])], WaitTrendState.Live, noData: null);
        await Press(model, PerfmonVerdict.Cause);
        Assert.Equal(["Memory"], _navigator.Moves);
    }

    private WaitsViewModel Waits(List<string>? done = null) =>
        new(_navigator, startTrend: () => done?.Add("start"), chartTopWaits: () => done?.Add("top 20"));

    private static void ShowTop(WaitsViewModel model, IReadOnlyList<TopWaitType> top) =>
        model.Show(top, during: null, [], WaitTrendState.NotStarted, "ms/s", "", noData: null);

    [Fact]
    public async Task Waits_OpensThePageOfTheTopCategory_AndStartsAndChartsTheTrendOnThePage()
    {
        var done  = new List<string>();
        var model = Waits(done);
        ShowTop(model, [Wait("SOS_SCHEDULER_YIELD", "CPU", 9_000), Wait("PAGEIOLATCH_SH", "I/O", 4_000), Wait("WRITELOG", "I/O", 3_000)]);

        await Press(model, TopWaitsVerdict.Cause);
        await Press(model, WaitsVerdict.StartTrend);
        await Press(model, WaitTrendVerdict.TopWaits);

        Assert.Equal(["ResourceQueries"], _navigator.Moves);
        Assert.Equal(["start", "top 20"], done);
    }

    [Fact]
    public async Task Waits_Cause_NowhereForACategoryNoPageExplains()
    {
        var model = Waits();
        ShowTop(model, [Wait("ASYNC_NETWORK_IO", "Network", 9_000)]);

        await Press(model, TopWaitsVerdict.Cause);

        Assert.Empty(_navigator.Moves);
    }

    [Fact]
    public async Task QueryStore_OpensRegressions_AndAQueryInItsOwnWindow()
    {
        var opened = new List<long>();
        var model  = new QueryStoreViewModel(openQuery: opened.Add, _navigator);

        await Press(model, QueryStoreVerdict.OpenQuery, id: 42);
        await Press(model, QueryStoreVerdict.Regressions);

        Assert.Equal([42L], opened);
        Assert.Equal(["QueryRegressions"], _navigator.Moves);
    }

    [Fact]
    public async Task QueryRegressions_OpenQuery_OpensQueryStoreOnIt()
    {
        var plans = 0;
        var model = new QueryRegressionsViewModel(showPlans: () => plans++, _navigator);

        await Press(model, QueryRegressionsVerdict.OpenQuery, id: 42);
        await Press(model, QueryRegressionsVerdict.ShowPlans);

        Assert.Equal(["QueryStore ⟵ Query Regressions: Query 42"], _navigator.Moves);
        Assert.Equal(1, plans);
    }

    [Fact]
    public void ShowError_SaysWhatCouldNotBeRead_InTheTopBarsWords()
    {
        var model = Waits();
        var error = new TimeoutException("Execution Timeout Expired");

        model.ShowError("the wait stats", error);

        Assert.Equal(PageVerdict.Unreadable("the wait stats", ErrorStatus.Format(error)), model.Verdict);
    }

    private static TopWaitType Wait(string type, string category, long ms) =>
        new(1, type, category, 10, ms, ms, 0, ms, 0, ms / 1000.0, ms / 10.0, 0, 0, DateTime.UtcNow, "#000000", "1");
}
