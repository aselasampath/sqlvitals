using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Memory's view model on its own (#187): no page, no STA thread, a fake repository. It reads the
/// figures, clerks and grants, trends the figures only while resumed, keeps the grant selected
/// across a refresh, filters to the waiting grants for an alert's focus, says what it couldn't
/// read once, and stops reading when the page is left.
/// </summary>
public sealed class MemoryViewModelTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeUiDispatcher _ui = new();

    private static FakeRepository Repo() => (FakeRepository)TemplateEverywhereTests.MemoryRepo();

    private (MemoryViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo)
    {
        var model = new MemoryViewModel(repo);
        var shell = new FakeShellStatus();
        model.Connect("Memory", shell, new RefreshScheduler(_time, _ui));
        return (model, shell);
    }

    [Fact]
    public async Task Loaded_ShowsTheFigures_TheClerks_AndTheGrants_TheLargestFirst()
    {
        var (model, shell) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Context", "Read Load"], shell.Reports);
        Assert.Equal(("24.0 GB", "28.0 GB", "Available physical memory is high", "Success"),
                     (model.Figures.Total, model.Figures.Target, model.Figures.State, model.Figures.StateTone));
        Assert.Equal("SQL Server 24.0 GB of 28.0 GB target · buffer pool 18.0 GB · plan cache 2.0 GB · Available physical memory is high", model.FiguresSummary);
        Assert.Equal(["SQLBUFFERPOOL", "SQLQUERYPLAN", "SQLCLR"], model.Clerks.Select(c => c.ClerkName));
        Assert.Equal([51, 77, 78], model.Grants.Select(g => g.SessionId).Order());
        Assert.Equal(51, model.Grants[0].SessionId);   // the largest grant
        Assert.Single(model.Samples);
        Assert.False(model.ShowsNoSamples);
        Assert.Equal("2 queries wait for a memory grant; session 77 the longest, for 41 s", model.Verdict!.Headline);
    }

    [Fact]
    public async Task Paused_UntilResumed_ThenTheTimerReadsOnlyTheFigures()
    {
        var repo = Repo();
        var (snapshots, grants) = (0, 0);
        var snapshot = repo.GetMemorySnapshotAsync!;
        var grantRead = repo.GetMemoryGrantsAsync!;
        repo.GetMemorySnapshotAsync = () => { snapshots++; return snapshot(); };
        repo.GetMemoryGrantsAsync   = () => { grants++; return grantRead(); };
        var (model, shell) = Connected(repo);
        await model.OnNavigatedToAsync();

        Assert.True(model.IsPaused);
        Assert.Equal("--", model.Countdown);
        Assert.EndsWith("paused: Resume (▶) in the top bar trends it", model.TrendSummary);
        _time.Advance(TimeSpan.FromSeconds(10));
        _ui.RunPosted();
        Assert.Equal((1, 1), (snapshots, grants));

        model.SetPaused(false);
        Assert.Equal("Context", shell.Reports[^1]);
        Assert.EndsWith("every 10 s", model.TrendSummary);
        Assert.Equal("10s", model.Countdown);
        _time.Advance(TimeSpan.FromSeconds(10));
        _ui.RunPosted();
        Assert.Equal((2, 1), (snapshots, grants));
        Assert.Equal(2, model.Samples.Count);
        Assert.Equal("Read Timed", shell.Reports[^1]);

        // Another interval: the wait starts again.
        model.IntervalIndex = 0;   // 5 s
        Assert.EndsWith("every 5 s", model.TrendSummary);
        _time.Advance(TimeSpan.FromSeconds(5));
        _ui.RunPosted();
        Assert.Equal(3, snapshots);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ATrendReadingThatFails_IsSaidInTheTopBar_AndKeepsTheVerdict()
    {
        var repo = Repo();
        var (model, shell) = Connected(repo);
        await model.OnNavigatedToAsync();
        var verdict = model.Verdict;
        model.SetPaused(false);

        repo.GetMemorySnapshotAsync = () => Task.FromException<MemorySnapshot>(new InvalidOperationException("Timeout expired."));
        _time.Advance(TimeSpan.FromSeconds(10));
        _ui.RunPosted();

        Assert.Equal("Error Timed: Timeout expired.", shell.Reports[^1]);
        Assert.Equal(verdict, model.Verdict);
        Assert.Equal("24.0 GB", model.Figures.Total);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task TheTrend_KeepsTheLast60Readings()
    {
        var (model, _) = Connected(Repo());
        MemorySnapshot? sampled = null;
        model.Sampled += s => sampled = s;

        for (var i = 0; i < MemoryViewModel.MaxSamples + 5; i++)
            await model.RefreshAsync();

        Assert.Equal(MemoryViewModel.MaxSamples, model.Samples.Count);
        Assert.Same(model.Samples[^1], sampled);
    }

    [Fact]
    public async Task ARefresh_KeepsTheGrantSelected()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedGrant = model.Grants.Single(g => g.SessionId == 78);

        await model.RefreshAsync();

        Assert.Equal(78, model.SelectedGrant!.SessionId);
    }

    [Fact]
    public async Task AMemoryGrantsAlert_ShowsTheWaitingGrantsOnly_UntilShowAll()
    {
        var (model, shell) = Connected(Repo());
        Assert.Null(model.FocusNote);

        model.ShowFocus(new PageFocus(new FocusFilter(FocusKind.PendingGrants), new FocusOrigin(FocusSource.Page, "a memory grants alert", "Alerts")));
        Assert.Null(model.FocusNote);   // nothing read yet
        await model.OnNavigatedToAsync();

        Assert.Equal([77, 78], model.Grants.Select(g => g.SessionId).Order());
        Assert.Equal("showing the 2 grants waiting for memory only", model.FocusNote);
        Assert.Equal("Show all 3 grants", model.ShowAllText);
        Assert.Contains("Context", shell.Reports);

        model.ClearFocus();
        Assert.Equal(3, model.Grants.Count);
        Assert.Null(model.ShowAllText);
    }

    [Fact]
    public async Task ShowTheWaitingGrants_OpensTheGrantsTab_OnTheLongestWaiter()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        MemoryGrant? shown = null;
        model.ShowRow += g => shown = g;

        await model.ActCommand.ExecuteAsync(new VerdictAction(MemoryVerdict.ShowWaiting, "Show"));

        Assert.Equal(MemoryViewModel.GrantsTab, model.TabIndex);
        Assert.Equal(77, model.SelectedGrant!.SessionId);
        Assert.Same(model.SelectedGrant, shown);

        await model.ActCommand.ExecuteAsync(new VerdictAction(MemoryVerdict.Clerks, "Clerks"));
        Assert.Equal(MemoryViewModel.ClerksTab, model.TabIndex);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var error = new InvalidOperationException("Timeout expired.");
        var (model, shell) = Connected(new FakeRepository
        {
            GetMemorySnapshotAsync = () => Task.FromException<MemorySnapshot>(error),
            GetMemoryGrantsAsync   = () => Task.FromException<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)>(error),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal("Error Load: Timeout expired.", Assert.Single(shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal)));
        Assert.Equal(PageVerdict.Unreadable("the memory figures", "Error: Timeout expired."), model.Verdict);
        Assert.Equal("--", model.Figures.Total);
        Assert.True(model.ShowsNoSamples);
    }

    [Fact]
    public async Task Empty_HasNoClerksOrGrants()
    {
        var repo = Repo();
        repo.GetMemoryGrantsAsync = () => Task.FromResult<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)>(([], [], []));
        var (model, _) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Clerks);
        Assert.Empty(model.Grants);
        // The figures still say how memory is used; the counter, that two wait.
        Assert.Equal("24.0 GB", model.Figures.Total);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<MemorySnapshot>();
        var repo = Repo();
        repo.GetMemorySnapshotAsync = () => reading.Task;
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetMemorySnapshotAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.DoesNotContain(shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal) || r.StartsWith("Read", StringComparison.Ordinal));
        Assert.Null(model.Verdict);
    }
}
