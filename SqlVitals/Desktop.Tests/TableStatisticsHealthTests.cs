using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.TableStatistics;
using static SqlVitals.Desktop.Tests.TableStatisticsViewModelTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The Statistics health column (#243): a second read after the sizes, in batches, largest first, that
/// holds nothing up, is cancelled by what would make it wrong, and can fail without touching the sizes.
/// </summary>
public sealed class TableStatisticsHealthTests
{
    private const long Mb = 128;

    private readonly FakeShellStatus _shell = new();

    private TableStatisticsViewModel Connected(FakeRepository repo)
    {
        var model = new TableStatisticsViewModel(repo, new FakeNavigator(), new FakeDialogService());
        model.Connect("TableStats", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    private static TableStatisticsHealth Rated(int id, StatisticsRating rating, double changed = 5) =>
        new(id, StatisticsHealthState.Rated, rating, changed, new DateTime(2026, 9, 1));

    private static TableStatisticsHealth State(int id, StatisticsHealthState state) => new(id, state, null, null, null);

    private static TableSizeRow Row(TableStatisticsViewModel model, int id) => model.Tables.Single(t => t.Table.ObjectId == id);

    [Fact]
    public async Task HealthArrives_InBatches_LargestFirst_RowsChangingInPlace()
    {
        // Listed in no order of size: the read still asks for the largest first.
        var tables = Enumerable.Range(1, 120).Select(i => Table(i, $"T{i}", i * Mb, 0)).ToArray();
        var repo = Repo(tables);
        var asked = new List<IReadOnlyList<int>>();
        var answers = new List<TaskCompletionSource<TableHealthBatch>>();
        repo.GetTableStatisticsHealthAsync = (_, ids) =>
        {
            asked.Add(ids);
            answers.Add(new TaskCompletionSource<TableHealthBatch>());
            return answers[^1].Task;
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        // The sizes are shown, the page has read, and the health doesn't hold its loading state.
        Assert.False(model.IsBusy);
        Assert.Equal(120, model.Tables.Count);
        Assert.Contains("Load done", _shell.Readings);
        Assert.All(model.Tables, r => Assert.Null(r.HealthText));
        Assert.Equal("Rating statistics: 0 of 120 tables", model.HealthProgress);

        var row120 = Row(model, 120);
        var changes = new List<string?>();
        row120.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var sameRows = model.Tables.ToList();

        Assert.Equal(Enumerable.Range(71, 50).Reverse(), Assert.Single(asked));
        answers[0].SetResult(new TableHealthBatch(asked[0].Select(id => Rated(id, StatisticsRating.Fair)).ToList(), []));
        await Until(() => asked.Count == 2);

        Assert.Equal("Fair", row120.HealthText);
        Assert.Equal("5 %", row120.MostChangedText);
        Assert.Equal("2026-09-01", row120.OldestUpdateText);
        Assert.Contains(nameof(TableSizeRow.HealthText), changes);
        Assert.Null(Row(model, 70).HealthText);
        Assert.Equal("Rating statistics: 50 of 120 tables", model.HealthProgress);
        Assert.Equal(Enumerable.Range(21, 50).Reverse(), asked[1]);

        answers[1].SetResult(new TableHealthBatch(asked[1].Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        await Until(() => asked.Count == 3);
        Assert.Equal(20, asked[2].Count);
        answers[2].SetResult(new TableHealthBatch(asked[2].Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        await Until(() => model.HealthProgress is null);

        Assert.All(model.Tables, r => Assert.NotNull(r.HealthText));
        Assert.Equal(sameRows, model.Tables);   // the same rows, so the grid keeps its selection
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ALockedTable_ShowsLocked_AndTheOthersAreRated()
    {
        var repo = Repo(Table(1, "Orders", Mb, 0), Table(2, "Busy", Mb, 0), Table(3, "Empty", Mb, 0), Table(4, "Hidden", Mb, 0));
        repo.GetTableStatisticsHealthAsync = (_, _) => Task.FromResult(new TableHealthBatch(
            [Rated(1, StatisticsRating.Poor), State(2, StatisticsHealthState.Locked), State(3, StatisticsHealthState.NoStatistics), State(4, StatisticsHealthState.NotReadable)], []));
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal("Poor", Row(model, 1).HealthText);
        Assert.Equal("Locked", Row(model, 2).HealthText);
        Assert.Contains("schema change", Row(model, 2).HealthTip);
        Assert.Equal(TableSpace.Unknown, Row(model, 3).HealthText);
        Assert.Contains("no statistics", Row(model, 3).HealthTip);
        Assert.Equal(TableSpace.Unknown, Row(model, 4).HealthText);
        Assert.Contains("can't read", Row(model, 4).HealthTip);
        Assert.Equal(TableSpace.Unknown, Row(model, 3).MostChangedText);
        Assert.Equal(TableSpace.Unknown, Row(model, 3).OldestUpdateText);

        // Sorted by severity, the worst first; those with nothing to rate after the rated.
        Assert.True(Row(model, 1).HealthOrder < Row(model, 2).HealthOrder);
        Assert.True(Row(model, 2).HealthOrder < Row(model, 3).HealthOrder);
    }

    [Fact]
    public void Severity_PutsPoorThenFairThenLockedThenGood_AndNotReadLast()
    {
        var row = new TableSizeRow(Table(1, "T", Mb, 0), Mb);
        var notRead = row.HealthOrder;
        var orders = new List<int>();
        foreach (var health in new[] { Rated(1, StatisticsRating.Poor), Rated(1, StatisticsRating.Fair), State(1, StatisticsHealthState.Locked), Rated(1, StatisticsRating.Good),
                                       State(1, StatisticsHealthState.NoStatistics), State(1, StatisticsHealthState.NotReadable) })
        {
            row.ShowHealth(health);
            orders.Add(row.HealthOrder);
        }

        Assert.Equal(orders.Order(), orders);
        Assert.True(notRead > orders.Max());
    }

    private static (FakeRepository Repo, TaskCompletionSource<TableHealthBatch> Answer) Pending(params TableSize[] tables)
    {
        var repo = Repo(tables);
        var answer = new TaskCompletionSource<TableHealthBatch>();
        repo.GetTableStatisticsHealthAsync = (_, _) => answer.Task;
        return (repo, answer);
    }

    [Fact]
    public async Task LeavingThePage_CancelsIt_AndALateAnswerShowsNothing()
    {
        var (repo, answer) = Pending(Table(1, "Orders", Mb, 0));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetTableStatisticsHealthAsync)];
        Assert.False(token.IsCancellationRequested);

        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        answer.SetResult(new TableHealthBatch([Rated(1, StatisticsRating.Poor)], []));
        await Task.Delay(20);
        Assert.Null(model.Tables[0].HealthText);
    }

    [Fact]
    public async Task ChoosingAnotherDatabase_CancelsIt_AndReadsTheNewOne()
    {
        var (repo, _) = Pending(Table(1, "Orders", Mb, 0));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetTableStatisticsHealthAsync)];

        string? readFrom = null;
        repo.GetTableStatisticsHealthAsync = (database, ids) =>
        {
            readFrom = database;
            return Task.FromResult(new TableHealthBatch(ids.Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        };
        model.SelectedDatabase = "AuditDb";

        Assert.True(token.IsCancellationRequested);
        await Until(() => readFrom == "AuditDb");
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task Refresh_CancelsIt_AndStartsAgainOnTheNewRows()
    {
        var (repo, first) = Pending(Table(1, "Orders", Mb, 0));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetTableStatisticsHealthAsync)];

        repo.GetTableStatisticsHealthAsync = (_, ids) => Task.FromResult(new TableHealthBatch(ids.Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        await model.RefreshAsync();

        Assert.True(token.IsCancellationRequested);
        Assert.Equal("Good", model.Tables[0].HealthText);

        // The first answer, late, is about rows gone.
        first.SetResult(new TableHealthBatch([Rated(1, StatisticsRating.Poor)], []));
        await Task.Delay(20);
        Assert.Equal("Good", model.Tables[0].HealthText);
        Assert.Null(model.HealthProgress);
    }

    [Fact]
    public async Task AFailure_ShowsThePermission_LeavesTheRestBlank_AndTheSizesAlone()
    {
        var tables = Enumerable.Range(1, 60).Select(i => Table(i, $"T{i}", (100 - i) * Mb, 0)).ToArray();
        var repo = Repo(tables);
        var calls = 0;
        repo.GetTableStatisticsHealthAsync = (_, ids) =>
        {
            calls++;
            return Task.FromResult(new TableHealthBatch(ids.Select(id => State(id, StatisticsHealthState.NotReadable)).ToList(),
                [new TableReadFailure("statistics health", "The login lacks VIEW DEFINITION in SalesDb.", "GRANT VIEW DEFINITION TO [me];")]));
        };
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(2, calls);
        Assert.Equal(1, model.HealthProblem!.Split("VIEW DEFINITION in SalesDb").Length - 1);   // once, for two batches
        Assert.Contains("GRANT VIEW DEFINITION TO [me];", model.HealthProblem);
        Assert.Equal("99 MB", Row(model, 1).TotalText);
        Assert.Equal(TableSpace.Unknown, Row(model, 1).HealthText);
    }

    [Fact]
    public async Task AThrownRead_SaysSo_AndStops_WithTheSizesStillShown()
    {
        var repo = Repo(Table(1, "Orders", Mb, 0));
        repo.GetTableStatisticsHealthAsync = (_, _) => Task.FromException<TableHealthBatch>(new InvalidOperationException("The server said no."));
        var model = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Contains("wasn't read for every table", model.HealthProblem);
        Assert.Null(model.HealthProgress);
        Assert.Null(model.Tables[0].HealthText);
        Assert.Equal("Orders", model.Tables[0].TableName);
        Assert.Contains(_shell.Reports, r => r.StartsWith("Error Timed", StringComparison.Ordinal));
        Assert.DoesNotContain(_shell.Reports, r => r.StartsWith("Error Load", StringComparison.Ordinal));
    }

    // ── The chips ────────────────────────────────────────────────────────

    private static void Tick(TableStatisticsViewModel model, string chip) => model.Health.Items.Single(i => i.Value == chip).IsChecked = true;

    [Fact]
    public void TheChips_AreGoodFairPoorAndLocked()
    {
        var model = Connected(Repo());
        Assert.Equal(["Fair", "Good", "Locked", "Poor"], model.Health.Items.Select(i => i.Value));
    }

    [Fact]
    public async Task Chips_TakeSeveralValues_AndClearFiltersTurnsThemOff()
    {
        var repo = Repo(Table(1, "A", Mb, 0), Table(2, "B", Mb, 0), Table(3, "C", Mb, 0), Table(4, "D", Mb, 0));
        repo.GetTableStatisticsHealthAsync = (_, _) => Task.FromResult(new TableHealthBatch(
            [Rated(1, StatisticsRating.Good), Rated(2, StatisticsRating.Poor), State(3, StatisticsHealthState.Locked), State(4, StatisticsHealthState.NoStatistics)], []));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        Assert.False(model.HasTableFilters);

        Tick(model, "Poor");
        Tick(model, "Locked");

        Assert.True(model.HasTableFilters);
        Assert.Equal(["B", "C"], model.Tables.Where(model.ShowsTable).Select(r => r.TableName));

        model.ClearTableFiltersCommand.Execute(null);

        Assert.False(model.HasTableFilters);
        Assert.Equal(4, model.Tables.Count(model.ShowsTable));
    }

    [Fact]
    public async Task ARowWithoutItsHealth_MatchesNoChip_AndItRefiltersOncePerBatch_OnlyWhenOneIsTicked()
    {
        var tables = Enumerable.Range(1, 120).Select(i => Table(i, $"T{i}", i * Mb, 0)).ToArray();
        var repo = Repo(tables);
        var answers = new List<TaskCompletionSource<TableHealthBatch>>();
        var asked = new List<IReadOnlyList<int>>();
        repo.GetTableStatisticsHealthAsync = (_, ids) =>
        {
            asked.Add(ids);
            answers.Add(new TaskCompletionSource<TableHealthBatch>());
            return answers[^1].Task;
        };
        var model = Connected(repo);
        var refilters = 0;
        model.TableFiltersChanged += () => refilters++;
        await model.OnNavigatedToAsync();

        // Nothing ticked: a batch costs no refilter.
        answers[0].SetResult(new TableHealthBatch(asked[0].Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        await Until(() => asked.Count == 2);
        Assert.Equal(0, refilters);

        Tick(model, "Good");
        Assert.Equal(1, refilters);
        Assert.True(model.ShowsTable(Row(model, 120)));
        Assert.False(model.ShowsTable(Row(model, 1)));   // not arrived

        // No match among those rated yet, and more to come: it says so.
        model.Health.Clear();
        Tick(model, "Poor");
        refilters = 0;
        model.TablesShown(0);
        Assert.Contains("Reading Statistics health", model.NoTablesText);

        answers[1].SetResult(new TableHealthBatch(asked[1].Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        await Until(() => asked.Count == 3);
        Assert.Equal(1, refilters);   // one, for fifty rows

        answers[2].SetResult(new TableHealthBatch(asked[2].Select(id => Rated(id, StatisticsRating.Good)).ToList(), []));
        await Until(() => model.HealthProgress is null);
        Assert.Equal(2, refilters);
        model.TablesShown(0);
        Assert.Equal("No tables match the filters.", model.NoTablesText);
    }

    [Fact]
    public async Task TheChips_SurviveRefreshAndLeaving()
    {
        var repo = Repo(Table(1, "A", Mb, 0));
        var model = Connected(repo);
        await model.OnNavigatedToAsync();
        Tick(model, "Good");
        Tick(model, "Poor");

        await model.RefreshAsync();
        Assert.Equal(["Good", "Poor"], model.Health.Items.Where(i => i.IsChecked).Select(i => i.Value));

        var saved = model.SaveState()!;
        var back = Connected(repo);
        back.RestoreState(saved);

        Assert.Equal(["Good", "Poor"], back.Health.Items.Where(i => i.IsChecked).Select(i => i.Value));
        Assert.Equal(4, back.Health.Items.Count);   // the others are still there to tick
        Assert.True(back.HasTableFilters);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "It didn't happen in time.");
    }
}
