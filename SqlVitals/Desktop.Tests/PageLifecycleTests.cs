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

        protected override async Task LoadAsync(CancellationToken cancel)
        {
            Loads++;
            LastToken = cancel;
            if (Gate is { } gate)
                await (ListensToToken ? gate.Task.WaitAsync(cancel) : gate.Task);
            Read();
        }
    }

    private TestViewModel Connected(IRefreshScheduler? scheduler = null)
    {
        var model = new TestViewModel();
        model.Connect("Test", _shell, scheduler ?? new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
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
        Assert.Equal("Refreshing…", model.Status);

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
        Assert.Equal("Loading…", model.Status);
        model.Gate.SetResult();
        await opening;

        Assert.Equal(["Read Load"], _shell.Reports);
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
