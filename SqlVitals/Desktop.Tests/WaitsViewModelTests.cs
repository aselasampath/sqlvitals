using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Waits' view model on its own (#188): no page, no STA thread, a fake repository. It reads the top
/// waits and the per-type totals, trends the rates of the types ticked only while resumed, keeps the
/// ticks across a refresh, reads a past range from the history, says what it couldn't read once,
/// and stops reading when the page is left.
/// </summary>
public sealed class WaitsViewModelTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeNavigator _navigator = new();

    private static TopWaitType Top(int rank, string type, string category, long ms, double pct) =>
        new(rank, type, category, ms / 7, ms, ms / 50, ms / 9, ms - ms / 9, 11, ms / 1000.0, 7, pct, pct, DateTime.Now, "#000", "");

    private static readonly TopWaitType[] TopWaits =
    [
        Top(1, "PAGEIOLATCH_SH", "I/O", 4_120_000, 38.2), Top(2, "WRITELOG", "I/O", 1_510_000, 14.0),
        Top(3, "SOS_SCHEDULER_YIELD", "CPU", 1_330_000, 12.3),
    ];

    private static WaitStatCumulative Totals(string type, long ms, long signalMs = 0, long tasks = 0) =>
        new(type, "I/O", 0, tasks, ms, 0, signalMs, ms - signalMs, 1, 1, 1, ms / 1000.0, DateTime.Now, DateTime.Now.AddDays(-3), "#000", "");

    // Every read, the totals 10 s of waiting later: WRITELOG 1,000 ms/s, CXPACKET 50 ms/s.
    private static FakeRepository Repo(List<string>? reads = null)
    {
        var n = 0;
        return new FakeRepository
        {
            GetTopWaitTypesAsync    = () => { reads?.Add("top"); return Task.FromResult<IEnumerable<TopWaitType>>(TopWaits); },
            GetCumulativeWaitsAsync = () =>
            {
                reads?.Add("totals");
                var i = n++;
                return Task.FromResult<IEnumerable<WaitStatCumulative>>(
                    [Totals("WRITELOG", 800_000 + i * 10_000, 100_000 + i * 2_000, 900 + i * 10), Totals("CXPACKET", 300_000 + i * 500)]);
            },
        };
    }

    private (WaitsViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo, ConnectionHistory? history = null)
    {
        var model = new WaitsViewModel(repo, history, _navigator, _time);
        var shell = new FakeShellStatus();
        model.Connect("Waits", shell, new RefreshScheduler(_time, _ui));
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
    public async Task Loaded_ShowsTheTopWaits_AndListsTheWaitTypes_TheTop20Ticked()
    {
        var (model, shell) = Connected(Repo());
        Assert.Null(model.Verdict);   // nothing read yet

        await model.OnNavigatedToAsync();

        Assert.Equal(["PAGEIOLATCH_SH", "WRITELOG", "SOS_SCHEDULER_YIELD"], model.TopWaits.Select(w => w.WaitType));
        Assert.Equal("Top wait types since the server started", model.TopHeader);
        Assert.StartsWith("PAGEIOLATCH_SH 38", model.TopSummary);
        Assert.Equal(["WRITELOG", "CXPACKET"], model.WaitTypes.Select(w => w.WaitType));
        Assert.All(model.WaitTypes, w => Assert.True(w.IsChecked));
        Assert.Equal("2 / 2 selected", model.SelectionCount);
        Assert.Equal("Loaded 2 wait types. Select types, then press Resume (▶) in the top bar.", model.StatusLine);
        Assert.StartsWith("Most waiting is on I/O", model.Verdict!.Headline);
        Assert.Equal(["File I/O", "Start trending"], model.Verdict.Actions.Select(a => a.Text));
        Assert.True(model.ShowsNoData);   // one reading: no rate yet
        Assert.Equal("Read Load", shell.Reports[^1]);
    }

    [Fact]
    public async Task Paused_UntilResumed_ThenTheTimerReadsTheTotalsOnly_AsRates()
    {
        var reads = new List<string>();
        var (model, shell) = Connected(Repo(reads));
        await model.OnNavigatedToAsync();
        Assert.True(model.IsPaused);
        Assert.Equal("--", model.Countdown);
        Tick(10);
        Assert.Equal(["top", "totals"], reads);

        model.SetPaused(false);
        Assert.Equal("Context", shell.Reports[^1]);
        Assert.Equal("10s", model.Countdown);
        Tick(10);
        Tick(10);

        Assert.Equal(["top", "totals", "totals", "totals"], reads);
        // The first rate is over the 20 s since the page opened, paused for the first 10.
        Assert.Equal([500d, 1_000d], model.Chart.Lines.Single(l => l.Name == "WRITELOG").Points.Select(p => p.Value!.Value));
        Assert.Equal([25d, 50d], model.Chart.Lines.Single(l => l.Name == "CXPACKET").Points.Select(p => p.Value!.Value));
        Assert.Equal("ms / sec", model.Chart.ValueName);
        Assert.False(model.ShowsNoData);
        Assert.StartsWith("WRITELOG waits the most: ", model.Verdict!.Headline);

        // Another metric: the lines start again, in its unit.
        model.MetricIndex = 3;   // waiting tasks
        Assert.Same(TrendChart.Empty, model.Chart);
        Tick(10);
        Assert.Equal([1d], model.Chart.Lines.Single(l => l.Name == "WRITELOG").Points.Select(p => p.Value!.Value));
        Assert.Equal("tasks / sec", model.Chart.ValueName);

        // Another interval: the wait starts again.
        model.IntervalIndex = 0;   // 5 s
        Tick(5);
        Assert.Equal(6, reads.Count);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ARefresh_KeepsTheTicks()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.WaitTypes.Single(w => w.WaitType == "CXPACKET").IsChecked = false;

        await model.RefreshAsync();

        Assert.Equal(["WRITELOG"], model.AllWaitTypes.Where(w => w.IsChecked).Select(w => w.WaitType));
        Assert.Equal(["WRITELOG"], model.Chart.Lines.Select(l => l.Name));
        Assert.Equal("1 / 2 selected", model.SelectionCount);
    }

    [Fact]
    public async Task ClearAll_ThenTop20_AndTheSearch()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();

        model.ClearAllCommand.Execute(null);
        Assert.Equal("0 / 2 selected", model.SelectionCount);
        Assert.Same(TrendChart.Empty, model.Chart);
        Assert.Equal("Select wait types on the left, then press Resume (▶) in the top bar to begin trending", model.NoData);

        model.ChartTopWaitsCommand.Execute(null);
        Assert.Equal(2, model.Chart.Lines.Count);

        model.Search = "write";
        Assert.Equal(["WRITELOG"], model.WaitTypes.Select(w => w.WaitType));
    }

    [Fact]
    public async Task MoreThan12Ticked_ChartsTheFirst12_AndSaysSo()
    {
        var repo = Repo();
        repo.GetCumulativeWaitsAsync = () => Task.FromResult<IEnumerable<WaitStatCumulative>>(
            Enumerable.Range(1, 15).Select(i => Totals($"WAIT_{i:00}", 1_000 * i)).ToList());
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();

        Assert.Equal(WaitsViewModel.MaxSeries, model.Chart.Lines.Count);
        Assert.Equal("⚠  Max 12 series — first 12 shown", model.MaxNote);
    }

    [Fact]
    public async Task NothingWaited_SaysSo()
    {
        var (model, _) = Connected(new FakeRepository
        {
            GetTopWaitTypesAsync    = () => Task.FromResult<IEnumerable<TopWaitType>>([]),
            GetCumulativeWaitsAsync = () => Task.FromResult<IEnumerable<WaitStatCumulative>>([]),
        });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.TopWaits);
        Assert.Equal("No waits worth noting", model.TopSummary);
        Assert.Equal("0 / 0 selected", model.SelectionCount);
        Assert.True(model.ShowsNoData);
    }

    [Fact]
    public async Task TheTotalsUnread_SaysSoOnce()
    {
        var repo = Repo();
        repo.GetCumulativeWaitsAsync = () => Task.FromException<IEnumerable<WaitStatCumulative>>(new InvalidOperationException("Timeout expired."));
        var (model, shell) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal((VerdictLevel.Unknown, "Couldn't read the wait statistics"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.Equal("Error: Timeout expired.", model.StatusLine);
        Assert.Equal(["Error Load: Timeout expired."], shell.Reports.Where(r => r.StartsWith("Error", StringComparison.Ordinal)));
        // The table that was read still shows.
        Assert.Equal(3, model.TopWaits.Count);
    }

    [Fact]
    public async Task TheTopWaitsUnread_TheTrendStillReads()
    {
        var repo = Repo();
        repo.GetTopWaitTypesAsync = () => Task.FromException<IEnumerable<TopWaitType>>(new InvalidOperationException("Timeout expired."));
        var (model, shell) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal("Couldn't read the wait statistics", model.Verdict!.Headline);
        Assert.Equal(2, model.AllWaitTypes.Count);
        Assert.Equal("Not read yet", model.TopSummary);
        Assert.Equal("Error Load: Timeout expired.", shell.Reports[^1]);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<IEnumerable<WaitStatCumulative>>();
        var repo = Repo();
        repo.GetCumulativeWaitsAsync = () => reading.Task;
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetCumulativeWaitsAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.StartsWith("Most waiting is on I/O", model.Verdict!.Headline);   // the table read before
    }

    [Fact]
    public async Task APastRange_ReadsItsTopWaitsAndLines_FromTheHistory_AndLiveComesBack()
    {
        var now     = DateTime.UtcNow;
        var history = new FakeHistoryReader
        {
            Waits =
            [
                new(now.AddMinutes(-40), 300, new Dictionary<string, WaitTypeTotals> { ["LCK_M_X"] = new("LCK_M_X", 4, 9_000, 0), ["WRITELOG"] = new("WRITELOG", 90, 1_500, 100) }),
                new(now.AddMinutes(-20), 300, new Dictionary<string, WaitTypeTotals> { ["LCK_M_X"] = new("LCK_M_X", 2, 6_000, 0) }),
            ],
        };
        var (model, shell) = Connected(Repo(), new ConnectionHistory(history, Guid.NewGuid()));
        await model.OnNavigatedToAsync();
        model.SetPaused(false);

        model.PickRange(HistoryRange.LastHour);
        Assert.True(model.IsPaused);   // a past range doesn't trend
        Assert.False(model.IsLive);
        await Until(() => model.TopWaits.Count == 2);

        Assert.Equal(["LCK_M_X", "WRITELOG"], model.TopWaits.Select(w => w.WaitType));
        Assert.Equal("Top wait types over the last hour, from the monitoring history", model.TopHeader);
        // LCK_M_X wasn't in the live list: added. WRITELOG, ticked, was recorded, so the ticks stay.
        Assert.Contains(model.AllWaitTypes, w => w is { WaitType: "LCK_M_X", IsChecked: false });
        Assert.Equal(5d, model.Chart.Lines.Single(l => l.Name == "WRITELOG").Points.Max(p => p.Value ?? 0));   // 1,500 ms over 300 s

        // Top 20 ticks the range's top waits.
        model.ChartTopWaitsCommand.Execute(null);
        Assert.Equal(30d, model.Chart.Lines.Single(l => l.Name == "LCK_M_X").Points.Max(p => p.Value ?? 0));   // 9,000 ms over 300 s
        Assert.DoesNotContain(model.Chart.Lines, l => l.Name == "CXPACKET");
        Assert.NotNull(model.Chart.From);
        Assert.NotNull(model.DataTo);
        Assert.Contains("Context", shell.Reports);

        model.PickRange(HistoryRange.Live);
        Assert.Equal(3, model.TopWaits.Count);
        Assert.Equal("Top wait types since the server started", model.TopHeader);
        Assert.Null(model.DataTo);
    }

    [Fact]
    public async Task APastRangeWithNothingRecorded_SaysWhatIsRecorded()
    {
        var (model, _) = Connected(Repo(), new ConnectionHistory(new FakeHistoryReader(), Guid.NewGuid()));
        await model.OnNavigatedToAsync();

        model.PickRange(HistoryRange.LastHour);
        await Until(() => model.StatusLine == "No history in this range");

        Assert.True(model.ShowsNoData);
        Assert.Contains("Waits per type are recorded every few minutes", model.NoData);
    }

    [Fact]
    public async Task TheVerdictsActions_ChartTheTop20_AndStartTheTrend()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.ClearAllCommand.Execute(null);

        await model.ActCommand.ExecuteAsync(new VerdictAction(WaitTrendVerdict.TopWaits, "Top 20"));
        await model.ActCommand.ExecuteAsync(new VerdictAction(WaitsVerdict.StartTrend, "Start trending"));

        Assert.Equal(2, model.AllWaitTypes.Count(w => w.IsChecked));
        Assert.False(model.IsPaused);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ARangePickedWhileAnotherReads_DropsTheOneBefore()
    {
        // Each history read waits for its gate, so the test sees what is shown between them.
        var gates   = new[] { new SemaphoreSlim(0), new SemaphoreSlim(0) };
        var started = 0;
        var now     = DateTime.UtcNow;
        var history = new FakeHistoryReader
        {
            Waits =
            [
                new(now.AddDays(-3), 300, new Dictionary<string, WaitTypeTotals> { ["LCK_M_X"] = new("LCK_M_X", 4, 9_000, 0) }),
                new(now.AddMinutes(-20), 300, new Dictionary<string, WaitTypeTotals> { ["WRITELOG"] = new("WRITELOG", 90, 1_500, 100) }),
            ],
            Reading = () => gates[Interlocked.Increment(ref started) - 1].Wait(TimeSpan.FromSeconds(10)),
        };
        var (model, _) = Connected(Repo(), new ConnectionHistory(history, Guid.NewGuid()));
        await model.OnNavigatedToAsync();

        model.PickRange(HistoryRange.Last7Days);
        await Until(() => Volatile.Read(ref started) == 1);
        model.PickRange(HistoryRange.LastHour);
        gates[0].Release();   // the 7 days' read ends after Last hour was picked
        await Until(() => Volatile.Read(ref started) == 2);

        // Dropped: the last hour isn't shown with the 7 days' waits.
        Assert.Empty(model.TopWaits);
        Assert.Equal("Top wait types over the last hour, from the monitoring history", model.TopHeader);

        gates[1].Release();
        await Until(() => model.TopWaits.Count == 1);
        Assert.Equal("WRITELOG", model.TopWaits[0].WaitType);
    }

    [Fact]
    public async Task APastRange_CantBeResumed_UntilLive()
    {
        var (model, _) = Connected(Repo(), new ConnectionHistory(new FakeHistoryReader(), Guid.NewGuid()));
        await model.OnNavigatedToAsync();
        model.PickRange(HistoryRange.LastHour);
        await Until(() => model.StatusLine == "No history in this range");

        model.SetPaused(false);
        await model.ActCommand.ExecuteAsync(new VerdictAction(WaitsVerdict.StartTrend, "Start trending"));
        Assert.True(model.IsPaused);

        model.PickRange(HistoryRange.Live);
        model.SetPaused(false);
        Assert.False(model.IsPaused);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ResumingWhileTrending_KeepsTheWaitForTheNextRead()
    {
        var reads = new List<string>();
        var (model, _) = Connected(Repo(reads));
        await model.OnNavigatedToAsync();
        model.SetPaused(false);
        Tick(6);

        model.SetPaused(false);   // Resume again, or the verdict's Start trending
        Tick(4);

        Assert.Equal(["top", "totals", "totals"], reads);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AReadingOnTheSameLines_MovesThem_WithoutBuildingTheChartAgain()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.RefreshAsync();   // the first rates: the lines are drawn
        var chart   = model.Chart;
        var sampled = 0;
        model.Sampled += () => sampled++;

        await model.RefreshAsync();

        Assert.Same(chart, model.Chart);
        Assert.Equal(1, sampled);
        Assert.Equal(2, chart.Lines[0].Points.Count);

        // Another tick: the chart is built again.
        model.WaitTypes[1].IsChecked = false;
        Assert.NotSame(chart, model.Chart);
    }
}
