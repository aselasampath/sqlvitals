using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// TempDB's view model on its own (#184): no page, no STA thread, a fake repository. It reads
/// through its lifecycle, says what it couldn't read once, stops reading when the page is left,
/// and keeps the session selected across a refresh.
/// </summary>
public sealed class TempDbViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 30, 0);

    private static TempDbSession Session(int id, long internalKb, long userKb = 0) =>
        new(id, userKb, 0, internalKb, 0, internalKb + userKb, "etl", "APP-ETL01", "Loader", null, "running", 10, "SalesDb", null, Now, "Info");

    private static (IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>) Pressure(params TempDbSession[] sessions) =>
        ([new("tempdev", "ROWS", @"T:\tempdb.mdf", 8_192, 2_100, 6_092, 25.6, "64 MB", Now),
          new("templog", "LOG", @"T:\templog.ldf", 2_048, 300, 1_748, 14.6, "64 MB", Now)],
         sessions,
         [new("Version Store Size (KB)", 12_288, Now), new("Free Space in tempdb (KB)", 900_000, Now), new("Active Temp Tables", 42, Now)]);

    private static (TempDbViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo)
    {
        var model = new TempDbViewModel(repo);
        var shell = new FakeShellStatus();
        model.Connect("TempDb", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell);
    }

    [Fact]
    public async Task Loaded_ShowsTheSessionsMostFirst_TheCountersAsCards_AndTheFoldedLines()
    {
        var (model, shell) = Connected(new FakeRepository
        {
            GetTempDbPressureAsync = () => Task.FromResult(Pressure(Session(91, 4_096), Session(88, 870_400, 20_480))),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal([88, 91], model.Sessions.Select(s => s.SessionId));
        Assert.Equal(2, model.Files.Count);
        Assert.Equal([TempDbViewModel.CounterTone.Danger, TempDbViewModel.CounterTone.Warning, TempDbViewModel.CounterTone.Accent],
                     model.Counters.Select(c => c.Tone));
        Assert.Equal(("12,288", "Version Store Size (KB)"), (model.Counters[0].Value, model.Counters[0].Label));
        Assert.Equal("Version Store Size (KB) 12,288 · Free Space in tempdb (KB) 900,000 · Active Temp Tables 42", model.CountersSummary);
        Assert.Equal("2 files · 2,400 MB used of 10,240 MB", model.FilesSummary);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);
        Assert.Null(model.Status);
    }

    [Fact]
    public async Task Empty_SaysNothingWasRead_AndShowsNoSession()
    {
        var (model, _) = Connected(new FakeRepository
        {
            GetTempDbPressureAsync = () => Task.FromResult<(IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>)>(([], [], [])),
        });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Sessions);
        Assert.Empty(model.Counters);
        Assert.Equal("No TempDB counters were read", model.CountersSummary);
        Assert.Equal("No TempDB files were read", model.FilesSummary);
        Assert.Null(model.SelectedSession);
        Assert.NotNull(model.Verdict);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell) = Connected(new FakeRepository
        {
            GetTempDbPressureAsync = () => Task.FromException<(IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>)>(
                new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("TempDB", "Error: Timeout expired."), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<(IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>)>();
        var repo = new FakeRepository { GetTempDbPressureAsync = () => reading.Task };
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetTempDbPressureAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
        Assert.Empty(model.Sessions);
    }

    [Fact]
    public async Task TheSessionSelected_IsKeptAcrossARefresh_ByItsId()
    {
        var reads = 0;
        var (model, _) = Connected(new FakeRepository
        {
            // Each read is new rows: the same sessions, holding more.
            GetTempDbPressureAsync = () => Task.FromResult(Pressure(Session(88, 870_400 + ++reads), Session(91, 4_096))),
        });
        await model.OnNavigatedToAsync();
        model.SelectedSession = model.Sessions.Single(s => s.SessionId == 91);
        var rows = model.Sessions;

        await model.RefreshAsync();

        Assert.Same(rows, model.Sessions);
        Assert.Equal(91, model.SelectedSession!.SessionId);
        Assert.Contains(model.SelectedSession, model.Sessions);
    }

    [Fact]
    public async Task ShowTheSession_SelectsTheOneHoldingTheMost_AndAsksTheViewToShowIt()
    {
        var (model, _) = Connected(new FakeRepository
        {
            GetTempDbPressureAsync = () => Task.FromResult(Pressure(Session(91, 4_096), Session(88, 870_400))),
        });
        await model.OnNavigatedToAsync();
        object? shown = null;
        model.ShowRow += row => shown = row;

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == TempDbVerdict.ShowSession));

        Assert.Equal(88, model.SelectedSession!.SessionId);
        Assert.Same(model.SelectedSession, shown);
    }

    [Fact]
    public async Task NoFilesRead_ClearsTheChart_RatherThanShowTheLastRead()
    {
        var reads = 0;
        var (model, _) = Connected(new FakeRepository
        {
            GetTempDbPressureAsync = () => Task.FromResult(++reads == 1
                ? Pressure(Session(88, 1_000))
                : ([], [Session(88, 1_000)], [])),
        });
        await model.OnNavigatedToAsync();
        Assert.Equal(2, model.Files.Count);

        await model.RefreshAsync();

        Assert.Empty(model.Files);
        Assert.Equal("No TempDB files were read", model.FilesSummary);
    }
}
