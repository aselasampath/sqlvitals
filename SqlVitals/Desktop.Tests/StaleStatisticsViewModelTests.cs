using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Stale Stats' view model on its own (#184): no page, no STA thread, a fake repository and
/// dialogs. Besides the lifecycle, it owns the UPDATE STATISTICS script: the rows selected or all,
/// in the grid's order, with the sampling chosen.
/// </summary>
public sealed class StaleStatisticsViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 30, 0);

    private static StaleStatistic Stat(string table, string name, long rows, long changes, int days = 9) =>
        new("dbo", table, name, Now.AddDays(-days), days, rows, rows / 4, 25, changes, false, false, false, "SalesDb", Now);

    private static readonly StaleStatistic[] Stats =
    [
        Stat("Orders", "IX_Orders_Date", 48_000_000, 2_100_000),
        Stat("OrderLines", "_WA_Sys_00000003_2A4B4B5E", 900_000, 410_000, 31),
        Stat("Customers", "PK_Customers", 1_200_000, 12_000, 4),
    ];

    private readonly FakeDialogService _dialogs = new();
    private readonly FakeShellStatus _shell = new();

    private StaleStatisticsViewModel Connected(FakeRepository repo)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var model = new StaleStatisticsViewModel(repo, _dialogs, time);
        model.Connect("StaleStats", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    private static FakeRepository Reading(Func<IEnumerable<StaleStatistic>> stats) =>
        new() { GetStaleStatisticsAsync = () => Task.FromResult(stats()) };

    [Fact]
    public async Task Loaded_ListsThem_AndTheScriptIsForAllOfThem()
    {
        var model = Connected(Reading(() => Stats));
        Assert.False(model.GenerateScriptCommand.CanExecute(null));

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Equal(Stats, model.Statistics);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);
        Assert.Equal("Generate UPDATE STATISTICS Script (all)", model.ScriptText);
        Assert.True(model.GenerateScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task Empty_SaysNoneHasChangesPending_AndOffersNoScript()
    {
        var model = Connected(Reading(() => []));

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Statistics);
        Assert.Equal("No statistic has changes pending", model.Verdict!.Headline);
        Assert.False(model.GenerateScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var model = Connected(new FakeRepository
        {
            GetStaleStatisticsAsync = () => Task.FromException<IEnumerable<StaleStatistic>>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], _shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the statistics", "Error: Timeout expired."), model.Verdict);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<IEnumerable<StaleStatistic>>();
        var repo    = new FakeRepository { GetStaleStatisticsAsync = () => reading.Task };
        var model   = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetStaleStatisticsAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(_shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task TheStatisticSelected_IsKeptAcrossARefresh_ByItsDatabaseTableAndName()
    {
        // Each read is new rows: the same statistics, with more changes.
        var reads = 0;
        var model = Connected(Reading(() => { reads++; return Stats.Select(s => s with { ModificationsSinceLastUpdate = s.ModificationsSinceLastUpdate + reads }); }));
        await model.OnNavigatedToAsync();
        // As the grid does: its row, and the rows selected, which are that one.
        model.SelectedStatistic  = model.Statistics.Single(s => s.TableName == "Customers");
        model.SelectedStatistics = [model.SelectedStatistic];

        await model.RefreshAsync();

        Assert.Equal("Customers", model.SelectedStatistic!.TableName);
        Assert.Contains(model.SelectedStatistic, model.Statistics);
        Assert.Equal([model.SelectedStatistic], model.SelectedStatistics);
        Assert.Equal("Generate UPDATE STATISTICS Script (1 selected)", model.ScriptText);
    }

    [Fact]
    public async Task EveryStatisticSelected_IsKeptAcrossARefresh_SoTheScriptStaysTheirs()
    {
        var reads = 0;
        var model = Connected(Reading(() => { reads++; return Stats.Select(s => s with { ModificationsSinceLastUpdate = s.ModificationsSinceLastUpdate + reads }); }));
        await model.OnNavigatedToAsync();
        model.SelectedStatistic  = model.Statistics[0];
        model.SelectedStatistics = [model.Statistics[0], model.Statistics[2]];

        await model.RefreshAsync();

        Assert.Equal(["Orders", "Customers"], model.SelectedStatistics.Select(s => s.TableName));
        Assert.All(model.SelectedStatistics, s => Assert.Contains(s, model.Statistics));
        Assert.Equal("Generate UPDATE STATISTICS Script (2 selected)", model.ScriptText);
    }

    [Fact]
    public async Task ShowIt_SelectsTheMostChanged_AndAsksTheViewToShowIt()
    {
        var model = Connected(Reading(() => Stats));
        await model.OnNavigatedToAsync();
        object? shown = null;
        model.ShowRow += row => shown = row;

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == StaleStatsVerdict.ShowWorst));

        Assert.Equal("OrderLines", model.SelectedStatistic!.TableName);
        Assert.Same(model.SelectedStatistic, shown);
        Assert.Equal("Generate UPDATE STATISTICS Script (1 selected)", model.ScriptText);
    }

    [Fact]
    public async Task TheScript_IsForTheRowsSelected_InTheGridsOrder_AndOnlyShown()
    {
        var model = Connected(Reading(() => Stats));
        await model.OnNavigatedToAsync();
        model.SelectedStatistics = [Stats[0], Stats[2]];

        // The grid sorted by table: Customers, OrderLines, Orders.
        model.GenerateScriptCommand.Execute(Stats.OrderBy(s => s.TableName).ToList());

        var script = Assert.Single(_dialogs.Scripts);
        Assert.Equal(["Script: Update Statistics (modal)"], _dialogs.Shown);
        Assert.Equal("UpdateStatistics_20261013_0930.sql", script.FileName);
        Assert.StartsWith("Review before running. 2 statistic(s) on 2 table(s) will be updated with default sampling.", script.Notice);
        Assert.True(script.Text.IndexOf("[Customers]", StringComparison.Ordinal) < script.Text.IndexOf("[Orders]", StringComparison.Ordinal));
        Assert.DoesNotContain("OrderLines", script.Text);
        Assert.DoesNotContain("WITH FULLSCAN", script.Text);
    }

    [Fact]
    public async Task TheScript_WithNothingSelected_IsForAll_AndFullScanReadsEveryRow()
    {
        var model = Connected(Reading(() => Stats));
        await model.OnNavigatedToAsync();
        model.FullScan = true;

        model.GenerateScriptCommand.Execute(null);

        var script = Assert.Single(_dialogs.Scripts);
        Assert.Contains("3 statistic(s) on 3 table(s) will be updated WITH FULLSCAN, reading about 50,100,000 rows", script.Notice);
        Assert.Contains("WITH FULLSCAN", script.Text);
    }
}
