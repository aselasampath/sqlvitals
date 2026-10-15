using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// SP Trace's view model on its own (#188): no page, no STA thread, a fake repository. Opening it
/// never starts a trace; Start and Stop keep their safety (a failed start drops what it left, a
/// failed stop says the session may be left, leaving the page drops it); the timer polls only while
/// a trace runs; the filters, the call selected across a poll, and what it couldn't read.
/// </summary>
public sealed class SpTraceViewModelTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeDialogService _dialogs = new();

    private static readonly DateTime Now = DateTime.Now;

    private static readonly TraceSessionStatus Running = new(true, TraceMode.ExtendedEvents, "SqlVitals_SpTrace", null, 5, 0, false);

    private static SpTraceEvent Call(int secondsAgo, string proc, long ms, int spid, string app, string database = "SalesDb") =>
        new(Now.AddSeconds(-secondsAgo), "rpc_completed", proc, "P", database, ms, ms / 3, ms * 41, 0, ms / 100, 12, spid, "svc_orders", app,
            "APP-WEB03", $"EXEC {proc} @CustomerId = 1042", Guid.NewGuid().ToString());

    private static readonly SpTraceEvent[] Calls =
    [
        Call(9, "dbo.usp_GetOrderHistory", 1_840, 61, "OrderService"),
        Call(7, "dbo.usp_PriceBasket", 42, 64, "OrderService"),
        Call(4, "dbo.usp_BillOrder", 310, 72, "BillingService", "BillingDb"),
        Call(2, "sys.sp_reset_connection", 0, 64, "OrderService"),
        Call(1, "dbo.usp_ShipOrder", 96, 77, "OrderService"),
    ];

    private static readonly SpAggregateRow[] Procedures =
    [
        new("SalesDb", "dbo", "usp_GetOrderHistory", 1, 1_840, 1_840, 1_840, 610, 75_440, 75_440, Now),
        new("SalesDb", "dbo", "usp_PriceBasket", 2, 40, 42, 80, 13, 1_640, 3_280, Now),
    ];

    // A server whose trace starts as Extended Events, and polls the calls given, once each.
    private static FakeRepository Repo(List<string>? calls = null, TraceSessionStatus? started = null)
    {
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([Calls]);
        return new FakeRepository
        {
            StartTraceAsync         = options => { calls?.Add($"start {options.MinDurationMs} ms"); return Task.FromResult(started ?? Running); },
            StopTraceAsync          = () => { calls?.Add("stop"); return Task.FromResult(TraceSessionStatus.Stopped("SqlVitals_SpTrace")); },
            GetTraceStatusAsync     = () => Task.FromResult(started ?? Running),
            PollTraceEventsAsync    = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []),
            PollProcedureStatsAsync = () => Task.FromResult<IReadOnlyList<SpAggregateRow>>(Procedures),
            GetObjectQueryPlanAsync = name => Task.FromResult<string?>($"<plan of {name}/>"),
        };
    }

    private (SpTraceViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo)
    {
        var model = new SpTraceViewModel(repo, repo, _dialogs);
        var shell = new FakeShellStatus();
        model.Connect("SpTrace", shell, new RefreshScheduler(_time, _ui));
        return (model, shell);
    }

    private void Tick(int seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        _ui.RunPosted();
    }

    [Fact]
    public async Task Opened_ReadsTheProcedures_AndStartsNothing()
    {
        var calls = new List<string>();
        var (model, shell) = Connected(Repo(calls));

        await model.OnNavigatedToAsync();

        Assert.Empty(calls);
        Assert.Equal(["usp_GetOrderHistory", "usp_PriceBasket"], model.Procedures.Select(p => p.ProcedureName));
        Assert.False(model.ShowsNoProcedures);
        Assert.Equal("Not capturing", model.Verdict!.Headline);
        Assert.Equal(("Idle", "Info", "● Start Trace"), (model.ModeText, model.ModeTone, model.TraceButtonText));
        Assert.Equal("No calls captured yet.", model.NoCalls);
        Assert.False(model.IsPolling);
        Assert.Equal("Read Load", shell.Reports[^1]);
        Tick(30);
        Assert.Empty(calls);   // nothing timed while stopped
    }

    [Fact]
    public async Task Start_CapturesWithTheMinDuration_ThenTheTimerPollsTheCalls_NewestFirst()
    {
        var calls = new List<string>();
        var (model, shell) = Connected(Repo(calls));
        await model.OnNavigatedToAsync();
        model.MinDurationText = "25";

        await model.ToggleTraceCommand.ExecuteAsync(null);

        Assert.Equal(["start 25 ms"], calls);
        Assert.True(model.IsTracing);
        Assert.Equal("■ Stop Trace", model.TraceButtonText);
        Assert.Equal(("Extended Events", "Success"), (model.ModeText, model.ModeTone));
        Assert.StartsWith("Capturing live calls via session 'SqlVitals_SpTrace' — 5 captured.", model.TraceStatus);
        Assert.True(model.IsPolling);
        Assert.Equal(TimeSpan.FromSeconds(2), model.PollInterval);
        Assert.Equal("Context", shell.Reports[^1]);

        Tick(2);
        // sp_reset_connection is hidden by default.
        Assert.Equal(["dbo.usp_ShipOrder", "dbo.usp_BillOrder", "dbo.usp_PriceBasket", "dbo.usp_GetOrderHistory"],
                     model.Calls.Select(c => c.ObjectName));
        Assert.Equal("Showing 4 of 5", model.FilterCount);
        Assert.Equal(["(All)", "BillingDb", "SalesDb"], model.Database.Items);
        Assert.Equal("Capturing: 5 calls so far", model.Verdict!.Headline);
        Assert.Equal("Read Timed", shell.Reports[^1]);

        // Another interval: the next poll waits for it.
        model.IntervalIndex = 4;   // 10 s
        Assert.Equal(TimeSpan.FromSeconds(10), model.PollInterval);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task Stop_DropsTheSession_AndThePollingStops()
    {
        var calls = new List<string>();
        var (model, _) = Connected(Repo(calls));
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);

        await model.ToggleTraceCommand.ExecuteAsync(null);

        Assert.Equal(["start 0 ms", "stop"], calls);
        Assert.False(model.IsTracing);
        Assert.False(model.IsPolling);
        Assert.Equal("Idle", model.ModeText);
        Assert.Empty(_dialogs.Shown);
    }

    [Fact]
    public async Task ADmvFallback_OpensByProcedure()
    {
        var fallback = new TraceSessionStatus(true, TraceMode.DmvFallback, "SqlVitals_SpTrace", "This login lacks ALTER ANY EVENT SESSION.", 0, 0, false);
        var (model, _) = Connected(Repo(started: fallback));

        await model.ToggleTraceCommand.ExecuteAsync(null);

        Assert.Equal(SpTraceViewModel.ProceduresTab, model.TabIndex);
        Assert.Equal(("DMV fallback", "Warning"), (model.ModeText, model.ModeTone));
        Assert.Equal("This login lacks ALTER ANY EVENT SESSION.", model.TraceStatus);
        Assert.Equal("Capturing totals per procedure only", model.Verdict!.Headline);
    }

    [Fact]
    public async Task AStartThatFails_SaysSo_AndDropsWhatItLeft()
    {
        var calls = new List<string>();
        var repo  = Repo(calls);
        repo.StartTraceAsync = _ => Task.FromException<TraceSessionStatus>(new InvalidOperationException("Login failed."));
        var (model, _) = Connected(repo);

        await model.ToggleTraceCommand.ExecuteAsync(null);

        Assert.Equal(["Warn: SP Trace: Could not start the trace: Login failed."], _dialogs.Shown);
        Assert.Equal(["stop"], calls);
        Assert.False(model.IsTracing);
        Assert.False(model.IsPolling);
    }

    [Fact]
    public async Task AStopThatFails_SaysTheSessionMayBeLeft()
    {
        var repo = Repo();
        var (model, _) = Connected(repo);
        await model.ToggleTraceCommand.ExecuteAsync(null);
        repo.StopTraceAsync = () => Task.FromException<TraceSessionStatus>(new InvalidOperationException("Timeout expired."));

        await model.ToggleTraceCommand.ExecuteAsync(null);

        Assert.Equal(["Warn: SP Trace: The trace stopped, but the event session may not have been dropped: Timeout expired."], _dialogs.Shown);
        Assert.False(model.IsPolling);
    }

    [Fact]
    public async Task TheButton_CantStartAgain_WhileAStartRuns()
    {
        var starting = new TaskCompletionSource<TraceSessionStatus>();
        var repo = Repo();
        repo.StartTraceAsync = _ => starting.Task;
        var (model, _) = Connected(repo);

        var start = model.ToggleTraceCommand.ExecuteAsync(null);

        Assert.True(model.IsTracing);
        Assert.False(model.ToggleTraceCommand.CanExecute(null));
        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.Stop, "Stop Trace"));   // the verdict waits too
        starting.SetResult(Running);
        await start;
        Assert.True(model.ToggleTraceCommand.CanExecute(null));
        Assert.True(model.IsPolling);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task Leaving_DropsTheSession_AndStopsPolling()
    {
        var calls = new List<string>();
        var (model, _) = Connected(Repo(calls));
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);

        model.OnNavigatedFrom();

        Assert.Equal(["start 0 ms", "stop"], calls);
        Assert.False(model.IsPolling);
        Assert.False(model.IsTracing);
        Assert.Empty(_dialogs.Shown);   // the page is gone: nothing to say
    }

    [Fact]
    public async Task LeavingWhileAStartRuns_DropsTheSessionOnceItStarts()
    {
        var calls    = new List<string>();
        var starting = new TaskCompletionSource<TraceSessionStatus>();
        var repo     = Repo(calls);
        repo.StartTraceAsync = _ => { calls.Add("start"); return starting.Task; };
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();

        var start = model.ToggleTraceCommand.ExecuteAsync(null);
        model.OnNavigatedFrom();
        Assert.Equal(["start"], calls);   // nothing running yet to drop
        starting.SetResult(Running);
        await start;

        Assert.Equal(["start", "stop"], calls);
        Assert.False(model.IsPolling);
    }

    [Fact]
    public async Task Leaving_CancelsTheProceduresRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<IReadOnlyList<SpAggregateRow>>();
        var repo = Repo();
        repo.PollProcedureStatsAsync = () => reading.Task;
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.PollProcedureStatsAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.DoesNotContain(shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal) || r.StartsWith("Read", StringComparison.Ordinal));
        Assert.Equal("Not capturing", model.Verdict!.Headline);
    }

    [Fact]
    public async Task TheFilters_NarrowTheCalls_AndClearFiltersPutsThemBack()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);

        model.ClientApp.Selected = "OrderService";
        Assert.Equal(["dbo.usp_ShipOrder", "dbo.usp_PriceBasket", "dbo.usp_GetOrderHistory"], model.Calls.Select(c => c.ObjectName));
        model.MinDurationFilterText = "90";
        Assert.Equal(["dbo.usp_ShipOrder", "dbo.usp_GetOrderHistory"], model.Calls.Select(c => c.ObjectName));
        model.Search = "History";
        Assert.Equal("Showing 1 of 5", model.FilterCount);
        model.SpidText = "999";
        Assert.Equal("No captured calls match the current filters.", model.NoCalls);
        Assert.True(model.ShowsNoCalls);

        model.ClearFiltersCommand.Execute(null);

        Assert.Equal(4, model.Calls.Count);   // sp_reset_connection hidden again, as the page opens
        Assert.Equal(FilterChoices.All, model.ClientApp.Selected);
        Assert.Equal("", model.Search);
        model.HideResets = false;
        Assert.Equal("5 calls", model.FilterCount);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AFilterPicked_StaysAChoice_AfterItsCallsAreCleared()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        model.Database.Selected = "BillingDb";

        model.ClearCommand.Execute(null);

        Assert.Equal(["(All)", "BillingDb"], model.Database.Items);
        Assert.Equal("BillingDb", model.Database.Selected);
        Assert.Equal("No calls captured yet.", model.NoCalls);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task APoll_KeepsTheCallSelected_AndScrollsToTheNewest()
    {
        var repo  = Repo();
        var later = Call(0, "dbo.usp_Refund", 55, 80, "BillingService");
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([Calls, [later]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        SpTraceEvent? scrolled = null;
        model.ScrollTo += call => scrolled = call;
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        model.SelectedCall = model.Calls.Single(c => c.ObjectName == "dbo.usp_BillOrder");

        Tick(2);

        Assert.Equal("dbo.usp_BillOrder", model.SelectedCall!.ObjectName);
        Assert.Same(later, model.Calls[0]);
        Assert.Same(later, scrolled);

        // Auto-scroll off: the grid stays where it is.
        model.AutoScroll = false;
        scrolled = null;
        model.Search = "usp";
        Assert.Null(scrolled);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ShowTheSlowest_ClearsTheFiltersThatHideIt_AndSelectsIt()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        SpTraceEvent? shown = null;
        model.ShowRow += call => shown = call;
        model.Search = "Basket";
        model.TabIndex = SpTraceViewModel.ProceduresTab;

        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.ShowSlowest, "Show the slowest"));

        Assert.Equal("dbo.usp_GetOrderHistory", model.SelectedCall!.ObjectName);
        Assert.Same(model.SelectedCall, shown);
        Assert.Equal("", model.Search);
        Assert.Equal(SpTraceViewModel.CallsTab, model.TabIndex);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AQuietPoll_KeepsTheProceduresShown()
    {
        var repo = Repo();
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        repo.PollProcedureStatsAsync = () => Task.FromResult<IReadOnlyList<SpAggregateRow>>([]);

        await model.RefreshAsync();

        Assert.Equal(2, model.Procedures.Count);
    }

    [Fact]
    public async Task NoProcedureActivity_SaysSo()
    {
        var repo = Repo();
        repo.PollProcedureStatsAsync = () => Task.FromResult<IReadOnlyList<SpAggregateRow>>([]);
        var (model, _) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.True(model.Procedures.IsFilled);
        Assert.Empty(model.Procedures);
        Assert.True(model.ShowsNoProcedures);
    }

    [Fact]
    public async Task AFailedRead_SaysSoInTheVerdict_ButAFailedPollOnlyInTheTopBar()
    {
        var repo = Repo();
        repo.PollProcedureStatsAsync = () => Task.FromException<IReadOnlyList<SpAggregateRow>>(new InvalidOperationException("Timeout expired."));
        var (model, shell) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal((VerdictLevel.Unknown, "Couldn't read the procedure statistics"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.Equal("Error: Timeout expired. Refresh to try again.", model.Verdict.Why);
        Assert.Equal("Error Load: Timeout expired.", shell.Reports[^1]);

        // Running: a poll that fails says so in the top bar; the trace and its verdict carry on.
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        Assert.Equal("Error Timed: Timeout expired.", shell.Reports[^1]);
        Assert.Equal("Capturing: 5 calls so far", model.Verdict!.Headline);
        Assert.True(model.IsPolling);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task TheDrawersPlan_IsTheProceduresCachedPlan()
    {
        var (model, _) = Connected(Repo());

        Assert.Equal("<plan of dbo.usp_BillOrder/>", await model.PlanOfAsync("dbo.usp_BillOrder"));
    }

    [Fact]
    public async Task AStartCancelledByLeaving_SaysNothing_AndDropsWhatItMade()
    {
        var calls    = new List<string>();
        var starting = new TaskCompletionSource<TraceSessionStatus>();
        var repo     = Repo(calls);
        repo.StartTraceAsync = _ => { calls.Add("start"); return starting.Task; };
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();

        var start = model.ToggleTraceCommand.ExecuteAsync(null);
        model.OnNavigatedFrom();
        starting.SetCanceled();   // leaving cancels the start, as the page's lifetime does
        await start;

        Assert.Empty(_dialogs.Shown);
        Assert.Equal(["start", "stop"], calls);
        Assert.False(model.IsTracing);
        Assert.False(model.IsPolling);
    }

    [Fact]
    public async Task TheSlowest_HiddenByTheDefaultFilters_IsShownToo()
    {
        var repo  = Repo();
        var reset = Call(3, "sys.sp_reset_connection", 9_000, 64, "OrderService");
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([[Calls[0], Calls[1], reset]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        Assert.DoesNotContain(reset, model.Calls);

        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.ShowSlowest, "Show the slowest"));

        Assert.Same(reset, model.SelectedCall);
        Assert.Contains(reset, model.Calls);
        Assert.False(model.HideResets);
        model.OnNavigatedFrom();
    }
}
