using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The page lifecycle of <see cref="PageViewModel"/> (#183): one read at a time, cancelled when the
/// page is left, a failure reported once, and the timed refresh paused and resumed on a stepped clock.
/// </summary>
public sealed class PageLifecycleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly FakeShellStatus _shell = new();

    /// <summary>A page whose reads the test runs: each waits for <see cref="Gate"/> unless it's null, then does <see cref="Read"/>.</summary>
    private sealed class TestViewModel : PageViewModel
    {
        public int Loads;
        public CancellationToken LastToken;
        public TaskCompletionSource? Gate;
        public bool ListensToToken = true;
        public Action Read = () => { };

        public new TimeSpan? RefreshInterval
        {
            get => base.RefreshInterval;
            set => base.RefreshInterval = value;
        }

        public void ShowCountdown() => ShowsCountdown = true;

        public IReading ShowPart(string doing, Action stop) => ShowReading(doing, doing + " read", stop);

        protected override async Task LoadAsync(CancellationToken cancel)
        {
            Loads++;
            LastToken = cancel;
            if (Gate is { } gate)
                await (ListensToToken ? gate.Task.WaitAsync(cancel) : gate.Task);
            Read();
        }
    }

    private TestViewModel Connected(IRefreshScheduler? scheduler = null, PageLifetime? lifetime = null)
    {
        var model = new TestViewModel();
        model.Connect("Test", _shell, scheduler ?? new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()), lifetime);
        return model;
    }

    // ── One read at a time ───────────────────────────────────────────────

    [Fact]
    public async Task Refresh_WhileReading_WaitsForThatRead_InsteadOfStartingAnother()
    {
        var model = Connected();
        model.Gate = new TaskCompletionSource();

        var first  = model.RefreshAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.RefreshCommand.CanExecute(null));
        // The loading bar says it is refreshing (#117), not the page.
        Assert.Null(model.Status);

        var second = model.RefreshAsync();
        var third  = model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, model.Loads);

        model.Gate.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(1, model.Loads);
        Assert.False(model.IsBusy);
        Assert.True(model.RefreshCommand.CanExecute(null));
        Assert.Null(model.Status);
        Assert.Equal(["Read Refresh"], _shell.Reports);

        // Once it's done, the next one reads again.
        await model.RefreshAsync();
        Assert.Equal(2, model.Loads);
    }

    [Fact]
    public async Task Opening_Loads_AndSaysSo()
    {
        var model = Connected();
        model.Gate = new TaskCompletionSource();

        var opening = model.OnNavigatedToAsync();
        Assert.Null(model.Status);
        Assert.Equal(["Load"], _shell.Readings);
        model.Gate.SetResult();
        await opening;

        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Equal(["Load", "Load done"], _shell.Readings);
        Assert.Same(model, _shell.From.Single());
        Assert.Equal("Test", model.Tag);
    }

    // ── Cancel on leave ──────────────────────────────────────────────────

    [Fact]
    public async Task Leaving_CancelsTheRead_AndNothingIsReported()
    {
        var model = Connected();
        model.Gate = new TaskCompletionSource();

        var opening = model.OnNavigatedToAsync();
        model.OnNavigatedFrom();
        await opening;

        Assert.True(model.LastToken.IsCancellationRequested);
        Assert.False(model.IsBusy);
        Assert.Empty(_shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task AReadThatCantBeCancelled_FailingAfterTheLeave_IsNotShown()
    {
        var model = Connected();
        // A code-behind read: it doesn't listen to the token (#167).
        model.Gate = new TaskCompletionSource();
        model.ListensToToken = false;
        model.Read = () => throw new InvalidOperationException("Timeout expired.");

        var opening = model.OnNavigatedToAsync();
        model.OnNavigatedFrom();
        model.Gate.SetResult();
        await opening;

        Assert.Empty(_shell.Reports);
        Assert.Null(model.Verdict);
        Assert.False(model.IsBusy);
    }

    // ── An error reported once ───────────────────────────────────────────

    [Fact]
    public async Task AFailedLoad_IsReportedOnce_ToTheShellAndInTheVerdict()
    {
        var model = Connected();
        model.Read = () => throw new InvalidOperationException("Timeout expired.");

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], _shell.Reports);
        var verdict = model.Verdict!;
        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.StartsWith("Couldn't read ", verdict.Headline);
        Assert.Equal("Error: Timeout expired. Refresh to try again.", verdict.Why);
        Assert.Equal("Error: Timeout expired.", model.Status);
        Assert.False(model.IsBusy);

        // Refresh reads again: it says so as a refresh, and a success clears the status.
        await model.RefreshAsync();
        Assert.Equal("Error Refresh: Timeout expired.", _shell.Reports[^1]);
        model.Read = () => { };
        await model.RefreshAsync();
        Assert.Equal("Read Refresh", _shell.Reports[^1]);
        Assert.Null(model.Status);
    }

    // ── The loading bar (#117) ───────────────────────────────────────────

    [Fact]
    public async Task UntilItHasRead_ItsGridsSayLoading_AndAFailureOrTheNextReadDoesnt()
    {
        var model = Connected();
        Assert.Equal(PageLoadState.Ready, model.LoadState);

        model.Gate = new TaskCompletionSource();
        var opening = model.OnNavigatedToAsync();
        Assert.Equal(PageLoadState.Loading, model.LoadState);
        model.Read = () => throw new InvalidOperationException("Timeout expired.");
        model.Gate.SetResult();
        await opening;
        // Failed: the verdict says why, and an empty grid says nothing more.
        Assert.Equal(PageLoadState.Ready, model.LoadState);

        // Refresh tries again: "Loading…" again meanwhile, as nothing has been read yet.
        model.Gate = new TaskCompletionSource();
        model.Read = () => { };
        var refreshing = model.RefreshAsync();
        Assert.Equal(PageLoadState.Loading, model.LoadState);
        model.Gate.SetResult();
        await refreshing;
        Assert.Equal(PageLoadState.Ready, model.LoadState);

        // Once read, a Refresh keeps what is on screen.
        model.Gate = new TaskCompletionSource();
        refreshing = model.RefreshAsync();
        Assert.Equal(PageLoadState.Ready, model.LoadState);
        model.Gate.SetResult();
        await refreshing;
    }

    [Fact]
    public async Task Stop_CancelsTheRead_AndEveryOtherOfThePage_KeepsWhatItShows_AndReportsNoError()
    {
        using var lifetime = new PageLifetime();
        var model = Connected(lifetime: lifetime);
        model.Read = () => model.Verdict = new PageVerdict(VerdictLevel.Ok, "Nothing wrong");
        await model.OnNavigatedToAsync();
        var verdict = model.Verdict;

        model.Gate = new TaskCompletionSource();
        var refreshing = model.RefreshAsync();
        var otherRead  = lifetime.Token;
        _shell.Stops[^1]!();
        await refreshing;

        Assert.True(model.LastToken.IsCancellationRequested);
        // A read of the page's own beside it (a tab, its code-behind's) stops too, and it can read again.
        Assert.True(otherRead.IsCancellationRequested);
        Assert.False(lifetime.Token.IsCancellationRequested);
        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Equal(["Load", "Load done", "Refresh", "Refresh ended"], _shell.Readings);
        Assert.Same(verdict, model.Verdict);
        Assert.Null(model.Status);
        Assert.False(model.IsBusy);
        Assert.Equal(PageLoadState.Ready, model.LoadState);

        // Refresh reads again.
        model.Gate = null;
        await model.RefreshAsync();
        Assert.Equal("Read Refresh", _shell.Reports[^1]);
    }

    [Fact]
    public async Task AFirstLoadStopped_HasReadNothing_UntilRefreshReads()
    {
        var model = Connected();
        model.Gate = new TaskCompletionSource();
        var opening = model.OnNavigatedToAsync();
        model.StopReading();
        await opening;

        Assert.Equal(PageLoadState.Stopped, model.LoadState);
        Assert.Null(model.Verdict);
        Assert.Empty(_shell.Reports);

        model.Gate = null;
        await model.RefreshAsync();
        Assert.Equal(PageLoadState.Ready, model.LoadState);
    }

    // Code review: Query Store's tab read swallows the cancellation and returns, which counted as read.
    [Fact]
    public async Task AReadThatReturns_AfterStop_IsStoppedAllTheSame()
    {
        var model = Connected();
        model.Gate = new TaskCompletionSource();
        model.ListensToToken = false;
        var opening = model.OnNavigatedToAsync();
        model.StopReading();
        model.Gate.SetResult();
        await opening;

        Assert.Empty(_shell.Reports);
        Assert.Equal(["Load", "Load ended"], _shell.Readings);
        Assert.Equal(PageLoadState.Stopped, model.LoadState);
    }

    // Code review: a failed first read left every timed read after it saying "Loading…" again.
    [Fact]
    public async Task AfterAFailedFirstRead_ATimedReadDoesntSayLoading()
    {
        var time  = new FakeTimeProviderAt();
        var ui    = new FakeUiDispatcher();
        var model = Connected(new RefreshScheduler(time, ui));
        model.RefreshInterval = Interval;
        model.Read = () => throw new InvalidOperationException("Timeout expired.");
        await model.OnNavigatedToAsync();
        Assert.Equal(PageLoadState.Ready, model.LoadState);

        model.Gate = new TaskCompletionSource();
        time.Advance(Interval);
        ui.RunPosted();
        Assert.True(model.IsBusy);
        Assert.Equal(PageLoadState.Ready, model.LoadState);
        model.Gate.SetResult();
        await WhenIdle(model);
    }

    [Fact]
    public async Task ATimedRead_ShowsNoBar_ItsCountdownSaysItIsReading_AndRefreshShowsTheBarFromThen()
    {
        var time  = new FakeTimeProviderAt();
        var ui    = new FakeUiDispatcher();
        var model = Connected(new RefreshScheduler(time, ui));
        model.RefreshInterval = Interval;
        model.ShowCountdown();
        await model.OnNavigatedToAsync();
        Assert.Equal("30s", model.Countdown);

        model.Gate = new TaskCompletionSource();
        time.Advance(Interval);
        ui.RunPosted();
        Assert.True(model.IsBusy);
        Assert.Equal(PageViewModel.ReadingNow, model.Countdown);
        Assert.Equal(["Load", "Load done"], _shell.Readings);

        // The countdown's own tick doesn't take ⟳ away while it reads.
        time.Advance(TimeSpan.FromSeconds(1));
        ui.RunPosted();
        Assert.Equal(PageViewModel.ReadingNow, model.Countdown);

        // Refresh waits for it, and the bar shows it from now on, Stop included.
        var refreshing = model.RefreshAsync();
        Assert.Equal(["Load", "Load done", "Refresh"], _shell.Readings);
        Assert.NotNull(_shell.Stops[^1]);
        model.Gate.SetResult();
        await refreshing;
        Assert.Equal(["Load", "Load done", "Refresh", "Refresh done"], _shell.Readings);
        Assert.Equal(["Read Load", "Read Refresh"], _shell.Reports);
        Assert.Equal("29s", model.Countdown);
    }

    [Fact]
    public async Task APartReadOnItsOwn_IsShownWithItsOwnWords_AndStopsWhatItIsGiven()
    {
        var model   = Connected();
        var stopped = false;
        using (var reading = model.ShowPart("Checking fragmentation", () => stopped = true))
        {
            Assert.Equal(["Checking fragmentation"], _shell.Readings);
            _shell.Stops[^1]!();
            Assert.True(stopped);
            reading.Done();
        }
        Assert.Equal(["Checking fragmentation", "Checking fragmentation done"], _shell.Readings);
    }

    // ── Timed refresh, paused and resumed ────────────────────────────────

    [Fact]
    public async Task TheTimer_ReadsEveryInterval_PausesAndResumes_AndStopsOnLeave()
    {
        var time  = new SteppingTime();
        var ui    = new FakeUiDispatcher();
        var model = Connected(new RefreshScheduler(time, ui));
        model.RefreshInterval = Interval;

        // Not before the page is shown.
        Assert.Equal(0, ui.Pending);
        await model.OnNavigatedToAsync();
        Assert.Equal(1, model.Loads);

        // SteppingTime moves the clock on by the interval at once: one tick.
        var start = time.GetUtcNow();
        await TickAsync(ui);
        Assert.Equal(2, model.Loads);
        Assert.Equal(Interval, time.GetUtcNow() - start);
        Assert.Equal(["Read Load", "Read Timed"], _shell.Reports);

        // Paused: however long, nothing.
        model.SetPaused(true);
        Assert.True(model.IsPaused);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, ui.Pending);
        Assert.Equal(2, model.Loads);

        // Refresh still reads while paused.
        await model.RefreshAsync();
        Assert.Equal(3, model.Loads);

        // Resumed: a whole interval later, not at once.
        var resumed = time.GetUtcNow();
        model.SetPaused(false);
        await TickAsync(ui);
        Assert.Equal(4, model.Loads);
        Assert.Equal(Interval, time.GetUtcNow() - resumed);

        // Left: the timer stops.
        model.OnNavigatedFrom();
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, ui.Pending);
        Assert.Equal(4, model.Loads);
    }

    [Fact]
    public async Task ATick_DueWhileARead_Runs_IsSkipped()
    {
        var time  = new FakeTimeProviderAt();
        var ui    = new FakeUiDispatcher();
        var model = Connected(new RefreshScheduler(time, ui));
        model.RefreshInterval = Interval;
        await model.OnNavigatedToAsync();

        model.Gate = new TaskCompletionSource();
        var refresh = model.RefreshAsync();
        time.Advance(Interval);
        ui.RunPosted();
        Assert.Equal(2, model.Loads);

        model.Gate.SetResult();
        await refresh;
        Assert.Equal(["Read Load", "Read Refresh"], _shell.Reports);
    }

    [Fact]
    public async Task ATimedFailure_IsReportedAsTimed()
    {
        var time  = new FakeTimeProviderAt();
        var ui    = new FakeUiDispatcher();
        var model = Connected(new RefreshScheduler(time, ui));
        model.RefreshInterval = Interval;
        await model.OnNavigatedToAsync();

        model.Read = () => throw new InvalidOperationException("Timeout expired.");
        time.Advance(Interval);
        ui.RunPosted();
        await WhenIdle(model);

        Assert.Equal(["Read Load", "Error Timed: Timeout expired."], _shell.Reports);
    }

    // Waits for SteppingTime's tick to be posted, runs it, and waits for its read.
    private static async Task TickAsync(FakeUiDispatcher ui)
    {
        Assert.True(SpinWait.SpinUntil(() => ui.Pending > 0, TimeSpan.FromSeconds(5)), "The timer didn't tick.");
        ui.RunPosted();
        await Task.Yield();
    }

    private static async Task WhenIdle(PageViewModel model)
    {
        for (var i = 0; i < 100 && model.IsBusy; i++)
            await Task.Delay(10);
    }

    /// <summary>A fake clock that only moves when told to.</summary>
    private sealed class FakeTimeProviderAt() : Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
}
