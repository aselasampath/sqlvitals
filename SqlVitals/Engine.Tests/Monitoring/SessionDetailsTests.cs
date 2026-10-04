using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Monitoring;

/// <summary>What the details drawer says about a session, and a Query Store row (#147).</summary>
public class SessionDetailsTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 14, 2, 14);

    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0, string status = "running", int openTran = 0,
                                    DateTime? tranBegin = null, string? waitType = null) =>
        new(spid, blockedBy, status, "svc_orders", "APP-WEB03", "OrderService", waitType ?? (blockedBy != 0 ? "LCK_M_X" : null), waitMs,
            null, blockedBy != 0 ? "KEY: 5:72057594045726720 (8194443284a0)" : null, "SalesDb", 120, 40, 2,
            Now.AddHours(-3), Now.AddMinutes(-4).AddSeconds(-12), openTran, "AWAITING COMMAND", "UPDATE dbo.Orders SET Status = @p0", "",
            tranBegin, Now);

    // 87 sleeps with an open transaction; 112 and 115 wait on it, 130 on 112.
    private static readonly IReadOnlyList<ProcessNode> Snapshot = BlockingChains.Classify(
    [
        Node(87, status: "sleeping", openTran: 1, tranBegin: Now.AddMinutes(-4).AddSeconds(-12)),
        Node(112, blockedBy: 87, waitMs: 245_000), Node(115, blockedBy: 87, waitMs: 10_000), Node(130, blockedBy: 112, waitMs: 5_000),
        Node(5),
    ]);

    private static IReadOnlyList<DetailRow> Rows(int spid) =>
        Invariant(() => SessionDetails.Rows(Snapshot.First(n => n.SessionId == spid), Snapshot));

    [Fact]
    public void AHeadBlocker_SaysWhatItBlocks_AndHowLongItsTransactionHasBeenOpen()
    {
        var rows = Rows(87).ToDictionary(r => r.Label);

        Assert.Equal(new DetailRow("Status", "sleeping · 1 open transaction", Warning: true), rows["Status"]);
        Assert.Equal(new DetailRow("Blocking", "3 sessions · longest 4.1 min", Warning: true), rows["Blocking"]);
        Assert.Equal(new DetailRow("Transaction open", "4 min · since 13:58:02", Warning: true), rows["Transaction open"]);
        Assert.Equal("13:58:02 · idle 4 min", rows["Last batch ended"].Value);
        Assert.Equal(["svc_orders", "APP-WEB03", "OrderService", "SalesDb"],
                     new[] { "Login", "Host", "Program", "Database" }.Select(l => rows[l].Value));
        Assert.False(rows.ContainsKey("Blocked by"));
    }

    [Fact]
    public void ABlockedSession_SaysByWhom_TheHeadOfItsChain_AndWhatItWaitsOn()
    {
        var rows = Rows(130).ToDictionary(r => r.Label);

        Assert.Equal("session 112 · head of chain 87", rows["Blocked by"].Value);
        Assert.Equal("LCK_M_X · 5 s · KEY: 5:72057594045726720 (8194443284a0)", rows["Waiting"].Value);
        Assert.Equal("session 87", Rows(112).Single(r => r.Label == "Blocked by").Value);
        Assert.Equal(new DetailRow("Blocking", "1 session directly", Warning: true), Rows(112).Single(r => r.Label == "Blocking"));
    }

    [Fact]
    public void TransactionAge_IsOnTheServersClock_AndNoneWithoutATransaction()
    {
        Assert.Equal(TimeSpan.FromSeconds(252), SessionDetails.TransactionAge(Snapshot[0]));
        Assert.Null(SessionDetails.TransactionAge(Node(5)));
        // A clock read before the transaction began (it began between the two reads): no age.
        Assert.Null(SessionDetails.TransactionAge(Node(5, tranBegin: Now.AddSeconds(1))));
        Assert.DoesNotContain(Rows(5), r => r.Label == "Transaction open");
    }

    [Fact]
    public void TheKillScript_IsForReview_AndSaysWhatKillingItUndoes()
    {
        var script = Invariant(() => SessionDetails.KillScript(Snapshot[0], Snapshot)).ReplaceLineEndings("\n");

        Assert.StartsWith("-- Review before running: SqlVitals never runs this.\n" +
                          "-- Session 87 · login svc_orders · host APP-WEB03 · program OrderService · database SalesDb\n" +
                          "-- It is blocking 3 sessions, the longest waiting 4.1 min.\n" +
                          "-- Its open transaction began at 13:58:02 (4 min ago). KILL rolls it back", script);
        Assert.Contains("\nDBCC INPUTBUFFER(87);\n", script);
        Assert.Contains("\nKILL 87;\n", script);
        Assert.EndsWith("KILL 87 WITH STATUSONLY;\n", script);
        Assert.DoesNotContain("system session", script);

        var system = Invariant(() => SessionDetails.KillScript(Node(42), [Node(42)]));
        Assert.Contains("-- Session 42 may be a system session, which can't be killed.", system);
    }

    // ── The SQL behind it ────────────────────────────────────────────────

    public static TheoryData<string, string> Statements => new()
    {
        { "Processes", WaitStatsRepository.ProcessesSql },
        { "Session plan", WaitStatsRepository.SessionPlanSql },
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void TheQueries_Parse_FromSqlServer2016On(string name, string sql)
    {
        foreach (TSqlParser parser in new TSqlParser[] { new TSql130Parser(true), new TSql160Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.True(errors.Count == 0, $"{name}: {string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}"))}");
        }
    }

    [Fact]
    public void TheSessionPlan_IsWhatItRuns_ElseItsLastStatementsCachedPlan()
    {
        var sql = WaitStatsRepository.SessionPlanSql;

        Assert.Contains("FROM sys.dm_exec_requests r", sql);
        Assert.Contains("qs.sql_handle = c.most_recent_sql_handle", sql);
        Assert.Contains("ORDER BY x.Ord", sql);
        Assert.Contains("tx.TransactionBegin", WaitStatsRepository.ProcessesSql);
    }

    // ── Query Store rows ─────────────────────────────────────────────────

    [Fact]
    public void AQueryStoreRow_ShowsItsTabsFigures()
    {
        var row  = new QueryStoreQueryRow(4183, 0, null, "SELECT 1", 2, 8_412, 3, 1_000, 18_120, 2_310, 41_000_000, 902, 902, new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));
        var rows = Invariant(() => QueryStoreDetails.Rows(row, "Last 24 hours")).ToDictionary(r => r.Label);

        Assert.Equal("ad hoc", rows["Object"].Value);
        Assert.Equal(new DetailRow("Plans", "2", Warning: true), rows["Plans"]);
        Assert.Equal(new DetailRow("Failed", "3", Warning: true), rows["Failed"]);
        Assert.Equal("2,310.0 ms", rows["Avg duration"].Value);
        Assert.Equal("Plan 902", rows["Forced plan"].Value);
        Assert.Equal((4183L, (long?)902, "SELECT 1"), QueryStoreDetails.QueryOf(row));
        Assert.Empty(QueryStoreDetails.Rows("not a row", "Last hour"));
        Assert.Null(QueryStoreDetails.QueryOf(null));
    }

    [Fact]
    public void APlan_IsOneLine()
    {
        var plan = new QueryStorePlanSummary(902, null, new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc).ToUniversalTime(), 200, 2_050, 100, 5_000, 1, true, 0, null);

        Assert.Equal("Plan 902 · forced", QueryStoreDetails.PlanTitle(plan));
        Assert.StartsWith("200 runs · 2,050.0 ms avg · 100.0 ms CPU · 1 failed · last ", Invariant(() => QueryStoreDetails.PlanLine(plan)));
        Assert.Equal("1 run", Invariant(() => QueryStoreDetails.PlanLine(plan with { Executions = 1, AvgDurationMs = null, AvgCpuMs = null, Failed = 0, LastUsedUtc = null })));
    }
}
