using Microsoft.Extensions.Logging;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.TableStatistics;
using static SqlVitals.Desktop.Tests.TableStatisticsViewModelTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The Statistics tab's view model (#243): what it reads and when, a new choice cancelling the last,
/// what it says when there's nothing to show, the choice of steps, the script, and the privacy of the
/// histogram's keys.
/// </summary>
public sealed class TableStatisticsDetailsTests
{
    private const long Mb = 128;

    /// <summary>A value that must stay on screen: nothing else may hold it.</summary>
    internal const string SentinelKey = "secret.person@example.com";

    private readonly FakeShellStatus _shell = new();
    private readonly FakeDialogService _dialogs = new();

    internal static readonly StatisticsObject Pk = new(1, "PK_Orders", true, true, false, "OrderId");
    internal static readonly StatisticsObject Status = new(2, "IX_Orders_Status", true, false, false, "Status");
    internal static readonly StatisticsObject Region = new(3, "_WA_Sys_Region", false, false, false, "Region");

    internal static HistogramStep Step(int number, string? key, double range, double eq, double distinct = 1) =>
        new(number, key, range, eq, distinct, distinct > 0 ? range / distinct : 0);

    /// <summary>A NULL step, an empty string, a key far above the estimate, and a hidden frequent value.</summary>
    internal static StatisticsDetails Sample(string key = "Pending") => new(
        new StatisticsProperties(new DateTime(2026, 11, 1, 3, 30, 0), 100_000, 20_000, 6, 40_000),
        [
            Step(1, null, 0, 30_000),
            Step(2, "", 0, 500),
            Step(3, key, 1_000, 50_000, 100),
            Step(4, "Shipped", 8_000, 400, 4),
            Step(5, "Returned", 1_000, 300, 100),
            Step(6, "Void", 8_800, 1, 8_800),
        ],
        [new DensityEntry("Status", 0.01, 8), new DensityEntry("Status, OrderId", 0.00001, 12)],
        160, false, []);

    internal static FakeRepository Repository(StatisticsDetails? details = null)
    {
        var repo = Repo(Table(1, "Orders", 300 * Mb, 250 * Mb), Table(2, "Lines", 100 * Mb, 50 * Mb));
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Pk, Status, Region], false, []));
        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromResult(details ?? Sample());
        return repo;
    }

    private TableStatisticsViewModel Connected(FakeRepository repo)
    {
        var model = new TableStatisticsViewModel(repo, new FakeNavigator(), _dialogs, TimeProvider.System);
        model.Connect("TableStats", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    private async Task<TableStatisticsViewModel> OnTheTab(FakeRepository repo)
    {
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        model.SelectedTable = model.Tables[0];
        await Until(() => model.Statistics.ChartSteps.Count > 0 || model.Statistics.Problem is not null || model.Statistics.NoHistogramText is not null);
        return model;
    }

    // ── When it reads ────────────────────────────────────────────────────

    [Fact]
    public async Task StayingOnSizes_ReadsNoStatistics_UntilTheTabIsShown()
    {
        var read = 0;
        var repo = Repository();
        repo.GetTableStatisticsAsync = (_, _) =>
        {
            read++;
            return Task.FromResult(new TableStatisticsList([Pk], false, []));
        };
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 3);
        Assert.Equal(0, read);

        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        await Until(() => model.Statistics.ChartSteps.Count > 0);
        Assert.Equal(1, read);

        // Back and forth, it has it already.
        model.DetailTab = 0;
        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        await Task.Delay(20);
        Assert.Equal(1, read);

        // A table chosen while it shows is read at once.
        model.SelectedTable = model.Tables[1];
        await Until(() => read == 2);
    }

    [Fact]
    public async Task Refresh_ReadsItAgain_ButOnlyIfShowing()
    {
        var read = 0;
        var repo = Repository();
        repo.GetTableStatisticsAsync = (_, _) =>
        {
            read++;
            return Task.FromResult(new TableStatisticsList([Pk, Status], false, []));
        };
        var model = await OnTheTab(repo);
        Assert.Equal(1, read);
        model.Statistics.SelectedObject = Status;
        await Until(() => model.Statistics.ChartSteps.Count > 0);

        await model.RefreshAsync();

        Assert.Equal(2, read);
        Assert.Equal("IX_Orders_Status", model.Statistics.SelectedObject!.Name);    // the object stays chosen
        Assert.NotEmpty(model.Statistics.ChartSteps);
        Assert.Same(model.Tables[0], model.SelectedTable);

        model.DetailTab = 0;
        await model.RefreshAsync();
        Assert.Equal(2, read);
        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        await Until(() => read == 3);
    }

    [Fact]
    public async Task Refresh_LeavesWhatShows_UntilItsNewAnswer()
    {
        var repo = Repository();
        var model = await OnTheTab(repo);

        var slow = new TaskCompletionSource<StatisticsDetails>();
        repo.GetStatisticsDetailsAsync = (_, _, _) => slow.Task;
        var refresh = model.RefreshAsync();
        await Until(() => repo.Tokens[nameof(FakeRepository.GetStatisticsDetailsAsync)].CanBeCanceled && !slow.Task.IsCompleted && model.IsBusy);
        await Task.Delay(20);

        Assert.Equal(6, model.Statistics.ChartSteps.Count);
        Assert.True(model.Statistics.HasHistogram);

        slow.SetResult(Sample("Newest"));
        await refresh;
        Assert.Contains(model.Statistics.ChartSteps, s => s.Key == "Newest");
    }

    [Fact]
    public async Task ItStartsOnTheClusteredIndex_ElseTheFirst()
    {
        var repo = Repository();
        var asked = new List<int>();
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Region, Pk, Status], false, []));
        repo.GetStatisticsDetailsAsync = (_, _, statsId) =>
        {
            asked.Add(statsId);
            return Task.FromResult(Sample());
        };
        var model = await OnTheTab(repo);

        Assert.Equal(Pk, model.Statistics.SelectedObject);
        Assert.Equal([1], asked);
        Assert.Contains("Clustered index statistics", model.Statistics.KindText);
        Assert.Contains("OrderId", model.Statistics.KindText);

        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Status, Region], false, []));
        model.SelectedTable = model.Tables[1];
        await Until(() => model.Statistics.SelectedObject == Status);
        Assert.Equal("Index statistics, histogram on Status", model.Statistics.KindText);

        model.Statistics.SelectedObject = Region;
        await Until(() => asked[^1] == 3);
        Assert.Equal("Column statistics, histogram on Region", model.Statistics.KindText);
    }

    private static readonly StatisticsObject Columnstore = new(1, "CCI_Orders", true, true, false, null);
    private static readonly StatisticsDetails NoneHeld = new(null, [], [], 160, false, []);

    [Fact]
    public async Task AClusteredColumnstore_IsPassedOver_ForAnObjectWithAHistogram()
    {
        var repo = Repository();
        var asked = new List<int>();
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Columnstore, Status, Region], false, []));
        repo.GetStatisticsDetailsAsync = (_, _, statsId) =>
        {
            asked.Add(statsId);
            return Task.FromResult(statsId == 3 ? Sample() : NoneHeld);
        };
        var page = await OnTheTab(repo);

        Assert.Equal(Region, page.Statistics.SelectedObject);
        Assert.Equal([1, 2, 3], asked);
        Assert.Equal(6, page.Statistics.ChartSteps.Count);
        Assert.Null(page.Statistics.NoHistogramText);

        // Chosen by hand, it's what shows, and a Refresh keeps it.
        page.Statistics.SelectedObject = Columnstore;
        await Until(() => page.Statistics.NoHistogramText is not null);
        await page.RefreshAsync();
        Assert.Equal(Columnstore, page.Statistics.SelectedObject);
        Assert.Contains("columnstore", page.Statistics.NoHistogramText);
    }

    [Fact]
    public async Task ATableWithOnlyAColumnstore_SaysThereIsNoHistogram()
    {
        var repo = Repository(NoneHeld);
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Columnstore], false, []));
        var model = (await OnTheTab(repo)).Statistics;

        Assert.Equal(Columnstore, model.SelectedObject);
        Assert.Contains("columnstore", model.NoHistogramText);
        Assert.False(model.HasHistogram);
    }

    [Fact]
    public async Task WithNoHistogramAnywhere_ItStaysOnTheClustered()
    {
        var model = (await OnTheTab(Repository(NoneHeld))).Statistics;

        Assert.Equal(Pk, model.SelectedObject);
        Assert.Contains("No histogram yet", model.NoHistogramText);
    }

    [Fact]
    public async Task ALock_IsNotPassedOver_AndAsksNoMore()
    {
        var repo = Repository();
        var asked = new List<int>();
        repo.GetStatisticsDetailsAsync = (_, _, statsId) =>
        {
            asked.Add(statsId);
            return Task.FromResult(new StatisticsDetails(null, [], [], null, true, []));
        };
        var model = (await OnTheTab(repo)).Statistics;

        Assert.Equal([1], asked);
        Assert.Equal(Pk, model.SelectedObject);
        Assert.Contains("under a schema change", model.Problem);
    }

    [Fact]
    public async Task ChoosingAnotherTable_WhilePassingOver_StopsAsking()
    {
        var repo = Repository();
        var slow = new TaskCompletionSource<StatisticsDetails>();
        repo.GetStatisticsDetailsAsync = (_, _, statsId) => statsId == 1 ? Task.FromResult(NoneHeld) : slow.Task;
        repo.GetTableStatisticsAsync = (_, id) => Task.FromResult(id == 1 ? new TableStatisticsList([Columnstore, Status], false, []) : new TableStatisticsList([Pk], false, []));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        model.SelectedTable = model.Tables.Single(t => t.Table.ObjectId == 1);
        await Until(() => repo.Tokens.ContainsKey(nameof(FakeRepository.GetStatisticsDetailsAsync)));
        var token = repo.Tokens[nameof(FakeRepository.GetStatisticsDetailsAsync)];

        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromResult(Sample());
        model.SelectedTable = model.Tables.Single(t => t.Table.ObjectId == 2);

        Assert.True(token.IsCancellationRequested);
        await Until(() => model.Statistics.ChartSteps.Count == 6);
        slow.SetResult(Sample("Late"));
        await Task.Delay(20);
        Assert.Equal(Pk, model.Statistics.SelectedObject);
        Assert.DoesNotContain(model.Statistics.ChartSteps, s => s.Key == "Late");
    }

    [Fact]
    public async Task ChoosingAnotherObject_Cancels_AndTheLateAnswerIsIgnored()
    {
        var repo = Repository();
        var model = await OnTheTab(repo);

        var slow = new TaskCompletionSource<StatisticsDetails>();
        repo.GetStatisticsDetailsAsync = (_, _, _) => slow.Task;
        model.Statistics.SelectedObject = Status;
        var first = repo.Tokens[nameof(FakeRepository.GetStatisticsDetailsAsync)];
        Assert.Empty(model.Statistics.ChartSteps);   // the last object's chart doesn't stay under the new name

        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromResult(Sample("Newest"));
        model.Statistics.SelectedObject = Region;

        Assert.True(first.IsCancellationRequested);
        await Until(() => model.Statistics.ChartSteps.Count == 6);
        slow.SetResult(Sample("Late"));
        await Task.Delay(20);
        Assert.Contains(model.Statistics.ChartSteps, s => s.Key == "Newest");
        Assert.DoesNotContain(model.Statistics.ChartSteps, s => s.Key == "Late");
    }

    [Fact]
    public async Task ChoosingAnotherTable_CancelsTheRead_AndLeavingDoesToo()
    {
        var repo = Repository();
        var slow = new TaskCompletionSource<TableStatisticsList>();
        repo.GetTableStatisticsAsync = (_, _) => slow.Task;
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        model.SelectedTable = model.Tables[0];
        var first = repo.Tokens[nameof(FakeRepository.GetTableStatisticsAsync)];

        model.SelectedTable = model.Tables[1];
        Assert.True(first.IsCancellationRequested);

        var second = repo.Tokens[nameof(FakeRepository.GetTableStatisticsAsync)];
        Assert.False(second.IsCancellationRequested);
        model.OnNavigatedFrom();
        Assert.True(second.IsCancellationRequested);
    }

    [Fact]
    public async Task TheLoadingBarsStop_EndsTheRead_AndSaysSo()
    {
        var repo = Repository();
        var slow = new TaskCompletionSource<TableStatisticsList>();
        repo.GetTableStatisticsAsync = (_, _) => slow.Task;
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 3);

        model.DetailTab = TableStatisticsViewModel.StatisticsTab;
        Assert.Contains("Reading the table's statistics", _shell.Readings);
        _shell.Stops[^1]!();
        slow.SetCanceled();

        await Until(() => model.Statistics.Problem is not null);
        Assert.Contains("stopped", model.Statistics.Problem);
        Assert.Equal("Orders", model.Tables[0].TableName);
    }

    // ── What it shows ────────────────────────────────────────────────────

    [Fact]
    public async Task AnObject_ShowsItsHeaderChartGridsAndFindings()
    {
        var model = (await OnTheTab(Repository())).Statistics;

        Assert.Equal("2026-11-01 03:30", model.LastUpdatedText);
        Assert.Equal(100_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), model.RowsText);
        Assert.Equal(20_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), model.RowsSampledText);
        Assert.Equal("20 %", model.SampleRateText);
        Assert.Equal("6", model.StepsText);
        Assert.Equal(40_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), model.ModificationsText);

        Assert.Equal(6, model.ChartSteps.Count);
        Assert.Equal(1_000, model.UnknownEstimate);   // all density x rows
        Assert.Equal(6, model.Histogram.Count);
        Assert.Equal("NULL", model.Histogram[0].KeyText);
        Assert.Equal("''", model.Histogram[1].KeyText);

        Assert.Equal(2, model.Density.Count);
        Assert.Equal(100, model.Density[0].DistinctValues!.Value, 3);
        Assert.Equal(1_000, model.Density[0].RowsPerValue!.Value, 3);

        Assert.Contains(model.Findings, f => f.Kind == StatisticsFindingKind.PastAutoUpdateThreshold);
        Assert.Contains("PK_Orders: Poor", model.ObjectVerdict!.Headline);
        Assert.Null(model.Problem);
        Assert.Null(model.NoHistogramText);
    }

    [Fact]
    public async Task NoHistogramYet_SaysSo_RatherThanAnEmptyChart()
    {
        var never = new StatisticsDetails(null, [], [], 160, false, []);
        var model = (await OnTheTab(Repository(never))).Statistics;

        Assert.Contains("No histogram yet", model.NoHistogramText);
        Assert.Contains("columnstore", model.NoHistogramText);
        Assert.False(model.HasHistogram);
        Assert.Equal(TableSpace.Unknown, model.RowsText);
        Assert.Contains("nothing to rate", model.ObjectVerdict!.Headline);
        Assert.Null(model.Problem);
    }

    [Fact]
    public async Task ALockedTable_SaysSo_NotThatThereIsNoHistogram()
    {
        var repo = Repository(new StatisticsDetails(null, [], [], null, true, []));
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Pk], false, []));
        var model = (await OnTheTab(repo)).Statistics;

        Assert.Contains("under a schema change", model.Problem);
        Assert.Null(model.NoHistogramText);
        Assert.Null(model.ObjectVerdict);
    }

    [Fact]
    public async Task ALockedList_SaysSo()
    {
        var repo = Repository();
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([], true, []));
        var model = (await OnTheTab(repo)).Statistics;

        Assert.Contains("under a schema change", model.Problem);
        Assert.Empty(model.Objects);
    }

    [Fact]
    public async Task APermissionMissing_ShowsTheGrant_AndTheSizesStay()
    {
        var failure = new TableReadFailure("the table's statistics", "The login lacks VIEW DEFINITION in SalesDb.", "GRANT VIEW DEFINITION TO [me];");
        var repo = Repository();
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([], false, [failure]));
        var page = await OnTheTab(repo);

        Assert.Contains("VIEW DEFINITION in SalesDb", page.Statistics.Problem);
        Assert.Contains("GRANT VIEW DEFINITION TO [me];", page.Statistics.Problem);
        await Until(() => page.Indexes.Count == 3);
        Assert.Equal("300 MB", page.DataText);
        Assert.Null(page.DetailProblem);
    }

    [Fact]
    public async Task AThrownRead_SaysSoOnTheTab_AndTheSizesStay()
    {
        var repo = Repository();
        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromException<StatisticsDetails>(new InvalidOperationException("The server said no."));
        var page = await OnTheTab(repo);

        Assert.Contains("The server said no.", page.Statistics.Problem);
        await Until(() => page.Indexes.Count == 3);
        Assert.Null(page.DetailProblem);
        Assert.Contains(_shell.Reports, r => r.StartsWith("Error Timed", StringComparison.Ordinal));
    }

    // ── The choice of steps ──────────────────────────────────────────────

    [Fact]
    public async Task TheChoiceOfSteps_Filters_AndLeavesTheChartAndFindingsWhole()
    {
        var model = (await OnTheTab(Repository())).Statistics;
        var findings = model.Findings;
        var filters = 0;
        model.StepFiltersChanged += () => filters++;

        Assert.False(model.HasStepFilters);
        Assert.Equal(6, model.Histogram.Count(model.ShowsStep));

        model.StepChoice = TableStatisticsDetailsViewModel.FarSteps;
        Assert.True(model.HasStepFilters);
        Assert.Equal(1, filters);
        // The estimate is 1,000: 500 and 300 are near it, and NULL never counts.
        Assert.Equal([3, 6], model.Histogram.Where(model.ShowsStep).Select(s => s.Number));

        model.StepChoice = TableStatisticsDetailsViewModel.AboveAverageSteps;
        Assert.Equal([4], model.Histogram.Where(model.ShowsStep).Select(s => s.Number));

        Assert.Equal(6, model.ChartSteps.Count);
        Assert.Same(findings, model.Findings);

        model.StepsShown(0);
        Assert.Equal("No rows match the filters.", model.NoStepsText);
        model.StepsShown(1);
        Assert.Null(model.NoStepsText);
        model.DensityShown(0);
        Assert.Equal("No rows match the filter.", model.NoDensityText);
    }

    // ── The script ───────────────────────────────────────────────────────

    [Fact]
    public async Task TheScript_IsForTheChosenObject_ShownForReview_AndNeverRun()
    {
        var model = (await OnTheTab(Repository())).Statistics;

        Assert.True(model.GenerateScriptCommand.CanExecute(null));
        model.GenerateScriptCommand.Execute(null);

        var script = Assert.Single(_dialogs.Scripts);
        Assert.Contains("Script: Update Statistics (modal)", _dialogs.Shown);
        Assert.Contains("USE [SalesDb];", script.Text);
        Assert.Contains("UPDATE STATISTICS [dbo].[Orders] ([PK_Orders]);", script.Text);
        Assert.DoesNotContain("FULLSCAN;", script.Text);
        Assert.Contains("does not run", script.Notice);

        model.FullScan = true;
        model.GenerateScriptCommand.Execute(null);
        Assert.Contains("UPDATE STATISTICS [dbo].[Orders] ([PK_Orders]) WITH FULLSCAN;", _dialogs.Scripts[1].Text);
        Assert.Contains("every row", _dialogs.Scripts[1].Notice);

        // The view model has nothing that could run it: reads are all it holds.
        Assert.DoesNotContain(typeof(TableStatisticsDetailsViewModel).GetConstructors().Single().GetParameters(),
                              p => p.ParameterType.Name.Contains("Execute", StringComparison.Ordinal) || p.ParameterType.Name.Contains("Session", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScript_NeedsAnObject()
    {
        var model = Connected(Repository()).Statistics;
        Assert.False(model.GenerateScriptCommand.CanExecute(null));
    }

    // ── Coming back ──────────────────────────────────────────────────────

    [Fact]
    public async Task LeavingAndComingBack_KeepsTheTabObjectStepsAndScale()
    {
        var repo = Repository();
        var model = await OnTheTab(repo);
        model.Statistics.SelectedObject = Status;
        await Until(() => model.Statistics.ChartSteps.Count > 0);
        model.Statistics.StepChoice = TableStatisticsDetailsViewModel.FarSteps;
        model.Statistics.LogScale = true;
        model.Statistics.FullScan = true;
        var saved = model.SaveState()!;
        model.OnNavigatedFrom();

        var back = Connected(repo);
        back.RestoreState(saved);
        await back.OnNavigatedToAsync();

        Assert.Equal(TableStatisticsViewModel.StatisticsTab, back.DetailTab);
        Assert.Equal("Orders", back.SelectedTable!.TableName);
        Assert.Equal(Status, back.Statistics.SelectedObject);
        Assert.Equal(TableStatisticsDetailsViewModel.FarSteps, back.Statistics.StepChoice);
        Assert.True(back.Statistics.LogScale);
        Assert.True(back.Statistics.FullScan);
        Assert.Equal(6, back.Statistics.ChartSteps.Count);
    }

    [Fact]
    public async Task AnObjectGoneAfterComingBack_FallsBackToTheClustered()
    {
        var repo = Repository();
        var model = await OnTheTab(repo);
        model.Statistics.SelectedObject = Status;
        await Until(() => model.Statistics.ChartSteps.Count > 0);
        var saved = model.SaveState()!;
        model.OnNavigatedFrom();

        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([Pk, Region], false, []));
        var back = Connected(repo);
        back.RestoreState(saved);
        await back.OnNavigatedToAsync();

        Assert.Equal(Pk, back.Statistics.SelectedObject);
    }

    // ── Privacy ──────────────────────────────────────────────────────────

    private sealed class CapturingLogs : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
                Lines.Add(formatter(state, exception) + exception);
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task AHistogramKey_ReachesOnlyTheChartAndTheGrid()
    {
        // Every finding fires on it: the skew, the parameter sensitivity, the hidden value.
        var logs = new CapturingLogs();
        AppLog.Factory.AddProvider(logs);
        var repo = Repository(Sample(SentinelKey));
        var page = await OnTheTab(repo);
        var model = page.Statistics;
        Assert.Contains(model.ChartSteps, s => s.Key == SentinelKey);   // the test is about something

        var said = new List<string?> { model.ObjectVerdict!.Headline, model.ObjectVerdict.Why, model.Problem, model.NoHistogramText, model.KindText,
                                       model.SelectedText, page.Status, page.Verdict?.Headline, page.Verdict?.Why, page.HealthProblem, page.DetailProblem };
        said.AddRange(model.Findings.SelectMany(f => new[] { f.What, f.Try }));
        Assert.NotEmpty(model.Findings);

        // A read that then fails, and leaving, with the key on screen.
        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromException<StatisticsDetails>(new InvalidOperationException("The server said no."));
        model.SelectedObject = Status;
        await Until(() => model.Problem is not null);
        said.Add(model.Problem);

        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromResult(Sample(SentinelKey));
        model.SelectedObject = Pk;
        await Until(() => model.ChartSteps.Count > 0);
        model.GenerateScriptCommand.Execute(null);
        said.AddRange(_dialogs.Scripts.SelectMany(s => new[] { s.Heading, s.Notice, s.Text, s.FileName }));
        said.AddRange(_dialogs.Shown);
        said.AddRange(_shell.Reports);
        said.AddRange(_shell.Readings);
        said.Add(page.SaveState()!.ToString());
        page.OnNavigatedFrom();

        Assert.All(said, text => Assert.DoesNotContain(SentinelKey, text ?? "", StringComparison.OrdinalIgnoreCase));
        lock (logs.Lines)
            Assert.All(logs.Lines, line => Assert.DoesNotContain(SentinelKey, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheTabsCode_HasNoWayToTheHistoryTheActivityLogOrNotifications()
    {
        var parameters = typeof(TableStatisticsViewModel).GetConstructors().SelectMany(c => c.GetParameters())
            .Concat(typeof(TableStatisticsDetailsViewModel).GetConstructors().SelectMany(c => c.GetParameters()))
            .Select(p => p.ParameterType.Name).ToList();
        Assert.DoesNotContain(parameters, name => name.Contains("History", StringComparison.Ordinal) || name.Contains("Activity", StringComparison.Ordinal)
                                               || name.Contains("Notif", StringComparison.Ordinal) || name.Contains("Chat", StringComparison.Ordinal)
                                               || name.Contains("Log", StringComparison.Ordinal));

        foreach (var file in new[] { "ViewModels/TableStatisticsDetailsViewModel.cs", "ViewModels/HistogramStepRow.cs", "Controls/HistogramChart.cs", "Controls/HistogramLayout.cs" })
        {
            var code = System.IO.File.ReadAllText(System.IO.Path.Combine(DesktopSource.Dir, file));
            Assert.DoesNotContain("AppLog", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ActivityLog", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ILogger", code, StringComparison.Ordinal);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "It didn't happen in time.");
    }
}
