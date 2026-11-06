using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.TableStatistics;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Table Statistics' view model on its own (#242): which database and table it reads, that a new
/// choice cancels the last one's answer, and what it says when part of a read is missing.
/// </summary>
public sealed class TableStatisticsViewModelTests
{
    private const long Mb = 128;

    internal static readonly DateTime Started = new(2026, 10, 30, 8, 15, 0);

    private readonly FakeShellStatus _shell = new();
    private readonly FakeNavigator _navigator = new();

    internal static TableSize Table(int id, string name, long data, long index, string schema = "dbo") =>
        new(id, schema, name, 1000, data, index, false);

    internal static TableIndexList Indexes(params TableIndexSize[] indexes) => new(indexes, false, Started, []);

    internal static TableIndexSize Clustered(long pages) => new(1, "PK", "CLUSTERED", true, 1000, pages, pages, 50, 20);

    internal static TableIndexSize Costly(long pages) => new(2, "IX_Legacy", "NONCLUSTERED", false, 1000, pages, pages, 17, 972_000);

    internal static TableIndexSize Plain(long pages) => new(3, "IX_Plain", "NONCLUSTERED", false, 1000, pages, pages, 900, 900);

    internal static FakeRepository Repo(params TableSize[] tables) => new()
    {
        GetTableDatabasesAsync = () => Task.FromResult(new TableDatabases("SalesDb", ["AuditDb", "SalesDb"], true)),
        GetTableSizesAsync = _ => Task.FromResult(new TableSizeList(tables, [])),
        GetTableIndexesAsync = (_, _) => Task.FromResult(Indexes(Clustered(300 * Mb), Costly(200 * Mb), Plain(50 * Mb))),
    };

    private TableStatisticsViewModel Connected(FakeRepository repo)
    {
        var model = new TableStatisticsViewModel(repo, _navigator);
        model.Connect("TableStats", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    [Fact]
    public async Task Opened_ListsTheConnectionsDatabase_AndReadsNoTablesIndexes()
    {
        string? read = "none";
        var calledIndexes = false;
        var repo = Repo(Table(1, "Orders", 300 * Mb, 250 * Mb));
        repo.GetTableSizesAsync = database =>
        {
            read = database;
            return Task.FromResult(new TableSizeList([Table(1, "Orders", 300 * Mb, 250 * Mb)], []));
        };
        repo.GetTableIndexesAsync = (_, _) =>
        {
            calledIndexes = true;
            return Task.FromResult(Indexes());
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal("SalesDb", read);
        Assert.Equal("SalesDb", model.SelectedDatabase);
        Assert.Equal(["AuditDb", "SalesDb"], model.Databases);
        Assert.True(model.CanSwitch);
        Assert.Equal("Orders", Assert.Single(model.Tables).TableName);
        Assert.Equal("Tables in SalesDb, largest first", model.TablesHeader);
        Assert.False(model.HasSelection);
        Assert.False(calledIndexes);
    }

    [Fact]
    public async Task ItNeverReadsOnATimer()
    {
        var model = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.False(model.ShowsCountdownForTest());
    }

    [Fact]
    public async Task OnAzureSqlDatabase_ThereIsNoSwitching_AndItReadsTheConnection()
    {
        var read = new List<string?>();
        var repo = Repo();
        repo.GetTableDatabasesAsync = () => Task.FromResult(new TableDatabases("AzureDb", ["AzureDb"], false));
        repo.GetTableSizesAsync = database =>
        {
            read.Add(database);
            return Task.FromResult(new TableSizeList([], []));
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.False(model.CanSwitch);
        Assert.Equal("AzureDb", model.ConnectedDatabase);
        Assert.Equal([null], read);
        Assert.Null(model.IndexHealthNote);
    }

    [Fact]
    public async Task ChoosingATable_ShowsItsSizes_AndTheBarIsTheWholeTable()
    {
        var model = Connected(Repo(Table(1, "Orders", 300 * Mb, 250 * Mb)));
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 3);

        Assert.Equal("Selected dbo.Orders", model.SelectedText);
        Assert.Equal("300 MB", model.DataText);
        Assert.Equal("250 MB", model.IndexesText);
        Assert.Equal("550 MB", model.TotalText);
        var legacy = model.Indexes.Single(i => i.Name == "IX_Legacy");
        Assert.True(legacy.IsCostly);
        Assert.Equal("200 MB", legacy.ReservedText);
        Assert.NotEmpty(legacy.Note);
        Assert.Equal(200.0 / 550 * 100, legacy.SharePct!.Value, 3);
        Assert.False(model.Indexes.Single(i => i.Name == "IX_Plain").IsCostly);
        Assert.Contains("2026-10-30 08:15", model.UsageNote);

        Assert.Equal(1.0, model.SpaceBar.Sum(p => p.Share), 3);
        Assert.Equal(SpaceBarPart.Kinds.Data, model.SpaceBar[0].Kind);
        Assert.Contains(model.SpaceBar, p => p.Kind == SpaceBarPart.Kinds.Costly);

        // The bar's about the table, so the grid's filters don't touch it.
        var bar = model.SpaceBar;
        model.FlaggedOnly = true;
        Assert.Same(bar, model.SpaceBar);
    }

    [Fact]
    public async Task AFlaggedIndexesLink_OpensIndexHealth()
    {
        var model = Connected(Repo());

        await model.OpenIndexHealthCommand.ExecuteAsync(null);

        Assert.Equal(["IndexHealth"], _navigator.Moves);
    }

    [Fact]
    public async Task ChoosingAnotherTable_CancelsTheLastOnesRead_AndItsAnswerNeverShows()
    {
        var first = new TaskCompletionSource<TableIndexList>();
        var repo = Repo(Table(1, "Orders", Mb, 0), Table(2, "Customers", Mb, 0));
        repo.GetTableIndexesAsync = (_, id) => id == 1 ? first.Task : Task.FromResult(Indexes(Plain(Mb)));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        var token = repo.Tokens[nameof(FakeRepository.GetTableIndexesAsync)];
        model.SelectedTable = model.Tables[1];
        await Until(() => model.Indexes.Count == 1);

        Assert.True(token.IsCancellationRequested);

        // The first read answers late, as one that couldn't be cancelled in time would.
        first.SetResult(Indexes(Clustered(Mb), Costly(Mb)));
        await Task.Delay(20);
        Assert.Equal("IX_Plain", Assert.Single(model.Indexes).Name);
        Assert.DoesNotContain(_shell.Reports, r => r.Contains("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChoosingADatabase_ClearsWhatWasShown_AndReadsThatOne_NotTheConnection()
    {
        var reads = new List<string?>();
        var audit = new TaskCompletionSource<TableSizeList>();
        var repo = Repo(Table(1, "Orders", Mb, 0));
        repo.GetTableSizesAsync = database =>
        {
            reads.Add(database);
            return database == "AuditDb" ? audit.Task : Task.FromResult(new TableSizeList([Table(1, "Orders", Mb, 0)], []));
        };
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 3);

        model.SelectedDatabase = "AuditDb";

        // Nothing of the last database stays while this one is read.
        Assert.Null(model.SelectedTable);
        Assert.Empty(model.Indexes);
        Assert.Empty(model.Tables);
        Assert.Null(model.Verdict);

        audit.SetResult(new TableSizeList([Table(9, "Log", Mb, 0)], []));
        await Until(() => model.Tables.Count == 1);
        Assert.Equal("Log", model.Tables[0].TableName);
        Assert.Equal(["SalesDb", "AuditDb"], reads);
        Assert.Contains("SalesDb", model.IndexHealthNote);
    }

    [Fact]
    public async Task ChoosingADatabase_CancelsTheListStillBeingRead_AndItsAnswerNeverShows()
    {
        var slow = new TaskCompletionSource<TableSizeList>();
        var repo = Repo();
        repo.GetTableSizesAsync = database => database == "SalesDb"
            ? slow.Task
            : Task.FromResult(new TableSizeList([Table(9, "Log", Mb, 0)], []));
        var model = Connected(repo);
        var opening = model.OnNavigatedToAsync();
        await Until(() => model.SelectedDatabase == "SalesDb");
        var token = repo.Tokens[nameof(FakeRepository.GetTableSizesAsync)];

        model.SelectedDatabase = "AuditDb";

        Assert.True(token.IsCancellationRequested);
        slow.SetResult(new TableSizeList([Table(1, "Orders", Mb, 0)], []));
        await opening;
        await Until(() => model.Tables.Count == 1);
        Assert.Equal("Log", Assert.Single(model.Tables).TableName);
    }

    [Fact]
    public async Task Refresh_ReadsTheSelectedTableAgain_AndKeepsItSelected()
    {
        var reads = 0;
        var repo = Repo(Table(1, "Orders", Mb, 0), Table(2, "Customers", Mb, 0));
        repo.GetTableIndexesAsync = (_, _) =>
        {
            reads++;
            return Task.FromResult(Indexes(Plain(Mb)));
        };
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedTable = model.Tables[1];
        await Until(() => reads == 1);

        await model.RefreshAsync();

        Assert.Equal(2, reads);
        Assert.Equal("Customers", model.SelectedTable!.TableName);
        Assert.Same(model.Tables[1], model.SelectedTable);
    }

    [Fact]
    public async Task ATableDroppedSinceTheLastRead_IsNoLongerSelected()
    {
        var tables = new[] { Table(1, "Orders", Mb, 0) };
        var repo = Repo();
        repo.GetTableSizesAsync = _ => Task.FromResult(new TableSizeList(tables, []));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count > 0);

        tables = [Table(2, "Other", Mb, 0)];
        await model.RefreshAsync();

        Assert.Null(model.SelectedTable);
        Assert.Empty(model.Indexes);
    }

    [Fact]
    public async Task ALockedTable_SaysSo_AndTheOthersStillLoad()
    {
        var repo = Repo(Table(1, "Orders", Mb, 0), new TableSize(2, "dbo", "Held", null, null, null, true));
        repo.GetTableIndexesAsync = (_, _) => Task.FromResult(new TableIndexList(
            [new TableIndexSize(1, "PK_Held", "CLUSTERED", true, null, null, null, null, null)], true, Started, []));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        var held = model.Tables.Single(t => t.IsLocked);
        Assert.Equal("Locked", held.TotalText);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);

        model.SelectedTable = held;
        await Until(() => model.Indexes.Count == 1);

        Assert.Equal("Locked", model.TotalText);
        Assert.Contains("schema change", model.DetailProblem);
        Assert.Empty(model.SpaceBar);
    }

    [Fact]
    public async Task AMissingPermission_IsNamed_AndWhatItCanReadStillShows()
    {
        var failure = new TableReadFailure("index reads and writes", "The login lacks VIEW SERVER STATE.", "USE [master];\nGRANT VIEW SERVER STATE TO [app];");
        var repo = Repo(Table(1, "Orders", Mb, 0));
        repo.GetTableIndexesAsync = (_, _) => Task.FromResult(new TableIndexList(
            [new TableIndexSize(1, "PK", "CLUSTERED", true, 10, Mb, Mb, null, null)], false, null, [failure]));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 1);

        Assert.Contains("VIEW SERVER STATE", model.DetailProblem);
        Assert.Contains("GRANT VIEW SERVER STATE", model.DetailProblem);
        Assert.Null(model.Indexes[0].Reads);
        Assert.Contains("since the server last restarted", model.UsageNote);
    }

    [Fact]
    public async Task ADatabaseThatCantBeRead_SaysSo_InTheVerdict()
    {
        var repo = Repo();
        repo.GetTableSizesAsync = _ => Task.FromException<TableSizeList>(new InvalidOperationException("Timeout expired."));
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(VerdictLevel.Unknown, model.Verdict!.Level);
        Assert.Contains("the tables in", model.Verdict.Headline);
    }

    [Fact]
    public async Task IndexesThatFailToRead_SaySoBesideTheTable_AndTheListStays()
    {
        var repo = Repo(Table(1, "Orders", Mb, 0));
        repo.GetTableIndexesAsync = (_, _) => Task.FromException<TableIndexList>(new InvalidOperationException("Timeout expired."));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        await Until(() => model.DetailProblem is not null);

        Assert.Contains("Timeout expired.", model.DetailProblem);
        Assert.Single(model.Tables);
    }

    [Fact]
    public async Task Filters_ComeBack_WithTheDatabaseAndTable()
    {
        var read = new List<string?>();
        var repo = Repo(Table(1, "Orders", Mb, 0, "sales"), Table(2, "Log", Mb, 0, "audit"));
        var sizes = repo.GetTableSizesAsync!;
        repo.GetTableSizesAsync = database =>
        {
            read.Add(database);
            return sizes(database);
        };
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedDatabase = "AuditDb";
        await Until(() => model.Tables.Count == 2);
        model.SelectedTable = model.Tables[1];
        await Until(() => model.Indexes.Count == 3);
        model.Schema.Items.Single(i => i.Value == "audit").IsChecked = true;
        model.IndexType.Items.Single(i => i.Value == "Nonclustered").IsChecked = true;
        model.FlaggedOnly = true;
        var saved = model.SaveState()!;

        read.Clear();
        var again = Connected(repo);
        again.RestoreState(saved);
        await again.OnNavigatedToAsync();
        await Until(() => again.Indexes.Count == 3);

        Assert.Equal(["AuditDb"], read);
        Assert.Equal("AuditDb", again.SelectedDatabase);
        Assert.Equal("Log", again.SelectedTable!.TableName);
        Assert.True(again.FlaggedOnly);
        Assert.False(again.ShowsTable(again.Tables.Single(t => t.TableName == "Orders")));
        Assert.True(again.ShowsTable(again.Tables.Single(t => t.TableName == "Log")));
        Assert.Equal(["IX_Legacy"], again.Indexes.Where(again.ShowsIndex).Select(i => i.Name));
    }

    [Fact]
    public async Task ADatabaseGoneSinceItWasChosen_FallsBackToTheConnections_WithNoTableSelected()
    {
        var repo = Repo(Table(1, "Orders", Mb, 0));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedDatabase = "AuditDb";
        await Until(() => model.Tables.Count == 1);
        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 3);

        repo.GetTableDatabasesAsync = () => Task.FromResult(new TableDatabases("SalesDb", ["SalesDb"], true));
        await model.RefreshAsync();

        Assert.Equal("SalesDb", model.SelectedDatabase);
        Assert.Null(model.SelectedTable);
        Assert.Empty(model.Indexes);
    }

    [Fact]
    public async Task ClearFilters_TurnsThemAllOff_AndTheTableStaysSelected()
    {
        var model = Connected(Repo(Table(1, "Orders", Mb, 0, "sales"), Table(2, "Log", Mb, 0, "audit")));
        var searchCleared = 0;
        model.TableSearchCleared += () => searchCleared++;
        await model.OnNavigatedToAsync();
        model.SelectedTable = model.Tables[0];
        model.Schema.Items.Single(i => i.Value == "audit").IsChecked = true;

        model.ClearTableFiltersCommand.Execute(null);

        Assert.False(model.HasTableFilters);
        Assert.Equal(1, searchCleared);
        Assert.Equal("Orders", model.SelectedTable!.TableName);
    }

    [Fact]
    public async Task TheVerdictsShowIt_SelectsTheLargestTable()
    {
        var model = Connected(Repo(Table(1, "Big", 10 * Mb, 0), Table(2, "Small", Mb, 0)));
        TableSizeRow? shown = null;
        model.ShowRow += row => shown = row;
        await model.OnNavigatedToAsync();

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions[0]);

        Assert.Equal("Big", model.SelectedTable!.TableName);
        Assert.Same(model.SelectedTable, shown);
    }

    [Fact]
    public async Task ATableDroppedAfterTheListWasRead_SaysSo_RatherThanShowingZero()
    {
        var repo = Repo(Table(1, "Orders", 300 * Mb, 0));
        repo.GetTableIndexesAsync = (_, _) => Task.FromResult(Indexes());
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        await Until(() => model.DetailProblem is not null);

        Assert.Contains("no longer in this database", model.DetailProblem);
        Assert.Equal("300 MB", model.DataText);
    }

    [Fact]
    public async Task TheLoadingBarsStop_OnATablesRead_SaysSo_AndReportsNoError()
    {
        var repo = Repo(Table(1, "Orders", Mb, 0));
        repo.GetTableIndexesAsync = (_, _) => Task.FromException<TableIndexList>(new OperationCanceledException());
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.SelectedTable = model.Tables[0];
        await Until(() => model.DetailProblem is not null);

        Assert.Contains("stopped", model.DetailProblem);
        Assert.DoesNotContain(_shell.Reports, r => r.Contains("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChoosingADatabase_ForgetsATableRestoredFromTheLastOne()
    {
        var chosen = new List<int>();
        var repo = Repo(Table(1, "Orders", Mb, 0));
        repo.GetTableIndexesAsync = (_, id) =>
        {
            chosen.Add(id);
            return Task.FromResult(Indexes(Plain(Mb)));
        };
        var first = Connected(repo);
        await first.OnNavigatedToAsync();
        first.SelectedTable = first.Tables[0];
        await Until(() => first.Indexes.Count == 1);
        var saved = first.SaveState()!;
        chosen.Clear();

        var pending = new TaskCompletionSource<TableSizeList>();
        var sizes = repo.GetTableSizesAsync!;
        repo.GetTableSizesAsync = database => database == "SalesDb" ? pending.Task : sizes(database);
        var again = Connected(repo);
        again.RestoreState(saved);
        var opening = again.OnNavigatedToAsync();
        await Until(() => again.Databases.Count > 0);

        again.SelectedDatabase = "AuditDb";
        pending.SetResult(new TableSizeList([], []));
        await opening;
        await Until(() => again.Tables.Count == 1);

        // The same object id in another database is another table: it isn't selected for that.
        Assert.Null(again.SelectedTable);
        Assert.Empty(chosen);
    }

    [Fact]
    public async Task ADatabaseGoneSinceItWasChosen_ForgetsItsRowsAndSchemas()
    {
        var repo = Repo(Table(1, "Log", Mb, 0, "audit"));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedDatabase = "AuditDb";
        await Until(() => model.Tables.Count == 1);
        model.Schema.Items.Single(i => i.Value == "audit").IsChecked = true;

        repo.GetTableDatabasesAsync = () => Task.FromResult(new TableDatabases("SalesDb", ["SalesDb"], true));
        repo.GetTableSizesAsync = _ => Task.FromException<TableSizeList>(new InvalidOperationException("Timeout expired."));
        await model.RefreshAsync();

        Assert.False(model.HasTableFilters);
        Assert.Empty(model.Tables);
        Assert.Equal("SalesDb", model.SelectedDatabase);
    }

    [Fact]
    public async Task NoTablesTheLoginCanSee_DoesntSayThereAreNone()
    {
        var repo = Repo();
        repo.GetTableSizesAsync = _ => Task.FromResult(new TableSizeList([], [new TableReadFailure(TableReadFailure.Names, "The login lacks VIEW DEFINITION.", null)]));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();

        model.TablesShown(0);

        Assert.Equal("This login can't see any tables in this database.", model.NoTablesText);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);
    }

    [Fact]
    public async Task EmptyGrids_SayWhy()
    {
        var model = Connected(Repo(Table(1, "Orders", Mb, 0)));
        model.TablesShown(0);
        Assert.Null(model.NoTablesText);    // not read yet

        await model.OnNavigatedToAsync();
        model.TablesShown(0);
        Assert.Equal("No tables match the filters.", model.NoTablesText);
        model.TablesShown(1);
        Assert.Null(model.NoTablesText);

        model.SelectedTable = model.Tables[0];
        await Until(() => model.Indexes.Count == 3);
        model.IndexesShown(0);
        Assert.Equal("No indexes match the filters.", model.NoIndexesText);
    }

    [Fact]
    public async Task NoTablesAtAll_SaysSo()
    {
        var model = Connected(Repo());
        await model.OnNavigatedToAsync();

        model.TablesShown(0);

        Assert.Equal("This database has no user tables.", model.NoTablesText);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "It didn't happen in time.");
    }
}

file static class Extensions
{
    /// <summary>A page that reads on a timer has a countdown running; this one has none.</summary>
    public static bool ShowsCountdownForTest(this TableStatisticsViewModel model) => model.Countdown != "--";
}
