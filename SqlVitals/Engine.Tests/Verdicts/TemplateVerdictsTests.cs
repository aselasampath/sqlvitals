using System.Globalization;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Tests.Verdicts;

/// <summary>
/// The verdicts of the Activity, Waits and Queries pages (#149): what is wrong, or that nothing is,
/// including when there is nothing to judge and when the page couldn't tell.
/// </summary>
public class TemplateVerdictsTests
{
    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── Active Waits ─────────────────────────────────────────────────────

    private static ActiveWait Wait(int spid, string? type, long ms, int blockedBy = 0, string category = "Locks") =>
        new(spid, blockedBy, type, ms, ms / 1000.0, null, type is null ? null : "KEY: 5:72057594 (a1b2)", "suspended", "SELECT",
            0, 0, 0, 0, 0, 0, 1, "app", "host", "OrderService", "SalesDb", category, blockedBy > 0 ? 1 : 0, "SELECT 1", DateTime.Now, "Low");

    [Fact]
    public void ActiveWaits_NothingRunning_IsOk()
    {
        var verdict = Invariant(() => ActiveWaitsVerdict.From([]));
        Assert.Equal((VerdictLevel.Ok, "No request is waiting", "No user request is running now."), (verdict.Level, verdict.Headline, verdict.Why));

        var running = Invariant(() => ActiveWaitsVerdict.From([Wait(51, null, 0), Wait(52, null, 0)]));
        Assert.Equal("2 requests are running, none waiting on anything.", running.Why);
    }

    [Fact]
    public void ActiveWaits_Blocked_NamesTheLongest_ColouredByTheThreshold()
    {
        var verdict = Invariant(() => ActiveWaitsVerdict.From(
            [Wait(51, "LCK_M_X", 40_000, blockedBy: 87), Wait(52, "LCK_M_S", 250_000, blockedBy: 87), Wait(60, "PAGEIOLATCH_SH", 20, category: "I/O")],
            new HealthThreshold(30, 120)));

        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("2 requests are blocked; session 52 the longest, for 4.2 min", verdict.Headline);
        Assert.Equal("It waits on LCK_M_S (KEY: 5:72057594 (a1b2)), held by session 87. Processes shows the whole chain.", verdict.Why);
        Assert.Equal([new VerdictAction(ActiveWaitsVerdict.Processes, "Blocking in Processes", 87)], verdict.Actions);

        var brief = Invariant(() => ActiveWaitsVerdict.From([Wait(51, "LCK_M_X", 2_000, blockedBy: 87)], new HealthThreshold(30, 120)));
        Assert.Equal((VerdictLevel.Info, "Session 51 is blocked by session 87, for 2 s"), (brief.Level, brief.Headline));
    }

    [Fact]
    public void ActiveWaits_NotBlocked_SaysWhatTheyWaitOn()
    {
        var verdict = Invariant(() => ActiveWaitsVerdict.From(
            [Wait(51, "PAGEIOLATCH_SH", 300, category: "I/O"), Wait(52, "WRITELOG", 90_000, category: "I/O"), Wait(53, "CXPACKET", 10, category: "CPU")]));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);   // a wait of over a minute
        Assert.Equal("3 requests are waiting, 2 on I/O waits", verdict.Headline);
        Assert.Equal("None is blocked. The longest wait: session 52 on WRITELOG, 1.5 min.", verdict.Why);
        Assert.Empty(verdict.Actions);
    }

    // ── Deadlocks ────────────────────────────────────────────────────────

    private static DeadlockReport Deadlock(DateTime utc, string code, params string[] objects) =>
        new(utc,
            [new("p1", true, 55, "app", "host", "OrderService", "SalesDb", null, "X", null, 10, 1, null, code, null, null, null),
             new("p2", false, 60, "app", "host", "OrderService", "SalesDb", null, "X", null, 10, 1, null, "dbo.usp_Other", null, null, null)],
            objects.Select(o => new DeadlockResource("Key", o, "PK", "hobt", [], [])).ToList(), "<deadlock/>");

    private static PageVerdict Deadlocks(DeadlockHistory history, string during = "in the last 24 hours")
    {
        var (patterns, reports) = DeadlockPatterns.Group(history.Deadlocks);
        return DeadlocksVerdict.From(history, reports, patterns, during);
    }

    [Fact]
    public void Deadlocks_CountsThem_AndNamesTheObjectFoughtOverMost()
    {
        var now = DateTime.UtcNow;
        var history = new DeadlockHistory(
        [
            Deadlock(now.AddMinutes(-5),  "dbo.usp_Transfer", "Sales.dbo.Orders", "Sales.dbo.Lines"),
            Deadlock(now.AddMinutes(-50), "dbo.usp_Transfer", "Sales.dbo.Orders", "Sales.dbo.Lines"),
            Deadlock(now.AddMinutes(-90), "dbo.usp_Report",   "Sales.dbo.Stock"),
        ], DeadlockSource.EventFile, 3, 0, "system_health*.xel", null);

        var verdict = Invariant(() => Deadlocks(history));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("3 deadlocks in the last 24 hours; Sales.dbo.Lines in 2 of them", verdict.Headline);
        Assert.StartsWith("The same deadlock came back 2 times, last at ", verdict.Why);
        Assert.EndsWith(": dbo.usp_Transfer; dbo.usp_Other. Fix it first.", verdict.Why);
        Assert.Equal([DeadlocksVerdict.OpenNewest, DeadlocksVerdict.Recurring], verdict.Actions.Select(a => a.Key));
    }

    [Fact]
    public void Deadlocks_One_NoneInRange_AndNoneAtAll()
    {
        var now = DateTime.UtcNow;
        var one = Invariant(() => Deadlocks(new DeadlockHistory([Deadlock(now, "dbo.usp_Transfer", "Sales.dbo.Orders")], DeadlockSource.RingBuffer, 1, 0, null, null)));
        Assert.Equal("1 deadlock in the last 24 hours, on Sales.dbo.Orders", one.Headline);
        Assert.Equal([DeadlocksVerdict.OpenNewest], one.Actions.Select(a => a.Key));

        var older = new DeadlockHistory([Deadlock(now.AddDays(-3), "x", "t")], DeadlockSource.EventFile, 1, 0, null, null);
        var none  = Invariant(() => DeadlocksVerdict.From(older, [], [], "in the last 24 hours"));
        Assert.Equal((VerdictLevel.Ok, "No deadlocks in the last 24 hours", "system_health holds 1 older."), (none.Level, none.Headline, none.Why));
        Assert.Equal(DeadlocksVerdict.ShowAll, Assert.Single(none.Actions).Key);

        var empty = Invariant(() => Deadlocks(new DeadlockHistory([], DeadlockSource.EventFile, 0, 0, null, null)));
        Assert.Equal((VerdictLevel.Ok, "No deadlocks"), (empty.Level, empty.Headline));
    }

    [Fact]
    public void Deadlocks_Unreadable_SaysWhy()
    {
        var verdict = Deadlocks(DeadlockHistory.Unavailable("VIEW SERVER STATE is needed to read system_health."));
        Assert.Equal((VerdictLevel.Unknown, "The deadlock history can't be read", "VIEW SERVER STATE is needed to read system_health."),
                     (verdict.Level, verdict.Headline, verdict.Why));
    }

    // ── App Connections ──────────────────────────────────────────────────

    private static ApplicationConnection App(string name, int connections, int active) =>
        new(name, connections, active, 0, 0, 0, 0, 0, DateTime.Now, DateTime.Now, DateTime.Now);

    [Fact]
    public void AppConnections_SaysWhoHoldsThem()
    {
        var verdict = Invariant(() => AppConnectionsVerdict.From([App("OrderService", 120, 3), App("Reports", 12, 1), App("", 2, 0)]));
        Assert.Equal((VerdictLevel.Ok, "3 applications hold 134 connections; OrderService the most, 120"), (verdict.Level, verdict.Headline));
        Assert.Equal("4 are running a request now.", verdict.Why);

        var none = AppConnectionsVerdict.From([]);
        Assert.Equal((VerdictLevel.Info, "No user connections"), (none.Level, none.Headline));
    }

    [Fact]
    public void AppConnections_AnIdlePoolThatBig_LooksLeaked()
    {
        var verdict = Invariant(() => AppConnectionsVerdict.From([App("OrderService", 480, 3), App("Reports", 12, 1)]));
        Assert.Equal((VerdictLevel.Warning, "OrderService holds 480 connections, 3 of them running a request"), (verdict.Level, verdict.Headline));
        Assert.Contains("connection leak", verdict.Why);

        // Busy enough: not a leak.
        Assert.Equal(VerdictLevel.Ok, AppConnectionsVerdict.From([App("OrderService", 480, 60)]).Level);
    }

    // ── Top Waits ────────────────────────────────────────────────────────

    private static TopWaitType Top(int rank, string type, string category, long ms) =>
        new(rank, type, category, 100, ms, 0, 0, 0, 0, ms / 1000.0, 0, 0, 0, DateTime.Now, "#000", "");

    [Fact]
    public void TopWaits_NamesTheTopCategory_AndWhatItMeans()
    {
        var verdict = Invariant(() => TopWaitsVerdict.From(
            [Top(1, "PAGEIOLATCH_SH", "I/O", 500), Top(2, "SOS_SCHEDULER_YIELD", "CPU", 300), Top(3, "WRITELOG", "I/O", 100), Top(4, "LCK_M_X", "Locks", 100)]));

        Assert.Equal(VerdictLevel.Info, verdict.Level);
        Assert.Equal("Most waiting is on I/O: 60 % of the wait time, mostly PAGEIOLATCH_SH", verdict.Headline);
        Assert.StartsWith("Queries wait for pages read from disk (PAGEIOLATCH)", verdict.Why);
        Assert.Contains(" Then CPU, 30 %.", verdict.Why);
        Assert.Equal([new VerdictAction(TopWaitsVerdict.Cause, "File I/O"), new VerdictAction(TopWaitsVerdict.Trend, "Wait Trend")], verdict.Actions);
        Assert.Equal(("FileIo", "File I/O"), TopWaitsVerdict.CausePage("I/O"));
    }

    [Fact]
    public void TopWaits_LocksOnTop_IsAWarning_AndNothingWaited_IsOk()
    {
        var locks = Invariant(() => TopWaitsVerdict.From([Top(1, "LCK_M_X", "Locks", 900), Top(2, "WRITELOG", "I/O", 100)]));
        Assert.Equal((VerdictLevel.Warning, "Most waiting is on Locks: 90 % of the wait time, mostly LCK_M_X"), (locks.Level, locks.Headline));

        var other = Invariant(() => TopWaitsVerdict.From([Top(1, "HADR_SYNC_COMMIT", "Other", 10)]));
        Assert.Equal("Most waiting is on less common waits: 100 %, mostly HADR_SYNC_COMMIT", other.Headline);
        Assert.Equal(TopWaitsVerdict.Trend, Assert.Single(other.Actions).Key);

        Assert.Equal(VerdictLevel.Ok, TopWaitsVerdict.From([]).Level);
        Assert.Equal(VerdictLevel.Ok, TopWaitsVerdict.From([Top(1, "X", "CPU", 0)]).Level);
    }

    // ── Wait Trend ───────────────────────────────────────────────────────

    [Fact]
    public void WaitTrend_NamesTheWaitTypeThatWaitsMost_AndWhetherItRises()
    {
        WaitTrendSeries[] lines =
        [
            new("CXPACKET", [10, 10, 12, 30, 40, 50]),
            new("WRITELOG", [5, 5, null, 5, 5, 5]),
        ];
        var verdict = Invariant(() => WaitTrendVerdict.From(lines, WaitTrendState.Live, "ms/s", "over the last minute"));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("CXPACKET waits the most: 25.3 ms/s on average over the last minute, and rising", verdict.Headline);
        Assert.Equal("84 % of the waiting drawn. Then WRITELOG, 5. Its last readings are at least twice its first.", verdict.Why);

        var steady = Invariant(() => WaitTrendVerdict.From([new("WRITELOG", [5, 5, 5, 5, 5, 5])], WaitTrendState.History, "ms/s", "over the last hour"));
        Assert.Equal((VerdictLevel.Info, "WRITELOG waits the most: 5 ms/s on average over the last hour"), (steady.Level, steady.Headline));
    }

    [Fact]
    public void WaitTrend_NothingToJudge_SaysWhy()
    {
        Assert.Equal("No wait type is selected", WaitTrendVerdict.From([], WaitTrendState.Live, "ms/s", "").Headline);

        WaitTrendSeries[] empty = [new("CXPACKET", [])];
        Assert.Equal((VerdictLevel.Unknown, "Not trending yet"), Pair(WaitTrendVerdict.From(empty, WaitTrendState.NotStarted, "ms/s", "")));
        Assert.Equal((VerdictLevel.Info, "Reading the first samples"), Pair(WaitTrendVerdict.From(empty, WaitTrendState.Live, "ms/s", "")));
        var history = WaitTrendVerdict.From(empty, WaitTrendState.History, "ms/s", "", noData: "Not recorded: monitoring was off.");
        Assert.Equal((VerdictLevel.Unknown, "Nothing recorded in this range", "Not recorded: monitoring was off."), (history.Level, history.Headline, history.Why));

        var zero = WaitTrendVerdict.From([new("CXPACKET", [0, 0, 0])], WaitTrendState.Live, "ms/s", "over the last minute");
        Assert.Equal((VerdictLevel.Ok, "No waiting on the wait types shown"), Pair(zero));
    }

    private static (VerdictLevel, string) Pair(PageVerdict v) => (v.Level, v.Headline);

    [Fact]
    public void WaitTrend_RisingNeedsEnoughPoints()
    {
        Assert.False(WaitTrendVerdict.Rising([1, 1, 5, 5, 5]));
        Assert.True(WaitTrendVerdict.Rising([1, 1, 1, 2, 2, 2]));
        Assert.False(WaitTrendVerdict.Rising([0, 0, 0, 0, 0, 0]));
    }

    // ── Resource Queries ─────────────────────────────────────────────────

    private static ResourceIntensiveQuery Query(string? obj, long cpuUs, long reads, long runs = 100, string text = "SELECT * FROM dbo.Orders WHERE CustomerId = @p0") =>
        new(text, "SalesDb", obj, runs, reads, reads / runs, cpuUs, cpuUs / runs, cpuUs, cpuUs / runs, 0, 0, DateTime.Now, DateTime.Now, DateTime.Now);

    [Fact]
    public void ResourceQueries_NamesTheTopByCpu_AndByReads()
    {
        var verdict = Invariant(() => ResourceQueriesVerdict.From(
            [Query("dbo.usp_GetOrders", 6_000_000, 10), Query(null, 3_000_000, 10), Query("dbo.usp_X", 1_000_000, 10)],
            [Query(null, 1, 900_000), Query("dbo.usp_GetOrders", 1, 100_000)]));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);   // 60 % of the top 3's CPU
        Assert.Equal("dbo.usp_GetOrders uses the most CPU: 60 % of the top 3 queries'", verdict.Headline);
        Assert.Equal("100 runs, 60 ms of CPU each. By reads, an ad hoc query (SELECT * FROM dbo.Orders WHERE Customer…) reads the most: 900,000 pages, 90 % of the top 2. Counted since each plan was cached.",
                     verdict.Why);
        Assert.Equal([ResourceQueriesVerdict.ShowCpu, ResourceQueriesVerdict.ShowReads], verdict.Actions.Select(a => a.Key));

        var even = Invariant(() => ResourceQueriesVerdict.From([Query("a", 10, 1), Query("b", 10, 1)], []));
        Assert.Equal(VerdictLevel.Info, even.Level);
    }

    [Fact]
    public void ResourceQueries_EmptyCache_CantRank()
    {
        var verdict = ResourceQueriesVerdict.From([], []);
        Assert.Equal((VerdictLevel.Unknown, "The plan cache has no queries to rank"), (verdict.Level, verdict.Headline));
        Assert.Empty(verdict.Actions);
    }

    // ── Query Regressions ────────────────────────────────────────────────

    private static QueryRegression Regression(string key, double extra, bool newPlan = false) =>
        new(key, 1000, 200, 500, 2050, 310, 100, 100, 0, true, false, 311, 902, newPlan, extra);

    private static readonly RegressionCriteria Criteria = new(50, 5);

    [Fact]
    public void QueryRegressions_NamesTheWorst_WithItsPlans()
    {
        var verdict = Invariant(() => QueryRegressionsVerdict.From(
            [Regression("0x1A2B", 5_000), Regression("4183", 310_000, newPlan: true)], true, "Last 24 hours compared with the 7 days before", null, Criteria));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("2 queries got slower; query 4183 the most: runs 4.1× slower since it got a new plan", verdict.Headline);
        Assert.Equal("Last 24 hours compared with the 7 days before. Its slower runs cost 5.2 min more in all. Compare its plans now and before.", verdict.Why);
        Assert.Equal([new VerdictAction(QueryRegressionsVerdict.ShowPlans, "Show its plans"),
                      new VerdictAction(QueryRegressionsVerdict.OpenQuery, "Open query 4183 in Query Store", 4183)], verdict.Actions);

        // A query hash from the history has nowhere to open.
        var hash = Invariant(() => QueryRegressionsVerdict.From([Regression("0x1A2B", 500)], true, "x", null, Criteria));
        Assert.Equal(QueryRegressionsVerdict.ShowPlans, Assert.Single(hash.Actions).Key);
    }

    [Fact]
    public void QueryRegressions_NoneSlower_OrNothingToCompare()
    {
        var none = Invariant(() => QueryRegressionsVerdict.From([], true, "Last hour compared with the 7 days before", null, Criteria));
        Assert.Equal((VerdictLevel.Ok, "No query got slower"), (none.Level, none.Headline));
        Assert.Equal("Last hour compared with the 7 days before: none more than 50 % slower in duration or CPU, with at least 5 executions in each period.", none.Why);

        var nothing = QueryRegressionsVerdict.From([], false, "x", "Query Store is off for SalesDb.", Criteria);
        Assert.Equal((VerdictLevel.Unknown, "Nothing to compare", "Query Store is off for SalesDb."), (nothing.Level, nothing.Headline, nothing.Why));
    }

    // ── Plan Cache Health ────────────────────────────────────────────────

    private static PlanCacheHealthSummary Summary(bool erased = false, int multi = 0, int guides = 0, string severity = "OK") =>
        new(erased, multi, 1482, guides, 0, severity);

    [Fact]
    public void PlanCacheHealth_TheWorstFirst()
    {
        var erased = PlanCacheHealthVerdict.From(Summary(erased: true, multi: 4, severity: "CRITICAL"), 0);
        Assert.Equal((VerdictLevel.Critical, "The plan cache was emptied in the last 24 hours"), (erased.Level, erased.Headline));

        var multi = Invariant(() => PlanCacheHealthVerdict.From(Summary(multi: 4, severity: "WARNING"), 2));
        Assert.Equal((VerdictLevel.Warning, "4 queries have more than 5 cached plans each"), (multi.Level, multi.Headline));
        Assert.Equal(new VerdictAction(PlanCacheHealthVerdict.ShowTab, "Multi-plan queries", PlanCacheHealthVerdict.MultiPlanTab), Assert.Single(multi.Actions));
        Assert.EndsWith("1,482 plans cached.", multi.Why);

        var guides = PlanCacheHealthVerdict.From(Summary(guides: 1, severity: "WARNING"), 0);
        Assert.Equal((VerdictLevel.Warning, "1 plan guide freezes a plan"), (guides.Level, guides.Headline));

        var lookups = PlanCacheHealthVerdict.From(Summary(), 3);
        Assert.Equal((VerdictLevel.Info, "No parameterisation problems; 3 busy plans do key lookups"), (lookups.Level, lookups.Headline));
    }

    [Fact]
    public void PlanCacheHealth_NothingFound_IsHealthy()
    {
        var verdict = PlanCacheHealthVerdict.From(Summary(), 0);
        Assert.Equal((VerdictLevel.Ok, "The plan cache looks healthy"), (verdict.Level, verdict.Headline));
        Assert.Empty(verdict.Actions);
    }

    // ── Implicit Conversions ─────────────────────────────────────────────

    private static ImplicitConversion Conversion(string? obj, int runs) => new("SalesDb", obj, runs, 64, "SELECT 1", DateTime.Now);

    [Fact]
    public void ImplicitConversions_CountsThem_TheMostUsedFirst()
    {
        var verdict = Invariant(() => ImplicitConversionsVerdict.From([Conversion("usp_Find", 40), Conversion("", 12_400), Conversion("usp_Get", 3)]));
        Assert.Equal((VerdictLevel.Warning, "3 cached plans convert implicitly; the most used ran 12,400 times"), (verdict.Level, verdict.Headline));
        Assert.StartsWith("The most used is an ad hoc query in SalesDb.", verdict.Why);
        Assert.Equal(ImplicitConversionsVerdict.ShowWorst, Assert.Single(verdict.Actions).Key);

        var quiet = Invariant(() => ImplicitConversionsVerdict.From([Conversion("usp_Find", 40)]));
        Assert.Equal((VerdictLevel.Info, "1 cached plan converts implicitly; the most used ran 40 times"), (quiet.Level, quiet.Headline));
        Assert.StartsWith("The most used is SalesDb.usp_Find.", quiet.Why);

        var capped = Invariant(() => ImplicitConversionsVerdict.From(Enumerable.Range(1, 50).Select(i => Conversion("p", i)).ToList()));
        Assert.StartsWith("At least 50 cached plans convert implicitly", capped.Headline);
    }

    [Fact]
    public void ImplicitConversions_None_IsOk()
    {
        var verdict = ImplicitConversionsVerdict.From([]);
        Assert.Equal((VerdictLevel.Ok, "No cached plan converts implicitly"), (verdict.Level, verdict.Headline));
    }

    // ── SP Trace ─────────────────────────────────────────────────────────

    private static SpTraceEvent Call(string? proc, long ms, int spid = 61) =>
        new(DateTime.Now, "rpc_completed", proc, "P", "SalesDb", ms, 1, 10, 0, 0, 1, spid, "app", "OrderService", "WEB01", "EXEC x", null);

    private static readonly TraceSessionStatus Running = new(true, TraceMode.ExtendedEvents, "SqlVitals_SpTrace", null, 2, 0, false);

    [Fact]
    public void SpTrace_Stopped_EmptyOrWithCalls()
    {
        var idle = Invariant(() => SpTraceVerdict.From(TraceSessionStatus.Stopped("SqlVitals_SpTrace"), [], procedures: 12));
        Assert.Equal((VerdictLevel.Info, "Not capturing"), (idle.Level, idle.Headline));
        Assert.EndsWith("By Procedure lists 12 procedures from the server's counters.", idle.Why);
        Assert.Equal(SpTraceVerdict.Start, Assert.Single(idle.Actions).Key);

        var stopped = Invariant(() => SpTraceVerdict.From(TraceSessionStatus.Stopped("SqlVitals_SpTrace"), [Call("dbo.usp_A", 12), Call("dbo.usp_B", 840, 77)], 0));
        Assert.Equal("Stopped, with 2 calls captured", stopped.Headline);
        Assert.StartsWith("The slowest: dbo.usp_B, 840 ms (session 77).", stopped.Why);
        Assert.Equal([SpTraceVerdict.Start, SpTraceVerdict.ShowSlowest], stopped.Actions.Select(a => a.Key));
    }

    [Fact]
    public void SpTrace_Running_SaysWhatItCaptures()
    {
        var empty = Invariant(() => SpTraceVerdict.From(Running, [], 0, minDurationMs: 100));
        Assert.Equal((VerdictLevel.Ok, "Capturing; no call yet", "Calls taking at least 100 ms appear as they complete."), (empty.Level, empty.Headline, empty.Why));
        Assert.Equal(SpTraceVerdict.Stop, Assert.Single(empty.Actions).Key);

        var busy = Invariant(() => SpTraceVerdict.From(Running, [Call("dbo.usp_A", 12), Call(null, 30)], 0));
        Assert.Equal("Capturing: 2 calls so far", busy.Headline);
        Assert.Equal("The slowest: a call without a procedure name, 30 ms (session 61).", busy.Why);

        var dropped = Invariant(() => SpTraceVerdict.From(Running with { EventsDropped = 340 }, [Call("dbo.usp_A", 12)], 0));
        Assert.Equal((VerdictLevel.Warning, "Capturing, but 340 events were dropped"), (dropped.Level, dropped.Headline));

        var dmv = SpTraceVerdict.From(Running with { Mode = TraceMode.DmvFallback, Reason = "No ALTER ANY EVENT SESSION." }, [], 3);
        Assert.Equal((VerdictLevel.Info, "Capturing totals per procedure only"), (dmv.Level, dmv.Headline));
        Assert.StartsWith("No ALTER ANY EVENT SESSION.", dmv.Why);
    }

    // ── The drawer's rows ────────────────────────────────────────────────

    [Fact]
    public void DrawerRows_SayTheUnits()
    {
        var rows = Invariant(() => RowDetails.Rows(Query("dbo.usp_GetOrders", 6_000_000, 10_000)).Select(r => $"{r.Label}: {r.Value}").ToList());
        Assert.Contains("CPU: 6 s in all, 60.0 ms a run", rows);
        Assert.Contains("Logical reads: 10,000 in all, 100 a run", rows);

        var regression = Invariant(() => RowDetails.Rows(Regression("4183", 1, newPlan: true)));
        Assert.Contains(regression, r => r is { Label: "Plan", Value: "New plan: plan 311 before, plan 902 now", Warning: true });
        Assert.Contains(regression, r => r is { Label: "Duration", Value: "500.0 ms before, 2,050.0 ms now (+310 %)", Warning: true });

        Assert.Contains(Invariant(() => RowDetails.Rows(Call("dbo.usp_A", 1_250))), r => r is { Label: "Duration", Value: "1,250 ms" });
    }
}
