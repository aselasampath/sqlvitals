using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Perfmon's view model on its own (#188): no page, no STA thread, a fake repository. It lists the
/// counters some group has, ticks a group's, charts rate counters as rates, holds at 12, reads
/// while resumed, keeps the ticks across a refresh, reads a past range from the history, says what
/// it couldn't read, and stops reading when the page is left.
/// </summary>
public sealed class PerfmonViewModelTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeUiDispatcher _ui = new();

    private static PerfmonCounter Counter(string name, long value, bool rate = false) =>
        new("SQLServer:SQL Statistics", name, "", value, rate ? LiveMetricsRepository.PerfmonRateCounterType : 65792, DateTime.Now);

    // Every read: 1,000 more batches (100/s over 10 s), 3 grants pending, and one counter no group has.
    private static FakeRepository Repo(List<string>? reads = null)
    {
        var n = 0;
        return new FakeRepository
        {
            GetPerfmonCountersAsync = () =>
            {
                reads?.Add("counters");
                var i = n++;
                return Task.FromResult<IEnumerable<PerfmonCounter>>(
                [
                    Counter("Batch Requests/sec", 1_200_000 + i * 1_000, rate: true),
                    Counter("SQL Compilations/sec", 40_000 + i * 50, rate: true),
                    Counter("Memory Grants Pending", 3),
                    Counter("Target Server Memory (KB)", 28_000_000),
                    Counter("Some Other Counter", 7),
                ]);
            },
        };
    }

    private (PerfmonViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo, ConnectionHistory? history = null)
    {
        var model = new PerfmonViewModel(repo, history, new FakeNavigator(), _time);
        var shell = new FakeShellStatus();
        model.Connect("Perfmon", shell, new RefreshScheduler(_time, _ui));
        return (model, shell);
    }

    private void Tick(int seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        _ui.RunPosted();
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
            await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task Loaded_ListsTheGroupsCounters_TheFirstGroupTicked()
    {
        var (model, shell) = Connected(Repo());
        Assert.Equal("Not charting yet", model.Verdict!.Headline);
        Assert.Equal("Select counters from the left panel to begin charting.", model.NoData);

        await model.OnNavigatedToAsync();

        // Only counters a group has; sorted.
        Assert.Equal(["Batch Requests/sec", "Memory Grants Pending", "SQL Compilations/sec", "Target Server Memory (KB)"],
                     model.Counters.Select(c => c.CounterName));
        Assert.Equal(["Batch Requests/sec", "SQL Compilations/sec"], model.Chart.Lines.Select(l => l.Name));
        Assert.Equal("General Throughput", model.Group);
        Assert.False(model.ShowsNoData);
        Assert.Matches(@"^Last refresh: \d\d:\d\d:\d\d  •  5 counters retrieved$", model.StatusLine);
        Assert.Equal("Read Load", shell.Reports[^1]);
    }

    [Fact]
    public async Task Resumed_TheTimerReads_RateCountersAsRates()
    {
        var reads = new List<string>();
        var (model, _) = Connected(Repo(reads));
        await model.OnNavigatedToAsync();
        Tick(10);
        Assert.Single(reads);   // paused until Resume

        model.SetPaused(false);
        Tick(10);

        Assert.Equal(2, reads.Count);
        // The first reading has no rate yet: 0; then 1,000 batches over the 20 s since.
        Assert.Equal([0d, 50d], model.Chart.Lines.Single(l => l.Name == "Batch Requests/sec").Points.Select(p => p.Value!.Value));
        Assert.Equal("10s", model.Countdown);

        model.IntervalIndex = 0;   // 5 s
        Tick(5);
        Assert.Equal(3, reads.Count);
        Assert.Equal(["5s", "10s", "15s"], model.IntervalNames.Take(3));
        Assert.Equal("1m 00s", model.IntervalNames[11]);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AGroup_TicksItsCounters_AndJudgesThem()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.Search = "batch";

        model.Group = "Memory Pressure";

        Assert.Equal("", model.Search);
        Assert.Equal(["Memory Grants Pending", "Target Server Memory (KB)"], model.Chart.Lines.Select(l => l.Name));
        await model.RefreshAsync();
        Assert.Equal((VerdictLevel.Warning, "Memory Grants Pending is 3: queries wait for a memory grant"), (model.Verdict!.Level, model.Verdict.Headline));
    }

    [Fact]
    public async Task ARefresh_KeepsTheTicks()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.Counters.Single(c => c.CounterName == "Batch Requests/sec").IsChecked = false;

        await model.RefreshAsync();

        Assert.Equal(["SQL Compilations/sec"], model.Chart.Lines.Select(l => l.Name));
    }

    [Fact]
    public async Task ClearAll_ThenSelectAll_AndThe13thTickIsTakenBack()
    {
        var repo = new FakeRepository
        {
            GetPerfmonCountersAsync = () => Task.FromResult<IEnumerable<PerfmonCounter>>(
                new[] { "Lazy writes/sec", "Page reads/sec", "Page writes/sec", "Checkpoint pages/sec", "Page lookups/sec", "Readahead pages/sec",
                 "Background writer pages/sec", "Log Flushes/sec", "Log Bytes Flushed/sec", "Log Flush Write Time (ms)", "Page IO latch waits",
                 "Log buffer waits", "Log write waits", "Full Scans/sec" }.Select(n => Counter(n, 1)).ToList()),
        };
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();

        model.ClearAllCommand.Execute(null);
        Assert.Empty(model.Chart.Lines);
        Assert.True(model.ShowsNoData);

        model.SelectAllCommand.Execute(null);
        Assert.Equal(PerfmonViewModel.MaxSeries, model.Chart.Lines.Count);

        var thirteenth = model.Counters.First(c => !c.IsChecked);
        thirteenth.IsChecked = true;
        Assert.False(thirteenth.IsChecked);
        Assert.Equal("Maximum 12 counters — deselect one first.", model.StatusLine);
    }

    [Fact]
    public async Task NoCounterAGroupHas_SaysWhatToDo()
    {
        var (model, _) = Connected(new FakeRepository
        {
            GetPerfmonCountersAsync = () => Task.FromResult<IEnumerable<PerfmonCounter>>([Counter("Some Other Counter", 7)]),
        });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Counters);
        Assert.True(model.ShowsNoData);
        Assert.Equal("Select counters from the left panel to begin charting.", model.NoData);
    }

    [Fact]
    public async Task AFailedFirstRead_SaysSoInTheVerdict_ALaterOneOnlyInTheTopBar()
    {
        var repo = Repo();
        var read = repo.GetPerfmonCountersAsync!;
        repo.GetPerfmonCountersAsync = () => Task.FromException<IEnumerable<PerfmonCounter>>(new InvalidOperationException("Timeout expired."));
        var (model, shell) = Connected(repo);

        await model.OnNavigatedToAsync();
        Assert.Equal((VerdictLevel.Unknown, "Couldn't read the performance counters"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.Equal("Error Load: Timeout expired.", shell.Reports[^1]);

        repo.GetPerfmonCountersAsync = read;
        await model.RefreshAsync();
        var verdict = model.Verdict;
        repo.GetPerfmonCountersAsync = () => Task.FromException<IEnumerable<PerfmonCounter>>(new InvalidOperationException("Timeout expired."));
        await model.RefreshAsync();

        Assert.Equal(verdict, model.Verdict);   // the lines so far stay drawn, and judged
        Assert.Equal("Error Refresh: Timeout expired.", shell.Reports[^1]);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<IEnumerable<PerfmonCounter>>();
        var repo = new FakeRepository { GetPerfmonCountersAsync = () => reading.Task };
        var (model, shell) = Connected(repo);
        var before = model.Verdict;

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetPerfmonCountersAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Equal(before, model.Verdict);
    }

    [Fact]
    public async Task APastRange_ChartsTheRecordedCounters_AndPausesTheLiveReads()
    {
        var now     = DateTime.UtcNow;
        var history = new FakeHistoryReader
        {
            Counters =
            [
                new(now.AddMinutes(-40), new Dictionary<string, double> { ["Batch Requests/sec"] = 120, ["Memory Grants Pending"] = 0 }),
                new(now.AddMinutes(-20), new Dictionary<string, double> { ["Batch Requests/sec"] = 180 }),
            ],
        };
        var (model, shell) = Connected(new FakeRepository(), new ConnectionHistory(history, Guid.NewGuid()));
        Assert.True(model.HistoryAvailable);

        model.PickRange(HistoryRange.LastHour);
        Assert.False(model.IsLive);
        await Until(() => model.Chart.Lines.Count > 0);

        // Opened on a past range: the counters recorded are offered.
        Assert.Equal(["Batch Requests/sec", "Memory Grants Pending"], model.Counters.Select(c => c.CounterName));
        Assert.Equal([120d, 180d], model.Chart.Lines.Single(l => l.Name == "Batch Requests/sec").Points.Where(p => p.Value is not null).Select(p => p.Value!.Value));
        Assert.NotNull(model.Chart.To);
        Assert.NotNull(model.DataTo);
        Assert.Contains("Context", shell.Reports);

        model.PickRange(HistoryRange.Live);
        Assert.True(model.IsLive);
        Assert.Null(model.DataTo);
        Assert.Equal("Live: Resume (▶) in the top bar starts reading, Refresh reads once.", model.StatusLine);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task APastRangeWithNothingRecorded_SaysWhatIsRecorded()
    {
        var (model, _) = Connected(Repo(), new ConnectionHistory(new FakeHistoryReader(), Guid.NewGuid()));
        await model.OnNavigatedToAsync();

        model.PickRange(HistoryRange.LastHour);
        await Until(() => model.StatusLine == "No history in this range");

        Assert.True(model.ShowsNoData);
        Assert.Contains("Perfmon counters are recorded with each history snapshot", model.NoData);
    }

    [Fact]
    public async Task AGroupPickedBeforeTheFirstRead_IsTheOneTicked()
    {
        var (model, _) = Connected(Repo());
        model.Group = "Memory Pressure";

        await model.OnNavigatedToAsync();

        Assert.Equal("Memory Pressure", model.Group);
        Assert.Equal(["Memory Grants Pending", "Target Server Memory (KB)"], model.Chart.Lines.Select(l => l.Name));
    }

    [Fact]
    public async Task APastRange_CantBeResumed_UntilLive()
    {
        var (model, _) = Connected(Repo(), new ConnectionHistory(new FakeHistoryReader(), Guid.NewGuid()));
        model.PickRange(HistoryRange.LastHour);
        await Until(() => model.StatusLine == "No history in this range");

        model.SetPaused(false);
        Assert.True(model.IsPaused);

        model.PickRange(HistoryRange.Live);
        model.SetPaused(false);
        Assert.False(model.IsPaused);
        model.OnNavigatedFrom();
    }
}
