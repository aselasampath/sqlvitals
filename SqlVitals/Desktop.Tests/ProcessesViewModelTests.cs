using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Processes' view model on its own (#186): no page, no STA thread, a fake repository. It reads the
/// sessions, and the waits only while the Waiting view is shown; it says what it couldn't read once,
/// stops reading when the page is left, remembers the view, the interval and Start/Stop, and lists
/// a session's waits from its link (#145). The session selected is the map's, kept by the control.
/// </summary>
public sealed class ProcessesViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0) =>
        new(spid, blockedBy, blockedBy != 0 ? "suspended" : "sleeping", "login", "host", "app",
            blockedBy != 0 ? "LCK_M_X" : null, waitMs, null, null, "db", 0, 0, 0,
            DateTime.MinValue, DateTime.MinValue, 0, null, null, "");

    private static ActiveWait Wait(int session, int blockedBy = 0) =>
        new(session, blockedBy, "LCK_M_X", 252_000, 252, null, null, "suspended", "SELECT", 0, 0, 0, 0, 0, 0, 1,
            "login", "host", "app", "db", "Lock", blockedBy != 0 ? 1 : 0, null, DateTime.Now, "Critical");

    // 87 blocks 112 and 115; 5 blocks nothing.
    private static readonly ProcessNode[] Sessions = [Node(87), Node(112, 87, 245_000), Node(115, 87, 60_000), Node(5)];

    private static FakeRepository Repo(Func<int>? waitReads = null) => new()
    {
        GetProcessesAsync   = () => Task.FromResult<IEnumerable<ProcessNode>>(Sessions),
        GetActiveWaitsAsync = () =>
        {
            waitReads?.Invoke();
            return Task.FromResult<IEnumerable<ActiveWait>>([Wait(87), Wait(112, 87), Wait(200)]);
        },
    };

    private ConnectionSettingsService Settings() => new(_dir);

    private (ProcessesViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo, string? view = null)
    {
        var model = new ProcessesViewModel(repo, repo, Settings(), new FakeNavigator(), view);
        var shell = new FakeShellStatus();
        model.Connect("Processes", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell);
    }

    [Fact]
    public async Task Loaded_OnTheMap_ClassifiesTheSessions_AndJudgesTheBlocking_WithoutTheWaits()
    {
        var reads = 0;
        var (model, shell) = Connected(Repo(() => reads++));

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(ProcessesView.Map, model.View);
        Assert.Equal(4, model.SessionsRead!.Count);
        Assert.Equal("Session 87 is blocking 2 sessions, the longest for 4.1 min", model.Verdict!.Headline);
        Assert.Equal(0, reads);
        Assert.Empty(model.Waits);
    }

    [Fact]
    public async Task Loaded_OnWaiting_ReadsTheWaits_AndJudgesThem()
    {
        var (model, _) = Connected(Repo(), nameof(ProcessesView.Waiting));

        await model.OnNavigatedToAsync();

        Assert.True(model.IsWaiting);
        Assert.False(model.ShowsMap);
        Assert.Equal(3, model.Waits.Count);
        Assert.StartsWith("Session 112 is blocked by session 87", model.Verdict!.Headline);
    }

    [Fact]
    public async Task Empty_NothingIsBlocked()
    {
        var (model, _) = Connected(new FakeRepository { GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>([]) });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.SessionsRead!);
        Assert.Equal(VerdictLevel.Ok, model.Verdict!.Level);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var error = new InvalidOperationException("Timeout expired.");
        var (sessions, shell) = Connected(new FakeRepository { GetProcessesAsync = () => Task.FromException<IEnumerable<ProcessNode>>(error) });
        await sessions.OnNavigatedToAsync();
        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the sessions", "Error: Timeout expired."), sessions.Verdict);

        var repo = Repo();
        repo.GetActiveWaitsAsync = () => Task.FromException<IEnumerable<ActiveWait>>(error);
        var (waits, _) = Connected(repo, nameof(ProcessesView.Waiting));
        await waits.OnNavigatedToAsync();
        Assert.Equal(PageVerdict.Unreadable("the waiting tasks", "Error: Timeout expired."), waits.Verdict);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<IEnumerable<ProcessNode>>();
        var repo = new FakeRepository { GetProcessesAsync = () => reading.Task };
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetProcessesAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.SessionsRead);
    }

    [Fact]
    public async Task ChoosingWaiting_ReadsTheWaits_AndIsRemembered_ALinksViewIsNot()
    {
        var reads = 0;
        var (model, _) = Connected(Repo(() => reads++));
        await model.OnNavigatedToAsync();

        model.IsWaiting = true;
        Assert.Equal(1, reads);
        Assert.Equal(ProcessesView.Waiting, Settings().Load().ProcessesView);

        model.IsGrid = true;
        model.IsWaiting = false;   // a button unchecked as another is: nothing
        Assert.Equal(ProcessesView.Grid, Settings().Load().ProcessesView);

        // A link's view: shown, not remembered.
        var (linked, _) = Connected(Repo(), nameof(ProcessesView.Waiting));
        Assert.True(linked.IsWaiting);
        Assert.Equal(ProcessesView.Grid, Settings().Load().ProcessesView);
    }

    [Fact]
    public async Task ChoosingWaiting_WhileTheSessionsAreRead_ReadsTheWaitsOnce()
    {
        var reads    = 0;
        var sessions = new TaskCompletionSource<IEnumerable<ProcessNode>>();
        var repo     = Repo(() => reads++);
        repo.GetProcessesAsync = () => sessions.Task;
        var (model, _) = Connected(repo);

        var loading = model.OnNavigatedToAsync();
        model.IsWaiting = true;
        sessions.SetResult(Sessions);
        await loading;
        while (model.IsBusy)
            await Task.Delay(10);

        Assert.Equal(1, reads);
        Assert.Equal(3, model.Waits.Count);
    }

    [Fact]
    public void TheIntervalAndStart_AreRemembered_AndPutBack()
    {
        var (model, _) = Connected(Repo());
        Assert.True(model.IsPaused);
        Assert.Equal("Start", model.AutoRefreshText);

        model.IntervalSeconds = 30;
        model.AutoRefresh     = true;

        Assert.False(model.IsPaused);
        Assert.Equal("Stop", model.AutoRefreshText);
        var (again, _) = Connected(Repo());
        Assert.Equal(30, again.IntervalSeconds);
        Assert.True(again.AutoRefresh);
        Assert.False(again.IsPaused);
    }

    [Fact]
    public async Task ASessionsLink_ListsItsWaits_AndThoseItBlocks_UntilShowAll()
    {
        var (model, _) = Connected(Repo());
        model.FocusOnSession(87);
        await model.OnNavigatedToAsync();

        Assert.Equal([87, 112], model.Waits.Select(w => w.SessionId));
        Assert.Equal("showing session 87 and the sessions it blocks only", model.SessionNote);
        Assert.Equal("Show all 3 waiting tasks", model.SessionShowAllText);

        model.ClearSession();

        Assert.Equal(3, model.Waits.Count);
        Assert.Null(model.SessionNote);
        Assert.False(model.HasSession);
    }

    [Fact]
    public async Task TheWaitingVerdicts_ShowTheChain_IsTheMap_TheChainsAndTheBlocker()
    {
        var (model, _) = Connected(Repo(), nameof(ProcessesView.Waiting));
        await model.OnNavigatedToAsync();
        var asked = new List<string>();
        model.ChainsOnly  += () => asked.Add("chains");
        model.GoToSession += id => asked.Add($"session {id}");

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single());

        Assert.Equal(ProcessesView.Map, model.View);
        Assert.Equal(["chains", "session 87"], asked);
        Assert.StartsWith("Session 87 is blocking", model.Verdict!.Headline);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
