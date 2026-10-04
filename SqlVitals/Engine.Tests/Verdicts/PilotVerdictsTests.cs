using System.Globalization;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Tests.Verdicts;

/// <summary>The pilot pages' verdicts (#146): what is wrong, or that nothing is, in plain words.</summary>
public class PilotVerdictsTests
{
    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── The verdict itself ───────────────────────────────────────────────

    [Fact]
    public void AVerdict_HasAtMostFourActions_AndSaysSomething()
    {
        var five = Enumerable.Range(1, 5).Select(i => new VerdictAction($"a{i}", $"Action {i}")).ToList();

        Assert.Throws<ArgumentException>(() => new PageVerdict(VerdictLevel.Ok, "Fine", actions: five));
        Assert.Throws<ArgumentException>(() => new PageVerdict(VerdictLevel.Ok, " "));
        Assert.Equal(4, new PageVerdict(VerdictLevel.Ok, "Fine", actions: five.Take(4).ToList()).Actions.Count);
    }

    [Fact]
    public void AVerdict_SaysItsLevelInWords_AndComparesByValue()
    {
        Assert.Equal(["OK", "Note", "Warning", "Critical", "Unknown"],
                     Enum.GetValues<VerdictLevel>().Select(l => new PageVerdict(l, "x").LevelText));
        Assert.Equal(new PageVerdict(VerdictLevel.Warning, "x", "y", [new("k", "K", 1)]),
                     new PageVerdict(VerdictLevel.Warning, "x", "y", [new("k", "K", 1)]));
        Assert.Null(new PageVerdict(VerdictLevel.Ok, "x", "  ").Why);
    }

    [Fact]
    public void AnUnreadablePage_SaysSo_AndHowToRetry()
    {
        var verdict = PageVerdict.Unreadable("the sessions", "Error [GetProcessesAsync]: Timeout expired.");

        Assert.Equal((VerdictLevel.Unknown, "Couldn't read the sessions"), (verdict.Level, verdict.Headline));
        Assert.Equal("Error [GetProcessesAsync]: Timeout expired. Refresh to try again.", verdict.Why);
        Assert.Equal("Refresh to try again.", PageVerdict.Unreadable("the sessions", null).Why);
    }

    // ── Processes ────────────────────────────────────────────────────────

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0, string status = "running", int openTran = 0,
                                    string? program = "OrderService", string? login = "svc_orders") =>
        new(spid, blockedBy, status, login, "APP-WEB03", program, blockedBy != 0 ? "LCK_M_X" : null, waitMs,
            null, null, "SalesDb", 0, 0, 0, DateTime.MinValue, DateTime.MinValue, openTran, null, null, "");

    private static PageVerdict Processes(params ProcessNode[] nodes) =>
        Invariant(() => ProcessesVerdict.From(BlockingChains.Classify(nodes)));

    [Fact]
    public void Processes_WithoutBlocking_SaysSo()
    {
        Assert.Equal(new PageVerdict(VerdictLevel.Ok, "No blocking", "3 sessions, none waiting on another."),
                     Processes(Node(5), Node(6), Node(7)));
        Assert.Equal("No user session is connected.", Processes().Why);
        Assert.Equal("1 session, not waiting on another.", Processes(Node(5)).Why);
    }

    [Fact]
    public void Processes_OneChain_NamesTheHeadBlocker_AndHowLongItHasBlocked()
    {
        var verdict = Processes(Node(87, status: "sleeping", openTran: 1),
                                Node(112, blockedBy: 87, waitMs: 245_000), Node(115, blockedBy: 87, waitMs: 10_000),
                                Node(130, blockedBy: 112, waitMs: 5_000));

        Assert.Equal(VerdictLevel.Critical, verdict.Level);   // past the 120 s default
        Assert.Equal("Session 87 is blocking 3 sessions, the longest for 4.1 min", verdict.Headline);
        Assert.Equal("Session 87 (OrderService, svc_orders) is sleeping with an open transaction: the application most likely didn't commit.",
                     verdict.Why);
        Assert.Equal([new VerdictAction(ProcessesVerdict.GoToHead, "Go to session 87", 87), new VerdictAction(ProcessesVerdict.ChainsOnly, "Blocking chains only")],
                     verdict.Actions);
    }

    [Theory]
    [InlineData(5_000,   VerdictLevel.Info)]       // under the 30 s warning
    [InlineData(45_000,  VerdictLevel.Warning)]
    [InlineData(130_000, VerdictLevel.Critical)]   // past the 120 s critical
    public void Processes_ColoursBlocking_ByTheBlockingThreshold(long waitMs, VerdictLevel level) =>
        Assert.Equal(level, Processes(Node(1), Node(2, blockedBy: 1, waitMs: waitMs)).Level);

    [Fact]
    public void Processes_UsesTheConnectionsOwnThreshold()
    {
        var nodes = BlockingChains.Classify([Node(1), Node(2, blockedBy: 1, waitMs: 5_000)]);

        Assert.Equal(VerdictLevel.Critical, ProcessesVerdict.From(nodes, new HealthThreshold(1, 3)).Level);
    }

    [Fact]
    public void Processes_SeveralChains_CountThem_AndNameTheLongest()
    {
        var verdict = Processes(
            Node(10), Node(11, blockedBy: 10, waitMs: 1_000), Node(12, blockedBy: 10, waitMs: 2_000),
            Node(20, status: "runnable"), Node(21, blockedBy: 20, waitMs: 60_000),
            Node(30), Node(31, blockedBy: 30, waitMs: 1_000),
            Node(40), Node(41, blockedBy: 40, waitMs: 1_000));

        Assert.Equal("4 head blockers are blocking 5 sessions; session 20 the longest, for 1 min", verdict.Headline);
        Assert.StartsWith("Head blockers 10: 2 blocked, 2 s · 20: 1 blocked, 1 min · 30: 1 blocked, 1 s · and 1 more.", verdict.Why);
        Assert.EndsWith("Session 20 (OrderService, svc_orders) is runnable.", verdict.Why);
        Assert.Equal(20, verdict.Actions[0].Id);
    }

    // ── Query Store ──────────────────────────────────────────────────────

    private static QueryStoreStatus Status(string state = "READ_WRITE") =>
        new("SalesDb", new QueryStoreHealth("SalesDb", state, state, 0, 100, 1000, 10, 900, 60, 30, "AUTO", "AUTO", null,
                                            200, 10, 12, 0, null, DateTime.Now), QueryStoreFeatures.None);

    private static QueryRegression Slower(string key, double pct, bool newPlan = false, double extraMs = 1000, bool duration = true) =>
        new(key, 100, 100, 100, 100 * (1 + pct / 100), duration ? pct : null, 10, 10, duration ? null : pct,
            duration, !duration, newPlan ? null : 1, 2, newPlan, extraMs);

    private const string Comparison = "Last 24 hours compared with the 7 days before";

    private static PageVerdict QueryStore(QueryStoreStatus status, params QueryRegression[]? regressions) =>
        Invariant(() => QueryStoreVerdict.From(status, regressions, Comparison, RegressionCriteria.Default));

    [Fact]
    public void QueryStore_NothingSlower_SaysSo()
    {
        var verdict = QueryStore(Status());

        Assert.Equal((VerdictLevel.Ok, "No query got slower"), (verdict.Level, verdict.Headline));
        Assert.Equal("Last 24 hours compared with the 7 days before: none more than 50 % slower, with at least 5 executions in each period.",
                     verdict.Why);
    }

    [Fact]
    public void QueryStore_OneQuerySlower_SaysByHowMuch_AndWhetherItsPlanChanged()
    {
        var verdict = QueryStore(Status(), Slower("4183", 310, newPlan: true));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("1 query got slower: query 4183 runs 4.1× slower since it got a new plan", verdict.Headline);
        Assert.Equal([new VerdictAction(QueryStoreVerdict.OpenQuery, "Open query 4183", 4183), new VerdictAction(QueryStoreVerdict.Regressions, "Query Regressions")],
                     verdict.Actions);
    }

    [Fact]
    public void QueryStore_SeveralSlower_NamesTheOneCostingMost()
    {
        var verdict = QueryStore(Status(), Slower("77", 120, extraMs: 9000, duration: false), Slower("12", 60));

        Assert.Equal("2 queries got slower; query 77 the most: uses 2.2× the CPU with a different plan", verdict.Headline);
        Assert.Equal("Last 24 hours compared with the 7 days before. Query Regressions lists each with its plans.", verdict.Why);
    }

    [Fact]
    public void QueryStore_Off_OrACustomPeriod_SaysWhyThereIsNoVerdict()
    {
        var off = QueryStore(Status("OFF"));
        Assert.Equal((VerdictLevel.Unknown, "Query Store isn't recording for SalesDb"), (off.Level, off.Headline));
        Assert.Equal("Query Store is off for SalesDb.", off.Why);

        var custom = Invariant(() => QueryStoreVerdict.From(Status(), null, Comparison, RegressionCriteria.Default));
        Assert.Equal((VerdictLevel.Info, "Not compared with a baseline"), (custom.Level, custom.Headline));

        var unread = Invariant(() => QueryStoreVerdict.From(Status(), null, Comparison, RegressionCriteria.Default, "It is off."));
        Assert.Equal((VerdictLevel.Unknown, "Couldn't compare with the 7 days before", "It is off."), (unread.Level, unread.Headline, unread.Why));
    }

    // ── Backups ──────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0);

    private static BackupInfo Ago(TimeSpan age) => new(Now - age - TimeSpan.FromMinutes(5), Now - age, 1_048_576, false, null);

    private static DatabaseBackupRow Db(string name, BackupInfo? full, BackupInfo? log = null, string recovery = "FULL", string state = "ONLINE") =>
        new(name, recovery, state, false, new DateTime(2024, 1, 1), false, true, true, full, null, log);

    private static PageVerdict Backups(params DatabaseBackupRow[] rows) =>
        Invariant(() => BackupsVerdict.From(BackupList.From(rows, Now, BackupRpo.Default)));

    private static readonly BackupInfo FreshFull = Ago(TimeSpan.FromDays(1));
    private static readonly BackupInfo FreshLog  = Ago(TimeSpan.FromMinutes(10));

    [Fact]
    public void Backups_AllWithinTheRpo_SaysSo()
    {
        var verdict = Backups(Db("Sales", FreshFull, FreshLog), Db("HR", FreshFull, recovery: "SIMPLE"), Db("Old", null, state: "OFFLINE"));

        Assert.Equal((VerdictLevel.Ok, "Every database is within your RPO"), (verdict.Level, verdict.Headline));
        Assert.Equal("2 databases checked for full backups older than 7 d, log backups older than 1 h. 1 not online, so not checked.", verdict.Why);
        Assert.Empty(verdict.Actions);
    }

    [Fact]
    public void Backups_OneLate_NamesIt()
    {
        var verdict = Backups(Db("Sales", FreshFull, Ago(TimeSpan.FromHours(3))), Db("HR", FreshFull, FreshLog));

        Assert.Equal(VerdictLevel.Warning, verdict.Level);
        Assert.Equal("Sales is past your RPO: log overdue", verdict.Headline);
        Assert.Equal([new VerdictAction(BackupsVerdict.ShowThem, "Show only it")], verdict.Actions);
    }

    [Fact]
    public void Backups_SeveralLate_CountThem_CriticalWhenOneCantBeRestored()
    {
        var verdict = Backups(Db("A", null), Db("B", Ago(TimeSpan.FromDays(9)), FreshLog), Db("C", FreshFull, Ago(TimeSpan.FromHours(2))),
                              Db("D", Ago(TimeSpan.FromDays(10)), FreshLog), Db("E", FreshFull, FreshLog));

        Assert.Equal(VerdictLevel.Critical, verdict.Level);   // A has no full backup at all
        Assert.Equal("4 databases are past your RPO", verdict.Headline);
        Assert.StartsWith("A: no full backup · ", verdict.Why);
        Assert.Contains("· and 1 more. Checked for full backups older than 7 d", verdict.Why);
        Assert.Equal("Show only these", Assert.Single(verdict.Actions).Text);
    }

    [Fact]
    public void Backups_ThatCantBeRead_OrWithNothingOnline_SayWhy()
    {
        var unavailable = BackupsVerdict.From(BackupList.Unavailable("Azure SQL Database takes its own backups.", BackupRpo.Default));
        Assert.Equal((VerdictLevel.Unknown, "Backups can't be checked here", "Azure SQL Database takes its own backups."),
                     (unavailable.Level, unavailable.Headline, unavailable.Why));

        var offline = Backups(Db("Old", null, state: "OFFLINE"));
        Assert.Equal((VerdictLevel.Unknown, "No online database to check"), (offline.Level, offline.Headline));
    }
}
