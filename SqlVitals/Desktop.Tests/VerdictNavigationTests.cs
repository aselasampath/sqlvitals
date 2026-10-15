using System.IO;
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
        var dir   = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
        var repo  = new FakeRepository();
        var model = new ProcessesViewModel(repo, repo, new ConnectionSettingsService(dir), _navigator, nameof(ProcessesView.Waiting));
        model.GoToSession += id => shown.Add($"session {id}");
        model.ChainsOnly  += () => shown.Add("chains");

        await Press(model, ActiveWaitsVerdict.Processes, id: 87);
        Assert.Equal(ProcessesView.Map, model.View);
        await Press(model, ProcessesVerdict.GoToHead, id: 51);
        await Press(model, ProcessesVerdict.ChainsOnly);

        Assert.Equal(["chains", "session 87", "session 51", "chains"], shown);
        Assert.Empty(_navigator.Moves);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Alerts_Investigate_OpensTheWorstAlertsEvidence()
    {
        var dir   = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
        var feed  = new FakeAlertFeed();
        var model = new AlertsViewModel(feed, new ConnectionSettingsService(dir), _navigator);
        var now   = DateTime.UtcNow;
        var conn  = Guid.NewGuid();
        Alert Cpu(HealthLevel level, int minutesAgo) =>
            new(Guid.NewGuid(), conn, HealthIndicator.Cpu, level, now.AddMinutes(-minutesAgo), null, 99, 90, "CPU at 99 %");
        var warning  = Cpu(HealthLevel.Warning, 30);
        var critical = Cpu(HealthLevel.Critical, 5);

        // Nothing read yet: nothing to investigate.
        await Press(model, AlertsVerdict.Investigate);
        Assert.Empty(_navigator.Moves);

        feed.Active.AddRange([warning, critical]);
        await model.RefreshAsync();
        await Press(model, AlertsVerdict.Investigate);
        await Press(model, AlertsVerdict.ActiveOnly);

        Assert.Equal((critical, ActivitySources.AlertsPage), Assert.Single(_navigator.Investigated));
        Assert.Equal(AlertsViewModel.ActiveOnlyIndex, model.StatusIndex);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Perfmon_Cause_OpensThePageOfTheCounterPastItsLimit()
    {
        static PerfmonCounter Counter(string name, long value) => new("SQLServer:Memory Manager", name, "", value, 65792, DateTime.Now);
        var model = new PerfmonViewModel(new FakeRepository
        {
            GetPerfmonCountersAsync = () => Task.FromResult<IEnumerable<PerfmonCounter>>(
                [Counter("Batch Requests/sec", 120), Counter("Memory Grants Pending", 3)]),
        }, null, _navigator);

        // General Throughput, the group it opens on: nothing past a limit.
        await model.RefreshAsync();
        await Press(model, PerfmonVerdict.Cause);
        Assert.Empty(_navigator.Moves);

        model.Group = "Memory Pressure";
        await model.RefreshAsync();
        await Press(model, PerfmonVerdict.Cause);
        Assert.Equal(["Memory"], _navigator.Moves);
    }

    // Waits, having read these top waits, and the same wait types' totals for the trend.
    private async Task<WaitsViewModel> Waits(IReadOnlyList<TopWaitType> top)
    {
        var model = new WaitsViewModel(new FakeRepository
        {
            GetTopWaitTypesAsync    = () => Task.FromResult<IEnumerable<TopWaitType>>(top),
            GetCumulativeWaitsAsync = () => Task.FromResult<IEnumerable<WaitStatCumulative>>(top.Select(w =>
                new WaitStatCumulative(w.WaitType, w.WaitCategory, 0, 10, w.WaitTimeMs, 0, 0, w.WaitTimeMs, 1, 1, 1, w.WaitTimeMs / 1000.0,
                                       DateTime.Now, DateTime.Now.AddDays(-3), "#000", ""))),
        }, null, _navigator);
        await model.RefreshAsync();
        return model;
    }

    [Fact]
    public async Task Waits_OpensThePageOfTheTopCategory_AndStartsAndChartsTheTrendOnThePage()
    {
        var model = await Waits([Wait("SOS_SCHEDULER_YIELD", "CPU", 9_000), Wait("PAGEIOLATCH_SH", "I/O", 4_000), Wait("WRITELOG", "I/O", 3_000)]);
        model.ClearAllCommand.Execute(null);
        Assert.True(model.IsPaused);

        await Press(model, TopWaitsVerdict.Cause);
        await Press(model, WaitsVerdict.StartTrend);
        await Press(model, WaitTrendVerdict.TopWaits);

        Assert.Equal(["ResourceQueries"], _navigator.Moves);
        Assert.False(model.IsPaused);
        Assert.Equal(3, model.AllWaitTypes.Count(i => i.IsChecked));
    }

    [Fact]
    public async Task Waits_Cause_NowhereForACategoryNoPageExplains()
    {
        var model = await Waits([Wait("ASYNC_NETWORK_IO", "Network", 9_000)]);

        await Press(model, TopWaitsVerdict.Cause);

        Assert.Empty(_navigator.Moves);
    }

    [Fact]
    public async Task QueryStore_OpensRegressions_AndAQueryInItsOwnWindow()
    {
        var dir     = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
        var dialogs = new FakeDialogService();
        var health  = new QueryStoreHealth("SalesDb", "READ_WRITE", "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, "AUTO", "AUTO", null,
                                           200, 1482, 1630, 0, DateTime.UtcNow.AddDays(-20), DateTime.Now);
        var repo    = new FakeRepository { GetQueryStoreStatusAsync = () => Task.FromResult(new QueryStoreStatus("SalesDb", health, QueryStoreFeatures.None)) };
        var model   = new QueryStoreViewModel(repo, new ConnectionSettingsService(dir), _navigator, dialogs, new FakeClipboard());
        await model.RefreshAsync();   // a query's window needs the settings read

        await Press(model, QueryStoreVerdict.OpenQuery, id: 42);
        await Press(model, QueryStoreVerdict.Regressions);

        Assert.Equal(["Query Store query 42"], dialogs.Shown);
        Assert.Equal(["QueryRegressions"], _navigator.Moves);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task QueryRegressions_OpenQuery_OpensQueryStoreOnIt()
    {
        var dir   = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
        var model = new QueryRegressionsViewModel(new FakeRepository(), null, new ConnectionSettingsService(dir), _navigator, new FakeDialogService());

        await Press(model, QueryRegressionsVerdict.OpenQuery, id: 42);
        await Press(model, QueryRegressionsVerdict.ShowPlans);   // nothing checked yet: nothing to show

        Assert.Equal(["QueryStore ⟵ Query Regressions: Query 42"], _navigator.Moves);
        Assert.Null(model.SelectedRegression);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ShowError_SaysWhatCouldNotBeRead_InTheTopBarsWords()
    {
        var model = new WaitsViewModel(new FakeRepository(), null, _navigator);
        var error = new TimeoutException("Execution Timeout Expired");

        model.ShowError("the wait stats", error);

        Assert.Equal(PageVerdict.Unreadable("the wait stats", ErrorStatus.Format(error)), model.Verdict);
    }

    private static TopWaitType Wait(string type, string category, long ms) =>
        new(1, type, category, 10, ms, ms, 0, ms, 0, ms / 1000.0, ms / 10.0, 0, 0, DateTime.UtcNow, "#000000", "1");
}
