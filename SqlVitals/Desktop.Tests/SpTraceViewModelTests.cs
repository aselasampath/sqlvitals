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
/// a trace runs; the filters (#237: several values a box, SqlVitals's own calls hidden), the call
/// selected across a poll, what called each call (#238), and what it couldn't read.
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

    private static void Tick(MultiFilterChoices box, params string[] values) => MultiFilterChoicesTests.Tick(box, values);

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
                     model.Calls.Select(c => c.Event.ObjectName));
        Assert.Equal("Showing 4 of 5", model.FilterCount);
        Assert.Equal(["BillingDb", "SalesDb"], model.Database.Items.Select(i => i.Value));
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

        Tick(model.ClientApp, "OrderService");
        Assert.Equal(["dbo.usp_ShipOrder", "dbo.usp_PriceBasket", "dbo.usp_GetOrderHistory"], model.Calls.Select(c => c.Event.ObjectName));
        model.MinDurationFilterText = "90";
        Assert.Equal(["dbo.usp_ShipOrder", "dbo.usp_GetOrderHistory"], model.Calls.Select(c => c.Event.ObjectName));
        model.Search = "History";
        Assert.Equal("Showing 1 of 5", model.FilterCount);
        model.SpidText = "999";
        Assert.Equal("No captured calls match the current filters.", model.NoCalls);
        Assert.True(model.ShowsNoCalls);

        model.ClearFiltersCommand.Execute(null);

        Assert.Equal(4, model.Calls.Count);   // sp_reset_connection hidden again, as the page opens
        Assert.True(model.ClientApp.IsAll);
        Assert.Equal("", model.Search);
        model.HideResets = false;
        Assert.Equal("5 calls", model.FilterCount);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task AFilterTicked_StaysAChoice_AfterItsCallsAreCleared()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        Tick(model.Database, "BillingDb");

        model.ClearCommand.Execute(null);

        Assert.Equal(["BillingDb"], model.Database.Items.Select(i => i.Value));
        Assert.Equal("BillingDb", model.Database.Summary);
        Assert.Equal("No calls captured yet.", model.NoCalls);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task SeveralValuesTicked_AnyMatchesInABox_AndEveryBoxMustMatch()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        model.HideResets = false;

        Tick(model.ClientApp, "BillingService", "OrderService");
        Assert.Equal("5 calls", model.FilterCount);
        Assert.Equal("2 selected", model.ClientApp.Summary);

        Tick(model.Database, "BillingDb");
        Assert.Equal(["dbo.usp_BillOrder"], model.Calls.Select(c => c.Event.ObjectName));
        Assert.Equal("Showing 1 of 5", model.FilterCount);

        model.ClientApp.Items.Single(i => i.Value == "BillingService").IsChecked = false;
        Assert.Equal("No captured calls match the current filters.", model.NoCalls);

        Tick(model.Database, "SalesDb");
        Assert.Equal(4, model.Calls.Count);   // OrderService's, in either database
        Tick(model.Event, "rpc_completed");
        Tick(model.Login, "SVC_ORDERS");
        Tick(model.Host, "APP-WEB03");
        Assert.Equal("Showing 4 of 5", model.FilterCount);

        model.ClearFiltersCommand.Execute(null);

        Assert.All(new[] { model.Database, model.Login, model.ClientApp, model.Host, model.Event }, box => Assert.True(box.IsAll));
        Assert.Equal("Showing 4 of 5", model.FilterCount);   // sp_reset_connection hidden again
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task TheTicks_SurviveTheBufferRollingOver()
    {
        var repo  = Repo();
        var flood = Enumerable.Range(0, SpTraceViewModel.MaxRowsRetained).Select(i => Call(0, $"dbo.usp_Step{i}", 5, 90, "OrderService")).ToList();
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([Calls, flood]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        Tick(model.Database, "BillingDb");
        Assert.Single(model.Calls);

        Tick(2);   // 5,000 more: every BillingDb call is dropped

        Assert.DoesNotContain(model.AllCalls, c => c.Event.DatabaseName == "BillingDb");
        Assert.Equal(["BillingDb", "SalesDb"], model.Database.Items.Select(i => i.Value));
        Assert.Equal("BillingDb", model.Database.Summary);
        Assert.Equal("No captured calls match the current filters.", model.NoCalls);
        Assert.Equal($"Showing 0 of {SpTraceViewModel.MaxRowsRetained:N0}", model.FilterCount);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task SqlVitalsOwnCalls_AreHiddenByDefault_AndShownAgainWithoutRestarting()
    {
        var repo  = Repo();
        var calls = new List<string>();
        var own   = new[]
        {
            Call(8, "sys.sp_executesql", 3, 55, "SqlVitals"),
            Call(6, "sys.sp_executesql", 4, 56, "sqlvitals collector"),   // either name, in any case
            Call(5, "dbo.usp_Report", 20, 57, "SqlVitals - Prod"),         // the user's own Application Name: not ours
        };
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([[.. Calls, .. own]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        repo.StartTraceAsync      = _ => { calls.Add("start"); return Task.FromResult(Running); };
        var (model, _) = Connected(repo);
        Assert.True(model.HideSqlVitals);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);

        Assert.DoesNotContain(model.Calls, c => c.Event.SessionId is 55 or 56);
        Assert.Contains(model.Calls, c => c.Event.SessionId == 57);
        Assert.Equal("Showing 5 of 8", model.FilterCount);   // and sp_reset_connection

        model.HideSqlVitals = false;

        Assert.Contains(model.Calls, c => c.Event.SessionId == 55);
        Assert.Contains(model.Calls, c => c.Event.SessionId == 56);
        Assert.Equal(["start"], calls);   // the same trace
        Assert.Contains("SqlVitals", model.ClientApp.Items.Select(i => i.Value));

        model.ClearFiltersCommand.Execute(null);

        Assert.True(model.HideSqlVitals);
        Assert.Equal("Showing 5 of 8", model.FilterCount);
        model.OnNavigatedFrom();
    }

    [Theory]
    [InlineData("SqlVitals", true)]
    [InlineData("SQLVITALS", true)]
    [InlineData("SqlVitals Collector", true)]
    [InlineData("SqlVitals - Prod", false)]
    [InlineData("Core Microsoft SqlClient Data Provider", false)]
    [InlineData(null, false)]
    public void IsSqlVitals_KnowsOurApplicationNames(string? app, bool ours) =>
        Assert.Equal(ours, SpTraceViewModel.IsSqlVitals(app));

    [Fact]
    public async Task APoll_KeepsTheCallSelected_AndScrollsToTheNewest()
    {
        var repo  = Repo();
        var later = Call(0, "dbo.usp_Refund", 55, 80, "BillingService");
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([Calls, [later]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        SpTraceCall? scrolled = null;
        model.ScrollTo += call => scrolled = call;
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        model.SelectedCall = model.Calls.Single(c => c.Event.ObjectName == "dbo.usp_BillOrder");

        Tick(2);

        Assert.Equal("dbo.usp_BillOrder", model.SelectedCall!.Event.ObjectName);
        Assert.Same(later, model.Calls[0].Event);
        Assert.Same(later, scrolled?.Event);

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
        SpTraceCall? shown = null;
        model.ShowRow += call => shown = call;
        model.Search = "Basket";
        model.TabIndex = SpTraceViewModel.ProceduresTab;

        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.ShowSlowest, "Show the slowest"));

        Assert.Equal("dbo.usp_GetOrderHistory", model.SelectedCall!.Event.ObjectName);
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
    public async Task SqlVitalsOwnCalls_WhileHidden_AreLeftOutOfTheVerdict_AndTheSlowest()
    {
        var repo  = Repo();
        var ours  = Call(3, "sys.sp_executesql", 9_000, 55, "SqlVitals");
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([[Calls[0], ours]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        Tick(model.ClientApp, "OrderService");

        Assert.Equal("Capturing: 1 call so far", model.Verdict!.Headline);
        Assert.Contains("dbo.usp_GetOrderHistory", model.Verdict.Why);
        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.ShowSlowest, "Show the slowest"));
        Assert.Same(Calls[0], model.SelectedCall?.Event);
        Assert.Equal("OrderService", model.ClientApp.Summary);   // shown already: the ticks stay

        // Shown: the verdict counts them, and the slowest is SqlVitals's own.
        model.HideSqlVitals = false;
        Assert.Equal("Capturing: 2 calls so far", model.Verdict!.Headline);
        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.ShowSlowest, "Show the slowest"));
        Assert.Same(ours, model.SelectedCall?.Event);
        Assert.False(model.HideSqlVitals);
        model.OnNavigatedFrom();
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
        Assert.DoesNotContain(reset, model.Calls.Select(c => c.Event));

        await model.ActCommand.ExecuteAsync(new VerdictAction(SpTraceVerdict.ShowSlowest, "Show the slowest"));

        Assert.Same(reset, model.SelectedCall?.Event);
        Assert.Contains(reset, model.Calls.Select(c => c.Event));
        Assert.False(model.HideResets);
        model.OnNavigatedFrom();
    }

    // One request of OrderService's: the client calls dbo.usp_PlaceOrder, which calls usp_PriceBasket,
    // which calls usp_GetTaxRate. Each ends before its caller; seq counts up as they end.
    private const string Request = "5A4D5C9E-1B2C-4D3E-8F90-0A1B2C3D4E5F";

    private static SpTraceEvent Nested(string ev, string proc, double startMs, double durationMs, int seq) =>
        new(Now.AddMilliseconds(Math.Floor(startMs + durationMs)), ev, proc, ev == "module_end" ? "P" : null, "SalesDb",
            (long)durationMs, 1, 10, 0, 0, 1, 61, "svc_orders", "OrderService", "APP-WEB03", null, $"{Request}-{seq}")
        {
            DurationUs = (long)(durationMs * 1000),
        };

    private static readonly SpTraceEvent Tax    = Nested("module_end", "usp_GetTaxRate", 5, 10, 1);
    private static readonly SpTraceEvent Price  = Nested("module_end", "usp_PriceBasket", 3, 15, 2);
    private static readonly SpTraceEvent Place  = Nested("module_end", "usp_PlaceOrder", 0.1, 20, 3);
    private static readonly SpTraceEvent Client = Nested("rpc_completed", "dbo.usp_PlaceOrder", 0, 20.3, 4);

    private static SpTraceCall Row(SpTraceViewModel model, SpTraceEvent call) => model.AllCalls.Single(c => ReferenceEquals(c.Event, call));

    [Fact]
    public async Task EachCall_SaysWhatCalledIt_AndItsLevel()
    {
        var repo  = Repo();
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([[Tax, Price, Place, Client]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);

        Assert.Equal(("usp_PriceBasket", 3), (Row(model, Tax).CalledBy, Row(model, Tax).Level));
        Assert.Equal(("usp_PlaceOrder", 2), (Row(model, Price).CalledBy, Row(model, Price).Level));
        Assert.Equal(("(client)", 1), (Row(model, Place).CalledBy, Row(model, Place).Level));
        Assert.Equal(("(client)", 1), (Row(model, Client).CalledBy, Row(model, Client).Level));
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task ACallReadBeforeItsCaller_GetsItOnTheNextPoll_OnTheSameRow()
    {
        var repo  = Repo();
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([[Tax, Price], [Place, Client]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        var price = Row(model, Price);
        model.SelectedCall = price;
        Assert.Null(price.CalledBy);   // usp_PlaceOrder hasn't ended yet
        var changed = new List<string?>();
        price.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Tick(2);

        Assert.Same(price, Row(model, Price));
        Assert.Equal(("usp_PlaceOrder", 2), (price.CalledBy, price.Level));
        Assert.Contains(nameof(SpTraceCall.CalledBy), changed);
        Assert.Contains(nameof(SpTraceCall.Level), changed);
        Assert.Same(price, model.SelectedCall);
        model.OnNavigatedFrom();
    }

    [Fact]
    public async Task TopLevelOnly_HidesTheNestedCalls_AndClearFiltersTurnsItOff()
    {
        var repo  = Repo();
        var polls = new Queue<IReadOnlyList<SpTraceEvent>>([[Tax, Price, Place, Client, Calls[0]]]);
        repo.PollTraceEventsAsync = () => Task.FromResult(polls.Count > 0 ? polls.Dequeue() : []);
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.ToggleTraceCommand.ExecuteAsync(null);
        Tick(2);
        Assert.False(model.TopLevelOnly);
        Assert.Equal(5, model.Calls.Count);

        model.TopLevelOnly = true;

        // The client's two calls, once each: usp_PlaceOrder's module_end repeats its rpc_completed row.
        Assert.Equal(new[] { "dbo.usp_PlaceOrder", "dbo.usp_GetOrderHistory" }.Order(StringComparer.Ordinal),
                     model.Calls.Select(c => c.Event.ObjectName).Order(StringComparer.Ordinal));
        Assert.Equal("Showing 2 of 5", model.FilterCount);

        model.ClearFiltersCommand.Execute(null);

        Assert.False(model.TopLevelOnly);
        Assert.Equal(5, model.Calls.Count);
        model.OnNavigatedFrom();
    }
}
