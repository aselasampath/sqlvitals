using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Desktop.ViewModels.Windows;
using SqlVitals.Engine.ExecutionPlans;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.TableStatistics;
using static SqlVitals.Desktop.Tests.TableStatisticsDetailsTests;
using static SqlVitals.Desktop.Tests.TableStatisticsViewModelTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Links to Table Statistics (#244): each one's target, the page opening on it, what it says when it
/// can't, and the state it shows a login that isn't allowed to read statistics.
/// </summary>
public sealed class TableStatisticsLinkTests
{
    private const long Mb = 128;
    private static readonly DateTime Now = new(2026, 11, 7, 9, 0, 0);

    private readonly FakeShellStatus _shell = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeDialogService _dialogs = new();

    private TableStatisticsViewModel Connected(FakeRepository repo, TableStatisticsTarget? target = null)
    {
        var model = new TableStatisticsViewModel(repo, _navigator, _dialogs);
        model.Connect("TableStats", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        model.OpenOn(target);
        return model;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
            await Task.Delay(10);
        Assert.True(done());
    }

    private static TableReadFailure Denied => new(TableReadFailure.Statistics,
        "The login lacks SELECT in SalesDb. " + TableReadFailure.SelectWarning, "USE [SalesDb];\nGRANT SELECT ON [dbo].[Orders] TO [svc];") { IsDenied = true };

    // ── The page, opened on a target ──────────────────────────────────────

    [Fact]
    public async Task OpenedOnATable_SelectsIt_InTheDatabaseAsked_OnSizes()
    {
        string? read = "none";
        var repo = Repository();
        repo.GetTableSizesAsync = database =>
        {
            read = database;
            return Task.FromResult(new TableSizeList([Table(1, "Orders", 300 * Mb, 250 * Mb), Table(2, "Lines", 100 * Mb, 50 * Mb)], []));
        };
        TableSizeRow? shown = null;
        var model = Connected(repo, new TableStatisticsTarget("auditdb", "DBO", "lines"));
        model.ShowRow += row => shown = row;

        await model.OnNavigatedToAsync();

        Assert.Equal("AuditDb", model.SelectedDatabase);
        Assert.Equal("AuditDb", read);
        Assert.Equal("Lines", model.SelectedTable?.Table.TableName);
        Assert.Same(model.SelectedTable, shown);
        Assert.Equal(0, model.DetailTab);
        Assert.Null(model.TargetProblem);
    }

    [Fact]
    public async Task OpenedOnAStatisticsObject_ShowsItOnTheStatisticsTab()
    {
        var read = new List<int>();
        var repo = Repository();
        repo.GetStatisticsDetailsAsync = (_, _, statsId) =>
        {
            read.Add(statsId);
            return Task.FromResult(Sample());
        };
        var model = Connected(repo, new TableStatisticsTarget("SalesDb", "dbo", "Orders", "IX_Orders_Status"));

        await model.OnNavigatedToAsync();

        Assert.Equal(TableStatisticsViewModel.StatisticsTab, model.DetailTab);
        Assert.Equal("IX_Orders_Status", model.Statistics.SelectedObject?.Name);
        Assert.Equal([Status.StatsId], read);
        Assert.Null(model.Statistics.Problem);
    }

    [Fact]
    public async Task AStatisticsObjectGone_IsSaid_AndTheTablesOthersShow()
    {
        var model = Connected(Repository(), new TableStatisticsTarget("SalesDb", "dbo", "Orders", "IX_Dropped"));

        await model.OnNavigatedToAsync();

        Assert.Contains("IX_Dropped", model.Statistics.Problem);
        Assert.Equal("PK_Orders", model.Statistics.SelectedObject?.Name);
        Assert.NotEmpty(model.Statistics.ChartSteps);
    }

    [Fact]
    public async Task ADroppedTable_IsSaid_AndThePageStaysUsable()
    {
        var model = Connected(Repository(), new TableStatisticsTarget("SalesDb", "dbo", "Dropped"));

        await model.OnNavigatedToAsync();

        Assert.Contains("dbo.Dropped isn't in SalesDb", model.TargetProblem);
        Assert.Null(model.SelectedTable);
        Assert.Equal(2, model.Tables.Count);

        model.SelectedTable = model.Tables[0];
        Assert.Null(model.TargetProblem);
    }

    [Fact]
    public async Task ATableThatComesBack_IsOpenedOnRefresh()
    {
        var tables = new List<TableSize> { Table(1, "Orders", 300 * Mb, 250 * Mb) };
        var repo = Repository();
        repo.GetTableSizesAsync = _ => Task.FromResult(new TableSizeList(tables.ToList(), []));
        var model = Connected(repo, new TableStatisticsTarget("SalesDb", "dbo", "Back"));

        await model.OnNavigatedToAsync();
        Assert.NotNull(model.TargetProblem);

        tables.Add(Table(9, "Back", 1, 1));
        await model.RefreshAsync();

        Assert.Null(model.TargetProblem);
        Assert.Equal("Back", model.SelectedTable?.Table.TableName);
    }

    [Fact]
    public async Task ADatabaseTheLoginCantOpen_IsSaid_AndItsTablesArentLookedFor()
    {
        var model = Connected(Repository(), new TableStatisticsTarget("Secret", "dbo", "Orders"));

        await model.OnNavigatedToAsync();

        Assert.Contains("Secret", model.TargetProblem);
        Assert.Contains("can't open", model.TargetProblem);
        Assert.Equal("SalesDb", model.SelectedDatabase);
        Assert.Null(model.SelectedTable);
        Assert.Equal(2, model.Tables.Count);
    }

    [Fact]
    public async Task OnAzure_AnotherDatabase_SaysToConnectToIt()
    {
        var repo = Repository();
        repo.GetTableDatabasesAsync = () => Task.FromResult(new TableDatabases("SalesDb", ["SalesDb"], false));
        var model = Connected(repo, new TableStatisticsTarget("Other", "dbo", "Orders"));

        await model.OnNavigatedToAsync();

        Assert.Contains("Azure SQL Database", model.TargetProblem);
        Assert.Contains("connect to Other", model.TargetProblem);
        Assert.Null(model.SelectedTable);
        Assert.Equal(2, model.Tables.Count);
    }

    [Fact]
    public async Task APlanFromAnotherServer_IsSaid_AndANamesakeIsntOpened()
    {
        var model = Connected(Repository(), new TableStatisticsTarget("SalesDb", "dbo", "Orders", FromAnotherServer: true));

        await model.OnNavigatedToAsync();

        Assert.Contains("another server", model.TargetProblem);
        Assert.Null(model.SelectedTable);
    }

    [Fact]
    public async Task ChoosingAnotherDatabase_ForgetsTheLink()
    {
        var model = Connected(Repository(), new TableStatisticsTarget("SalesDb", "dbo", "Dropped"));
        await model.OnNavigatedToAsync();

        model.SelectedDatabase = "AuditDb";

        Assert.Null(model.TargetProblem);
        await Until(() => !model.IsBusy);
        Assert.Null(model.TargetProblem);
    }

    [Fact]
    public async Task ComingBack_ShowsWhatWasLeft_NotTheLinksTable()
    {
        var first = Connected(Repository(), new TableStatisticsTarget("SalesDb", "dbo", "Orders"));
        await first.OnNavigatedToAsync();
        first.SelectedTable = first.Tables.Single(t => t.Table.TableName == "Lines");
        await Until(() => first.Indexes.Count > 0);
        var saved = first.SaveState()!;

        var again = Connected(Repository(), new TableStatisticsTarget("SalesDb", "dbo", "Orders"));
        again.RestoreState(saved);
        await again.OnNavigatedToAsync();

        Assert.Equal("Lines", again.SelectedTable?.Table.TableName);
    }

    [Fact]
    public async Task OpeningFromALink_ChangesNoConnection()
    {
        var model = Connected(Repository(), new TableStatisticsTarget("AuditDb", "dbo", "Orders"));

        await model.OnNavigatedToAsync();

        Assert.Equal("SalesDb", model.ConnectedDatabase);
        Assert.Empty(_navigator.Moves);
    }

    // ── A login that isn't allowed to read them ───────────────────────────

    private static readonly StatisticsDetails DeniedDetails = new(null, [], [], 160, false, [Denied]);

    [Fact]
    public async Task ALoginThatCantRead_GetsNoFiguresOrScript_AndAWarningWithTheGrant()
    {
        var model = Connected(Repository(DeniedDetails), new TableStatisticsTarget("SalesDb", "dbo", "Orders", "PK_Orders"));

        await model.OnNavigatedToAsync();

        var statistics = model.Statistics;
        Assert.True(statistics.ReadDenied);
        Assert.False(statistics.CanRead);
        Assert.False(statistics.GenerateScriptCommand.CanExecute(null));
        Assert.Contains("read the table's rows", statistics.Problem);
        Assert.Contains("GRANT SELECT", statistics.Problem);
        Assert.Null(statistics.NoHistogramText);
        Assert.Null(statistics.ObjectVerdict);

        // Sizes still read: it's only statistics that are off.
        Assert.Equal("Orders", model.SelectedTable?.Table.TableName);
        Assert.NotEmpty(model.Indexes);
    }

    [Fact]
    public async Task AnObjectItCanRead_TurnsItBackOn()
    {
        var repo = Repository();
        repo.GetStatisticsDetailsAsync = (_, _, statsId) => Task.FromResult(statsId == Pk.StatsId ? DeniedDetails : Sample());
        var model = Connected(repo, new TableStatisticsTarget("SalesDb", "dbo", "Orders", "PK_Orders"));
        await model.OnNavigatedToAsync();
        Assert.True(model.Statistics.ReadDenied);

        model.Statistics.SelectedObject = Status;
        await Until(() => model.Statistics.ChartSteps.Count > 0);

        Assert.False(model.Statistics.ReadDenied);
        Assert.True(model.Statistics.GenerateScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task ABatchAllDenied_StopsStatisticsHealth_AndSaysSo()
    {
        var tables = Enumerable.Range(1, TableStatisticsViewModel.HealthBatch + 5).Select(i => Table(i, $"T{i}", 100 - i % 50, 1)).ToArray();
        var repo = Repo(tables);
        var batches = 0;
        repo.GetTableStatisticsHealthAsync = (_, ids) =>
        {
            batches++;
            return Task.FromResult(new TableHealthBatch(
                ids.Select(id => new TableStatisticsHealth(id, StatisticsHealthState.NotReadable, null, null, null)).ToList(), [Denied]));
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();
        await Until(() => model.HealthProgress is null && model.HealthProblem is not null);

        Assert.Equal(1, batches);
        Assert.Contains("Statistics health stopped", model.HealthProblem);
        Assert.Contains("read the table's rows", model.HealthProblem);
        Assert.All(model.Tables, t => Assert.Equal(StatisticsHealthState.NotReadable, t.Health?.State));
    }

    [Fact]
    public async Task ADeniedBatchAfterAReadOne_KeepsReading()
    {
        var tables = Enumerable.Range(1, TableStatisticsViewModel.HealthBatch * 2 + 5).Select(i => Table(i, $"T{i}", 1000 - i, 1)).ToArray();
        var repo = Repo(tables);
        var batches = 0;
        repo.GetTableStatisticsHealthAsync = (_, ids) =>
        {
            var denied = ++batches > 1;
            return Task.FromResult(new TableHealthBatch(
                ids.Select(id => new TableStatisticsHealth(id, denied ? StatisticsHealthState.NotReadable : StatisticsHealthState.Rated, denied ? null : StatisticsRating.Good, null, null)).ToList(),
                denied ? [Denied] : []));
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();
        await Until(() => model.HealthProgress is null && batches > 0);

        Assert.Equal(3, batches);
        Assert.DoesNotContain("stopped", model.HealthProblem);
    }

    [Fact]
    public async Task ATimeoutOnTheHeader_IsNotCalledDenied()
    {
        var failed = new TableReadFailure(TableReadFailure.Statistics, "The query timed out.", null);
        var model = Connected(Repository(new StatisticsDetails(null, [], [], 160, false, [failed])), new TableStatisticsTarget("SalesDb", "dbo", "Orders", "PK_Orders"));

        await model.OnNavigatedToAsync();

        Assert.False(model.Statistics.ReadDenied);
        Assert.Contains("timed out", model.Statistics.Problem);
    }

    [Fact]
    public async Task ACaseSensitiveDatabase_OpensTheExactTable()
    {
        var repo = Repository();
        repo.GetTableSizesAsync = _ => Task.FromResult(new TableSizeList([Table(1, "ORDERS", 900 * Mb, 1), Table(2, "Orders", 100 * Mb, 1)], []));
        var model = Connected(repo, new TableStatisticsTarget("SalesDb", "dbo", "Orders"));

        await model.OnNavigatedToAsync();

        Assert.Equal(2, model.SelectedTable?.Table.ObjectId);
    }

    [Fact]
    public async Task AnObjectThatComesBack_StopsBeingCalledMissing_OnRefresh()
    {
        var objects = new List<StatisticsObject> { Pk };
        var repo = Repository();
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList(objects.ToList(), false));
        var model = Connected(repo, new TableStatisticsTarget("SalesDb", "dbo", "Orders", "IX_Orders_Status"));
        await model.OnNavigatedToAsync();
        Assert.Contains("IX_Orders_Status", model.Statistics.Problem);

        objects.Add(Status);
        await model.RefreshAsync();

        Assert.Null(model.Statistics.Problem);
        Assert.Equal("IX_Orders_Status", model.Statistics.SelectedObject?.Name);
    }

    [Fact]
    public async Task ABatchWithSomeRated_KeepsReading()
    {
        var tables = Enumerable.Range(1, TableStatisticsViewModel.HealthBatch + 5).Select(i => Table(i, $"T{i}", 100 - i % 50, 1)).ToArray();
        var repo = Repo(tables);
        var batches = 0;
        repo.GetTableStatisticsHealthAsync = (_, ids) =>
        {
            batches++;
            return Task.FromResult(new TableHealthBatch(
                ids.Select((id, i) => new TableStatisticsHealth(id, i == 0 ? StatisticsHealthState.Rated : StatisticsHealthState.NotReadable,
                                                                i == 0 ? StatisticsRating.Good : null, null, null)).ToList(), [Denied]));
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();
        await Until(() => model.HealthProgress is null && batches > 0);

        Assert.Equal(2, batches);
    }

    // ── The links ─────────────────────────────────────────────────────────

    private static StaleStatistic Stale(string table, string name) =>
        new("dbo", table, name, Now, 1, 10, 10, 100, 5, false, false, false, "SalesDb", Now);

    [Fact]
    public async Task StaleStats_ARow_OpensItsDatabaseTableAndObject_AndKeepsTheSelection()
    {
        var rows = new[] { Stale("Orders", "IX_Date"), Stale("Lines", "PK_Lines") };
        var model = new StaleStatisticsViewModel(new FakeRepository { GetStaleStatisticsAsync = () => Task.FromResult<IEnumerable<StaleStatistic>>(rows) }, _dialogs, _navigator);
        model.Connect("StaleStats", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        await model.OnNavigatedToAsync();
        Assert.False(model.OpenTableStatisticsCommand.CanExecute(null));

        model.SelectedStatistic = rows[0];
        Assert.True(model.OpenTableStatisticsCommand.CanExecute(null));
        await model.OpenTableStatisticsCommand.ExecuteAsync(null);

        Assert.Equal(["TableStats ⟵ Stale Statistics: Table"], _navigator.Moves);
        Assert.Equal(new TableStatisticsTarget("SalesDb", "dbo", "Orders", "IX_Date"), _navigator.LastFilter?.Table);
        Assert.Same(rows[0], model.SelectedStatistic);

        await model.OpenTableStatisticsCommand.ExecuteAsync(rows[1]);
        Assert.Equal("Lines", _navigator.LastFilter?.Table?.Table);
        Assert.Same(rows[0], model.SelectedStatistic);
    }

    private IndexHealthViewModel IndexHealth()
    {
        var repo = new FakeRepository();
        var model = new IndexHealthViewModel(repo, repo, _dialogs, _navigator);
        model.Connect("IndexHealth", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    [Fact]
    public async Task IndexHealth_AnUnusedRow_OpensItsDatabaseAndTable()
    {
        var model = IndexHealth();
        Assert.False(model.OpenUnusedTableCommand.CanExecute(null));

        var row = new UnusedIndex("SalesDb", "dbo", "Orders", "IX_Legacy", "NONCLUSTERED", false, false, false, 0, 0, 0, 9, null, null, null, 0, 10, Now);
        model.SelectedUnused = row;
        Assert.True(model.OpenUnusedTableCommand.CanExecute(null));
        await model.OpenUnusedTableCommand.ExecuteAsync(null);

        Assert.Equal(["TableStats ⟵ Index Health: Table"], _navigator.Moves);
        Assert.Equal(new TableStatisticsTarget("SalesDb", "dbo", "Orders"), _navigator.LastFilter?.Table);
        Assert.Same(row, model.SelectedUnused);
    }

    [Fact]
    public async Task IndexHealth_AFragmentedRow_OpensItsDatabaseAndTable()
    {
        var model = IndexHealth();
        Assert.False(model.OpenFragmentedTableCommand.CanExecute(null));

        var row = new IndexFragmentation("sales", "Lines", "IX_Date", "NONCLUSTERED", 40, 2000, 70, 100, "SalesDb", "Rebuild", Now);
        model.SelectedFragmentation = row;
        await model.OpenFragmentedTableCommand.ExecuteAsync(null);

        Assert.Equal(new TableStatisticsTarget("SalesDb", "sales", "Lines"), _navigator.LastFilter?.Table);
        Assert.Same(row, model.SelectedFragmentation);
    }

    private QueryPlanViewModel PlanModel(Guid? server)
    {
        _navigator.ActiveServer = server;
        return new QueryPlanViewModel(null, null, null, new FakeClipboard(), new FakeFileDialogs(), _dialogs, _navigator);
    }

    [Fact]
    public async Task APlan_OpensAnOperatorsTable_AndTheMainWindowComesForward()
    {
        var server = Guid.NewGuid();
        var model = PlanModel(server);
        var opening = 0;
        model.TableStatisticsOpening += () => opening++;

        await model.OpenTableStatisticsCommand.ExecuteAsync(new TableStatisticsTarget("SalesDb", "dbo", "Orders", "PK_Orders"));

        Assert.Equal(["TableStats ⟵ the execution plan: Table"], _navigator.Moves);
        Assert.Equal(new TableStatisticsTarget("SalesDb", "dbo", "Orders", "PK_Orders"), _navigator.LastFilter?.Table);
        Assert.Equal(1, opening);
        Assert.Equal(1, _navigator.WindowShown);
        Assert.Equal(server, _navigator.ActiveServer);
    }

    [Fact]
    public async Task APlan_AfterASwitchOfServer_SaysItsFromAnotherServer()
    {
        var model = PlanModel(Guid.NewGuid());
        _navigator.ActiveServer = Guid.NewGuid();

        await model.OpenTableStatisticsCommand.ExecuteAsync(new TableStatisticsTarget("SalesDb", "dbo", "Orders"));

        Assert.True(_navigator.LastFilter?.Table?.FromAnotherServer);
    }

    [Fact]
    public void ALinksRoute_NamesTheTable_ForTheBanner()
    {
        var route = EvidenceRoutes.Link("TableStats", Guid.NewGuid(), new FocusFilter(FocusKind.Table, Table: new TableStatisticsTarget("SalesDb", "dbo", "Orders")), "Stale Statistics");

        Assert.Equal("dbo.Orders", route.Focus?.Origin.What);
        Assert.Equal("Stale Statistics", route.Focus?.Origin.Page);
    }

    [Fact]
    public void TheParsedPlan_NamesTheStatisticsUsed()
    {
        var plan = ShowplanParser.Parse("""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence><Batch><Statements><StmtSimple StatementType="SELECT" StatementSubTreeCost="1"><QueryPlan>
                <OptimizerStatsUsage><StatisticsInfo Database="[A]" Schema="[dbo]" Table="[T]" Statistics="[S]"/></OptimizerStatsUsage>
              </QueryPlan></StmtSimple></Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """);

        Assert.Equal(new PlanStatisticsUsed("A", "dbo", "T", "S"), Assert.Single(plan.Statements[0].StatisticsUsed));
    }
}
