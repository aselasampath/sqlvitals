using System.IO;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A code-behind page's timed reads on its view model (#169): no timer of its own, one read at a
/// time with Refresh and its own Read again, a failure reported once, its Start/Stop and the top
/// bar's Pause the view model's, and its countdown the view model's.
/// </summary>
public sealed class PageRefreshTests : IDisposable
{
    private static readonly TimeSpan AgentJobsInterval = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShellStatus _shell = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeUiDispatcher _ui = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ── The countdown ────────────────────────────────────────────────────

    [Theory]
    [InlineData(30.0, "30s")]
    [InlineData(29.2, "30s")]
    [InlineData(0.4, "1s")]
    [InlineData(0.0, "0s")]
    [InlineData(-1.0, "0s")]
    [InlineData(60.0, "1:00")]
    [InlineData(65.0, "1:05")]
    [InlineData(120.0, "2:00")]
    public void CountdownText_WholeSecondsRoundedUp_MinutesFromOne(double seconds, string text) =>
        Assert.Equal(text, PageViewModel.CountdownText(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheSchedule_SaysHowLongUntilItsNextTick()
    {
        using var schedule = new RefreshScheduler(_time, _ui).Every(TimeSpan.FromSeconds(10), () => Task.CompletedTask);
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Remaining);

        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), schedule.Remaining);

        // Once it fires, the next is a whole interval away.
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Remaining);
    }

    [Fact]
    public async Task TheCountdown_FollowsTheTimer_EverySecond_AndIsDashes_WhilePausedOrLeft()
    {
        var model = new CountingViewModel();
        model.Connect("Test", _shell, new RefreshScheduler(_time, _ui));
        model.ReadEvery(TimeSpan.FromSeconds(10), countdown: true);
        Assert.Equal("--", model.Countdown);

        await model.OnNavigatedToAsync();
        Assert.Equal("10s", model.Countdown);

        Step(TimeSpan.FromSeconds(1));
        Assert.Equal("9s", model.Countdown);
        Step(TimeSpan.FromSeconds(3));
        Assert.Equal("6s", model.Countdown);

        // It reads at 0, and counts from the whole interval again.
        Step(TimeSpan.FromSeconds(6));
        Assert.Equal(2, model.Loads);
        Assert.Equal("10s", model.Countdown);

        model.SetPaused(true);
        Assert.Equal("--", model.Countdown);
        model.SetPaused(false);
        Assert.Equal("10s", model.Countdown);

        model.OnNavigatedFrom();
        Assert.Equal("--", model.Countdown);
        Step(TimeSpan.FromMinutes(1));
        Assert.Equal(2, model.Loads);
    }

    [Fact]
    public async Task WithoutACountdown_OnlyTheReadsTick()
    {
        var model = new CountingViewModel();
        model.Connect("Test", _shell, new RefreshScheduler(_time, _ui));
        model.ReadEvery(TimeSpan.FromSeconds(10));
        await model.OnNavigatedToAsync();

        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, _ui.Pending);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, _ui.Pending);
    }

    private sealed class CountingViewModel : CodeBehindViewModel
    {
        public int Loads;

        protected override Task LoadAsync(CancellationToken cancel)
        {
            Loads++;
            return Task.CompletedTask;
        }
    }

    // ── A pilot page: Agent Jobs, every 30 s when ticked ─────────────────

    [Fact]
    public void AgentJobs_ReadsEvery30s_WhenTicked_OneAtATime_AndReportsAFailureOnce() => UiThread.Run(() =>
    {
        var reads = 0;
        TaskCompletionSource<AgentJobList>? gate = null;
        var repo = new FakeRepository
        {
            GetAgentJobsAsync = () =>
            {
                reads++;
                return gate?.Task ?? Task.FromResult(new AgentJobList([], AgentServiceState.Running, null, null));
            },
        };
        var page  = new AgentJobsPage(repo, shell: _shell);
        var model = Connect(page, page.ViewModel, "AgentJobs");

        UiThread.Wait(model.OnNavigatedToAsync());
        Assert.Equal(1, reads);

        // A snapshot until ticked: nothing is timed.
        Step(AgentJobsInterval);
        Assert.Equal(1, reads);
        Assert.Equal(PageKind.Snapshot, page.Kind);

        page.ChkAutoRefresh.IsChecked = true;
        Assert.Equal(PageKind.Live, page.Kind);
        Assert.Equal(AgentJobsInterval, page.LiveInterval);
        Step(AgentJobsInterval);
        Assert.Equal(2, reads);
        Assert.Equal("Read Timed", _shell.Reports[^1]);

        // Read again while a timed read runs: still one read.
        gate = new TaskCompletionSource<AgentJobList>();
        Step(AgentJobsInterval);
        Assert.Equal(3, reads);
        var again = model.RefreshAsync();
        Assert.Equal(3, reads);
        gate.SetResult(new AgentJobList([], AgentServiceState.Running, null, null));
        UiThread.Wait(again);
        gate = null;
        Assert.Equal(3, reads);

        // A failed timed read: one report, as timed; the jobs read before stay.
        repo.GetAgentJobsAsync = () => { reads++; return Task.FromException<AgentJobList>(new InvalidOperationException("Timeout expired.")); };
        Step(AgentJobsInterval);
        Assert.Equal(4, reads);
        Assert.Single(_shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal));
        Assert.Equal("Error Timed: Timeout expired.", _shell.Reports[^1]);
        Assert.False(page.ViewModel.Verdict?.Headline.StartsWith("Couldn't read", StringComparison.Ordinal) ?? false);

        // Unticked, or left: no more reads.
        page.ChkAutoRefresh.IsChecked = false;
        Step(AgentJobsInterval);
        Assert.Equal(4, reads);
        page.ChkAutoRefresh.IsChecked = true;
        model.OnNavigatedFrom();
        Step(AgentJobsInterval);
        Assert.Equal(4, reads);
        Assert.Equal(0, _ui.Pending);
    });

    // ── A pilot page: Processes, Start/Stop is the view model's pause ────

    [Fact]
    public void Processes_StartAndStop_AreTheTopBarsPause_AndTheCountdownIsTheViewModels() => UiThread.Run(() =>
    {
        var reads = 0;
        var repo  = new FakeRepository { GetProcessesAsync = () => { reads++; return Task.FromResult<IEnumerable<ProcessNode>>([]); } };
        Directory.CreateDirectory(_dir);
        var page    = new ProcessesPage(repo, new ConnectionSettingsService(_dir), new FakeNavigator(), _shell);
        var model   = Connect(page, page.ViewModel, "Processes");
        var context = (IPageContext)page;
        var every   = context.LiveInterval!.Value;

        // Stopped until the saved choice starts it.
        UiThread.Wait(model.OnNavigatedToAsync());
        Assert.True(context.IsPaused);
        Assert.Equal("--", page.TxtCountdown.Text);
        Step(every);
        Assert.Equal(1, reads);

        // The top bar's Resume is Start: the button follows, and the countdown runs.
        context.SetPaused(false);
        Assert.True(page.BtnAutoRefresh.IsChecked);
        Assert.False(model.IsPaused);
        Assert.Equal(PageViewModel.CountdownText(every), page.TxtCountdown.Text);
        Step(TimeSpan.FromSeconds(1));
        Assert.Equal(PageViewModel.CountdownText(every - TimeSpan.FromSeconds(1)), page.TxtCountdown.Text);
        Step(every - TimeSpan.FromSeconds(1));
        Assert.Equal(2, reads);
        Assert.Equal("Read Timed", _shell.Reports[^1]);

        // Stop is the top bar's Pause.
        page.BtnAutoRefresh.IsChecked = false;
        Assert.True(context.IsPaused);
        Assert.Equal("--", page.TxtCountdown.Text);
        Step(every);
        Assert.Equal(2, reads);
    });

    // The page as the page factory makes it, on this test's clock.
    private T Connect<T>(ICodeBehindPage page, T model, string tag) where T : CodeBehindViewModel
    {
        model.LoadThrough(page);
        model.Connect(tag, _shell, new RefreshScheduler(_time, _ui));
        return model;
    }

    // Moves the clock on a second at a time, running each tick and the reads it starts.
    private void Step(TimeSpan by)
    {
        for (var left = by; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(1))
        {
            _time.Advance(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1));
            _ui.RunPosted();
        }
    }

    /// <summary>A fake clock that only moves when told to.</summary>
    private sealed class FakeTimeProvider() : Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero));
}
