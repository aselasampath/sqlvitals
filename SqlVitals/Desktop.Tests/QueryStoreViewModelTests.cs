using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Query Store's view model on its own (#189): no page, no STA thread, a fake repository. It reads
/// the settings and the tab on screen, another tab when it is opened, a tab's failure in the tab's
/// own line; keeps the query selected across a read; and follows the period, the custom period, a
/// link's query, the Over Time charts ticked, a wait category, and the rows' buttons.
/// </summary>
public sealed class QueryStoreViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShellStatus _shell = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeNavigator _navigator = new();
    private readonly List<(QueryStorePeriod Period, QueryStoreMetric Metric, QueryStoreStatistic Statistic, int Top)> _topReads = [];
    private readonly List<string> _reads = [];

    private ConnectionSettingsService? _settings;

    private static QueryStoreHealth Health(string state = "READ_WRITE", string? waitCapture = null, int forced = 0) =>
        new("SalesDb", state, "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, "AUTO", "AUTO", waitCapture,
            200, 1482, 1630, forced, DateTime.UtcNow.AddDays(-20), DateTime.Now);

    private static QueryStoreQueryRow Query(long id, long plan = 902, double cpu = 18_120) =>
        new(id, 0, null, $"SELECT {id} FROM dbo.Orders", 2, 8_412, 0, cpu, cpu, 2_310, 41_000_000, null, plan, DateTime.UtcNow.AddMinutes(-3));

    // Query Store on, with two top queries, and the reads of every tab written down.
    private FakeRepository Repo(QueryStoreHealth? health = null, QueryStoreFeatures? features = null)
    {
        var status = new QueryStoreStatus("SalesDb", health ?? Health(), features ?? QueryStoreFeatures.None);
        return new FakeRepository
        {
            Called                            = _reads.Add,
            GetQueryStoreStatusAsync          = () => Task.FromResult(status),
            GetQueryStoreTopQueriesAsync      = (period, metric, statistic, top) =>
            {
                _topReads.Add((period, metric, statistic, top));
                return Task.FromResult(new QueryStoreRows<QueryStoreQueryRow>([Query(4183), Query(2210, plan: 455, cpu: 13_200)]));
            },
            GetQueryStoreRegressionStatsAsync = _ => Task.FromResult(new QueryStoreRegressionStats("SalesDb", "READ_WRITE", true, [])),
            GetQueryStoreHighVariationAsync   = (_, _, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreVariationRow>(
                [new(4183, 0, null, "SELECT 4183", 2, 400, 120, 2, 900, 310, 902)])),
            GetQueryStoreOverTimeAsync        = (_, slot, origin) => Task.FromResult(new QueryStoreRows<QueryStoreSlot>(
            [
                new(QueryStoreTimeSlots.SlotStartUtc(origin, slot, QueryStoreTimeSlots.SlotIndex(origin, slot, DateTime.UtcNow.AddHours(-3))),
                    new QueryStoreTotals(120, 9_000_000, 12_000_000, 4_000, 10, 30, 0, 500, 0, 0)),
            ])),
            GetQueryStoreWaitCategoriesAsync  = _ => Task.FromResult(new QueryStoreRows<QueryStoreWaitCategory>(
                [new(4, "Lock", 9_000, 60), new(6, "Buffer IO", 6_000, 40)])),
            GetQueryStoreWaitQueriesAsync     = (_, category, _) => Task.FromResult(new QueryStoreRows<QueryStoreWaitQuery>(
                [new(4183, 0, null, "SELECT 4183", category * 1000, 9_000, 10, 902)])),
            GetQueryStoreForcedPlansAsync     = _ => Task.FromResult(new QueryStoreForcedPlans(
                new QueryStoreRows<QueryStoreForcedPlan>([new(4183, 902, 0, null, "SELECT 4183", "Manual", 2, "NO_INDEX", DateTime.UtcNow, 10, 4.2)]),
                new QueryStoreRows<QueryStoreHint>([new(2210, "OPTION (RECOMPILE)", "User", 0, null, "SELECT 2210")]))),
            GetQueryStorePlanAsync            = id => Task.FromResult<string?>($"<plan {id}/>"),
        };
    }

    private QueryStoreViewModel Connected(FakeRepository repo)
    {
        _settings = new ConnectionSettingsService(_dir);
        var model = new QueryStoreViewModel(repo, _settings, _navigator, _dialogs, _clipboard);
        model.Connect("QueryStore", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    private static async Task Opened(QueryStoreViewModel model)
    {
        await model.OnNavigatedToAsync();
        await model.VerdictLoaded;
    }

    // ── Loaded, empty, failed ────────────────────────────────────────────

    [Fact]
    public async Task Loaded_ShowsTheSettingsAsCards_AndTheTopQueries()
    {
        var model = Connected(Repo(Health(forced: 3)));

        await Opened(model);

        Assert.Contains("Read Load", _shell.Reports);
        Assert.Equal("Query Store — SalesDb", model.Title);
        Assert.Equal("READ_WRITE · 41.0 % of storage · settings OK", model.HealthSummary);
        Assert.Equal(KpiCard.Tones.Success, model.Cards.Single(c => c.Label == "State").Tone);
        Assert.Equal(("3", KpiCard.Tones.Warning), model.Cards.Single(c => c.Label == "Forced plans") is var f ? (f.Value, f.Tone) : default);
        Assert.Equal(KpiCard.Tones.Muted, model.Cards.Single(c => c.Label == "Stale after").Tone);
        Assert.StartsWith("Query Store has data from ", model.DataFromText);

        Assert.Equal([4183L, 2210L], model.TopRows.Select(r => r.QueryId));
        Assert.Equal("2 queries · Last 24 hours", model.TopStatus);
        Assert.Null(model.TopNotice);
        Assert.Equal("Total CPU ms", model.TopMetricHeader);
        Assert.False(model.ShowsTotalCpu);   // ranked by total CPU: the ranked column is it
        Assert.Equal(VerdictLevel.Ok, model.Verdict!.Level);
        // Only the tab on screen was read.
        Assert.DoesNotContain(nameof(FakeRepository.GetQueryStoreHighVariationAsync), _reads);
        Assert.Equal(PageKindOf(model), (QueryStorePeriod.Default.Label, true));
    }

    private static (string, bool) PageKindOf(QueryStoreViewModel model) => (model.PeriodLabel, model.DataTo is not null);

    [Fact]
    public async Task QueryStoreOff_EveryTabSaysSo_WithTheStatementThatTurnsItOn()
    {
        var model = Connected(Repo(Health(state: "OFF")));

        await Opened(model);

        Assert.Empty(model.TopRows);
        Assert.Equal("Query Store is off for SalesDb.", model.TopNotice!.Message);
        Assert.Equal("ALTER DATABASE [SalesDb] SET QUERY_STORE = ON;", model.TopNotice.Script);
        Assert.Equal(string.Empty, model.TopStatus);
        Assert.DoesNotContain(nameof(FakeRepository.GetQueryStoreTopQueriesAsync), _reads);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Forced;
        await model.TabLoaded;
        Assert.Equal("Query Store is off for SalesDb.", model.ForcedNotice!.Message);
        Assert.False(model.ShowsHintsHeader);
    }

    [Fact]
    public async Task TheSettingsUnread_TheVerdictSaysSo_OnceToTheTopBar()
    {
        var repo  = Repo();
        repo.GetQueryStoreStatusAsync = () => Task.FromException<QueryStoreStatus>(new InvalidOperationException("The login can't see it."));
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: The login can't see it."], _shell.Reports.Where(r => r != "Context"));
        Assert.Equal(VerdictLevel.Unknown, model.Verdict!.Level);
        Assert.Contains("Query Store's settings", model.Verdict.Headline);
        Assert.Empty(model.TopRows);
    }

    [Fact]
    public async Task ATabThatFails_SaysSoInItsLine_AndIsReadAgainWhenNextOpened()
    {
        var repo  = Repo();
        var fail  = true;
        var read  = repo.GetQueryStoreHighVariationAsync!;
        repo.GetQueryStoreHighVariationAsync = (p, m, e, t) =>
            fail ? Task.FromException<QueryStoreRows<QueryStoreVariationRow>>(new InvalidOperationException("Timeout expired.")) : read(p, m, e, t);
        var model = Connected(repo);
        await Opened(model);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;
        await model.TabLoaded;
        Assert.Equal("Error: Timeout expired.", model.VariationStatus);
        Assert.DoesNotContain(_shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal));   // the page read
        // The loading bar went, without "read" (#117).
        Assert.Equal("Reading High Variation ended", _shell.Readings[^1]);

        fail = false;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Top;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;
        await model.TabLoaded;
        Assert.Single(model.VariationRows);
        Assert.StartsWith("1 query · Last 24 hours · at least 10 executions each", model.VariationStatus);
    }

    // ── Tabs, read when opened ───────────────────────────────────────────

    [Fact]
    public async Task ATab_IsReadOnceForWhatIsOnScreen_AndAgainAfterRefreshOrANewPeriod()
    {
        var model = Connected(Repo());
        await Opened(model);
        int Reads() => _reads.Count(r => r == nameof(FakeRepository.GetQueryStoreForcedPlansAsync));

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Forced;
        await model.TabLoaded;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Top;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Forced;
        await model.TabLoaded;
        Assert.Equal(1, Reads());
        // Within the page's read, the bar shows the page; opened on its own, the tab (#117).
        Assert.Equal(["Load", "Load done", "Reading Forced Plans", "Reading Forced Plans done"], _shell.Readings);
        Assert.Equal("1 forced plan as they are now, 1 failing · executions and avg duration for last 24 hours", model.ForcedStatus);
        Assert.True(model.ShowsHints);
        Assert.Single(model.HintRows);

        model.PeriodIndex = 0;   // the last hour
        await model.TabLoaded;
        Assert.Equal(2, Reads());   // the tab on screen, at once
        Assert.Equal("Last hour", model.PeriodLabel);
    }

    // Code review: Stop on a tab read on its own cancelled the whole page; now it cancels that tab's read alone.
    [Fact]
    public async Task StoppingATabsRead_StopsThatReadAlone_AndTheTabIsReadAgainWhenOpened()
    {
        var repo    = Repo();
        var read    = repo.GetQueryStoreForcedPlansAsync!;
        var pending = new TaskCompletionSource<QueryStoreForcedPlans>();
        repo.GetQueryStoreForcedPlansAsync = p => pending is { } gate ? gate.Task : read(p);
        var model = Connected(repo);
        await Opened(model);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Forced;
        var token = repo.Tokens[nameof(FakeRepository.GetQueryStoreForcedPlansAsync)];
        Assert.Equal("Reading Forced Plans", _shell.Readings[^1]);
        _shell.Stops[^1]!();
        Assert.True(token.IsCancellationRequested);
        pending.SetCanceled(token);
        await model.TabLoaded;

        Assert.Equal("Reading Forced Plans ended", _shell.Readings[^1]);
        Assert.DoesNotContain(_shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal));
        Assert.Empty(model.ForcedRows);

        // Opened again, it reads again.
        pending = null!;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Top;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Forced;
        await model.TabLoaded;
        Assert.Single(model.ForcedRows);
    }

    [Fact]
    public async Task TheQuerySelected_IsKeptAcrossARead_AndTheDrawerKeepsItWhenTheSelectionGoes()
    {
        var model = Connected(Repo());
        await Opened(model);

        model.SelectedTopRow = model.TopRows.Single(r => r.QueryId == 2210);
        var before = model.SelectedTopRow;
        await model.RefreshAsync();

        Assert.NotSame(before, model.SelectedTopRow);   // the row read again
        Assert.Equal(2210, model.SelectedTopRow!.QueryId);
        Assert.Same(model.SelectedTopRow, model.Selected);

        model.SelectedTopRow = null;
        Assert.Equal(2210, ((QueryStoreQueryRow)model.Selected!).QueryId);
    }

    [Fact]
    public async Task TheDrawer_StaysOnTheRowPickedLast_WhenAnotherTabIsReadAgain()
    {
        var model = Connected(Repo());
        await Opened(model);
        model.SelectedTopRow = model.TopRows.Single(r => r.QueryId == 2210);
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;
        await model.TabLoaded;
        model.SelectedVariationRow = model.VariationRows[0];

        model.PeriodIndex = 0;   // Variation is read again
        await model.TabLoaded;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Top;   // and Top, which selects 2210 again
        await model.TabLoaded;

        Assert.Equal(2210, model.SelectedTopRow!.QueryId);
        Assert.IsType<QueryStoreVariationRow>(model.Selected);   // the drawer stays on the row picked last
    }

    [Fact]
    public async Task AStaleReadCancelled_LeavesTheNewerReadCurrent()
    {
        var repo  = Repo();
        var first = new TaskCompletionSource<QueryStoreRows<QueryStoreVariationRow>>();
        var read  = repo.GetQueryStoreHighVariationAsync!;
        var reads = 0;
        repo.GetQueryStoreHighVariationAsync = (p, m, e, t) => ++reads == 1 ? first.Task : read(p, m, e, t);
        var model = Connected(repo);
        await Opened(model);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;   // read 1, held
        var stale = model.TabLoaded;
        model.VariationMetricIndex = 1;                                 // read 2, shown
        await model.TabLoaded;
        first.SetCanceled();
        await stale;

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Top;
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;
        Assert.Equal(2, reads);   // still current: not read a third time
        Assert.Single(model.VariationRows);
    }

    [Fact]
    public async Task RankingByExecutions_HasNoStatistic_AndReadsTheTopQueriesAgain()
    {
        var model = Connected(Repo());
        await Opened(model);

        model.RankIndex = model.RankChoices.ToList().FindIndex(m => m.Metric == QueryStoreMetric.Executions);
        await model.TabLoaded;

        Assert.False(model.StatisticEnabled);
        Assert.Equal(QueryStoreMetric.Executions, _topReads[^1].Metric);
        Assert.True(model.ShowsTotalCpu);
        model.TopIndex = 2;
        await model.TabLoaded;
        Assert.Equal(100, _topReads[^1].Top);
    }

    // ── The period ───────────────────────────────────────────────────────

    [Fact]
    public async Task ACustomPeriod_StartsFromTheOneOnScreen_AndIsReadWhenItParses()
    {
        var model  = Connected(Repo());
        await Opened(model);
        var opened = 0;
        model.CustomPeriodOpened += () => opened++;

        model.PeriodIndex = model.PeriodNames.Count - 1;
        Assert.True(model.ShowsCustom);
        Assert.Equal(1, opened);
        Assert.Equal(DateTime.Now.Date, model.CustomToDate);

        model.CustomFromTime = "25:99";
        await model.ApplyCustomCommand.ExecuteAsync(null);
        Assert.True(model.ShowsPeriodError);
        Assert.Single(_topReads);

        model.CustomFromDate = DateTime.Today.AddDays(-1);
        model.CustomFromTime = "09:00";
        model.CustomToDate   = DateTime.Today.AddDays(-1);
        model.CustomToTime   = "17:00";
        await model.ApplyCustomCommand.ExecuteAsync(null);
        Assert.False(model.ShowsPeriodError);
        Assert.True(_topReads[^1].Period.IsCustom);
        Assert.Equal(TimeSpan.FromHours(8), _topReads[^1].Period.Length);

        model.PeriodIndex = 1;   // back to a preset: the boxes go
        Assert.False(model.ShowsCustom);
    }

    // ── A link's query (#145) ────────────────────────────────────────────

    [Fact]
    public async Task OpenedOnAQuery_ShowsOnlyIt_OpensItsWindowOnce_UntilShowAll()
    {
        var model = Connected(Repo());
        model.ShowFocus(new PageFocus(new FocusFilter(FocusKind.Query, 2210), new FocusOrigin(FocusSource.Page, "Query 2210", "Query Regressions")));

        await Opened(model);
        await model.RefreshAsync();

        Assert.Equal([2210L], model.TopRows.Select(r => r.QueryId));
        Assert.Equal("2210", model.QueryIdText);
        Assert.Equal("showing query 2210 only; its details are in their own window", model.FocusNote);
        Assert.Equal(["Query Store query 2210"], _dialogs.Shown);

        model.ClearFocus();
        await model.TabLoaded;
        Assert.Equal(2, model.TopRows.Count);
        Assert.Null(model.FocusNote);
        Assert.Null(model.ShowAllText);
    }

    [Fact]
    public async Task AQueryId_OpensItsWindow_AndAnythingElseSaysWhatToType()
    {
        var model = Connected(Repo());
        await Opened(model);

        model.QueryIdText = "abc";
        model.OpenQueryIdCommand.Execute(null);
        Assert.StartsWith("Enter a query ID", model.PeriodError);

        model.QueryIdText = " 4183 ";
        model.OpenQueryIdCommand.Execute(null);
        model.OpenRowCommand.Execute(model.TopRows[1]);
        Assert.Null(model.PeriodError);
        Assert.Equal(["Query Store query 4183", "Query Store query 2210"], _dialogs.Shown);
    }

    // ── Over Time ────────────────────────────────────────────────────────

    [Fact]
    public async Task OverTime_DrawsAChartPerMetricTicked_RemembersTheChoice_AndAPointOpensItsSlot()
    {
        var model = Connected(Repo());
        await Opened(model);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.OverTime;
        await model.TabLoaded;
        var ticked = model.ChartMetrics.Where(m => m.IsChecked).Select(m => m.Info.Metric).ToList();
        Assert.Equal(ticked.Count, model.OverTime.Charts.Count);
        Assert.Null(model.OverTime.Empty);
        Assert.All(model.OverTime.Charts, c => Assert.Contains(c.Points, p => p.Value is not null));
        Assert.StartsWith("The database's totals, each point an hour · Last 24 hours.", model.OverTimeStatus);

        var rows = model.ChartMetrics.Single(m => m.Info.Metric == QueryStoreMetric.Rows);
        rows.IsChecked = !rows.IsChecked;
        Assert.Equal(rows.IsChecked, _settings!.Load().QueryStoreChartMetrics.Contains(QueryStoreMetric.Rows));
        Assert.Equal(model.ChartMetrics.Count(m => m.IsChecked), model.OverTime.Charts.Count);

        var point = model.OverTime.Charts[0].Points.First(p => p.Value is not null);
        await model.PickSlotAsync(point.DateTime);
        Assert.Equal((int)QueryStoreViewModel.Tab.Top, model.SelectedTab);
        Assert.True(model.ShowsCustom);
        Assert.Equal(model.PeriodNames.Count - 1, model.PeriodIndex);
        Assert.Equal(TimeSpan.FromHours(1), _topReads[^1].Period.Length);
    }

    // ── Waits, Forced Plans, the rows' buttons ───────────────────────────

    [Fact]
    public async Task AWaitCategory_ReadsItsQueries_AndCaptureOff_SaysHowToTurnItOn()
    {
        var model = Connected(Repo(Health(waitCapture: "OFF"), QueryStoreFeatures.None with { WaitStats = true }));
        await Opened(model);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Waits;
        await model.TabLoaded;
        Assert.Equal(["Lock", "Buffer IO"], model.WaitBars.Select(b => b.Category.Name));
        Assert.Equal(1.0, model.WaitBars[0].Fraction);
        Assert.StartsWith("Wait stats capture is off for SalesDb", model.WaitsNotice!.Message);
        Assert.Equal("2 wait categories · Last 24 hours, largest first", model.WaitsStatus);

        model.SelectedWaitBar = model.WaitBars[1];
        await model.WaitQueriesLoaded;
        Assert.Equal(6_000, Assert.Single(model.WaitQueryRows).WaitMs);
        Assert.Equal("Top queries waiting on Buffer IO · Last 24 hours", model.WaitQueriesHeader);

        // Read again with the same bars: none is picked, so picking Buffer IO again reads its queries again.
        await model.RefreshAsync();
        Assert.Null(model.SelectedWaitBar);
        Assert.Empty(model.WaitQueryRows);
        model.SelectedWaitBar = model.WaitBars[1];
        await model.WaitQueriesLoaded;
        Assert.Single(model.WaitQueryRows);
    }

    [Fact]
    public async Task WaitsBefore2017_SaySo_WithoutReading()
    {
        var model = Connected(Repo());
        await Opened(model);

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Waits;
        await model.TabLoaded;

        Assert.StartsWith("This version of SQL Server doesn't record waits", model.WaitsNotice!.Message);
        Assert.False(model.ShowsWaitBars);
        Assert.DoesNotContain(nameof(FakeRepository.GetQueryStoreWaitCategoriesAsync), _reads);
    }

    [Fact]
    public async Task TheButtons_CopyTheFixAndUnforce_OpenForcedPlans_AndShowAPlan()
    {
        var repo  = Repo(Health(forced: 1) with { SizeCleanupMode = "OFF" });
        var model = Connected(repo);
        await Opened(model);

        model.CopyFixCommand.Execute(model.Checks.First(c => c.Fix is not null));
        Assert.Equal(model.Checks.First(c => c.Fix is not null).Fix, _clipboard.Text);

        model.OpenCardCommand.Execute(model.Cards.Single(c => c.Label == "Storage %"));
        Assert.Equal((int)QueryStoreViewModel.Tab.Top, model.SelectedTab);
        model.OpenCardCommand.Execute(model.Cards.Single(c => c.Label == "Forced plans"));
        Assert.Equal((int)QueryStoreViewModel.Tab.Forced, model.SelectedTab);
        await model.TabLoaded;

        model.CopyUnforceCommand.Execute(model.ForcedRows[0]);
        Assert.Contains("sp_query_store_unforce_plan", _clipboard.Text);

        await model.ViewPlanCommand.ExecuteAsync(model.TopRows[0]);
        Assert.Equal(["<plan 902/>"], _dialogs.Plans);
        repo.GetQueryStorePlanAsync = _ => Task.FromException<string?>(new InvalidOperationException("Gone."));
        await model.ViewPlanCommand.ExecuteAsync(model.TopRows[1]);
        Assert.Equal("Warn: Plan Error: Failed to retrieve execution plan:\nGone.", _dialogs.Shown[^1]);

        await model.RegressionsCommand.ExecuteAsync(null);
        Assert.Equal(["QueryRegressions"], _navigator.Moves);
    }

    [Fact]
    public async Task Leaving_WhileTheSettingsAreRead_SaysNothing()
    {
        var reading = new TaskCompletionSource<QueryStoreStatus>();
        var repo    = Repo();
        repo.GetQueryStoreStatusAsync = () => reading.Task;
        var model   = Connected(repo);

        var opening = model.OnNavigatedToAsync();
        model.OnNavigatedFrom();
        reading.SetCanceled();
        await opening;

        Assert.DoesNotContain(_shell.Reports, r => r.StartsWith("Read", StringComparison.Ordinal) || r.StartsWith("Error", StringComparison.Ordinal));
        Assert.Null(model.Verdict);
        Assert.Empty(model.TopRows);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
