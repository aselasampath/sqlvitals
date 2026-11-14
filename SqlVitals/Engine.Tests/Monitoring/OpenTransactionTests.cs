using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Tests.Monitoring;

/// <summary>
/// The open transaction check (#252): the oldest open transaction that holds locks or log, graded
/// in minutes with every sample, worded with who holds it and what it stops the log from reusing.
/// </summary>
public class OpenTransactionTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

    private static OpenTransaction Transaction(double minutes, string? status = "sleeping", string? program = "OrderService",
                                               string? host = "WEB01", string? database = "Sales",
                                               string? logReuseWait = "ACTIVE_TRANSACTION") =>
        new(minutes * 60, 87, "sales_app", host, program, status, database, logReuseWait);

    private static LiveMetricSample Sample(OpenTransaction? transaction) =>
        new(T0, 0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0, 5000, 0, 99.9, LogUsedPct: 5, LogUsedDatabase: "Sales", TempDbUsedPct: 10,
            OldestTransaction: transaction);

    private static HealthReading Read(OpenTransaction? transaction, HealthThresholds? thresholds = null) =>
        InCulture("en-GB", () => HealthRules.Grade(Sample(transaction), thresholds ?? HealthThresholds.Default)
                                            .Single(r => r.Indicator == HealthIndicator.OpenTransaction));

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── The check ─────────────────────────────────────────────────────────────

    [Fact]
    public void ItIsASampleCheck_OnEveryServer_WarningAt5MinAndCriticalAt20()
    {
        Assert.Contains(HealthIndicator.OpenTransaction, HealthRules.SampleIndicators);
        Assert.False(ServerChecks.IsServerCheck(HealthIndicator.OpenTransaction));
        Assert.False(HealthRules.IsAzureOnly(HealthIndicator.OpenTransaction));

        var info = HealthThresholds.Info(HealthIndicator.OpenTransaction);
        Assert.Equal(("Open transaction", "min", false, new HealthThreshold(5, 20)), (info.Name, info.Unit, info.LowerIsWorse, info.Default));
        Assert.Equal(new HealthThreshold(5, 20), HealthThresholds.Default.Get(HealthIndicator.OpenTransaction));
        Assert.Equal(8, HealthThresholds.Default.Weight(HealthIndicator.OpenTransaction));
        Assert.Equal("Tran", ServerTile.ShortName(HealthIndicator.OpenTransaction));
    }

    [Theory]
    [InlineData(0,    HealthLevel.Healthy)]
    [InlineData(4.9,  HealthLevel.Healthy)]
    [InlineData(5,    HealthLevel.Warning)]
    [InlineData(19.9, HealthLevel.Warning)]
    [InlineData(20,   HealthLevel.Critical)]
    [InlineData(600,  HealthLevel.Critical)]
    public void TheOldestTransaction_IsGradedInMinutes(double minutes, HealthLevel expected)
    {
        var reading = Read(Transaction(minutes));

        Assert.Equal((expected, minutes, true), (reading.Level, reading.Value, reading.Measured));
    }

    [Fact]
    public void NoOpenTransaction_IsAFigure_0Min_AndHealthy()
    {
        var reading = Read(null);

        Assert.Equal((HealthLevel.Healthy, 0d, true), (reading.Level, reading.Value, reading.Measured));
        Assert.Null(reading.Reason);
    }

    [Fact]
    public void ItsThresholds_AreTheConnections()
    {
        var thresholds = HealthThresholds.Default.With(HealthIndicator.OpenTransaction, new HealthThreshold(60, null));

        Assert.Equal(HealthLevel.Healthy, Read(Transaction(30), thresholds).Level);
        Assert.Equal(HealthLevel.Warning, Read(Transaction(90), thresholds).Level);
    }

    // ── The words ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheReason_NamesTheSession_WhoHoldsIt_AndWhatTheLogWaitsOn()
    {
        Assert.Equal("Transaction open 23 min (session 87 sleeping, sales_app, OrderService on WEB01, Sales log waiting on ACTIVE_TRANSACTION)",
                     Read(Transaction(23)).Reason);
        Assert.Equal("Transaction open 1.5 h (session 87 running, sales_app, OrderService on WEB01, Sales log waiting on ACTIVE_TRANSACTION)",
                     Read(Transaction(90, status: "running")).Reason);
    }

    [Fact]
    public void TheReason_LeavesOutWhatTheServerDidntSay()
    {
        Assert.Equal("Transaction open 6 min (session 87, sales_app, WEB01, Sales)",
                     Read(Transaction(6, status: null, program: " ", logReuseWait: null)).Reason);
        Assert.Equal("Transaction open 6 min (session 87 sleeping, sales_app, OrderService)",
                     Read(Transaction(6, host: null, database: null)).Reason);
    }

    [Fact]
    public void ThePhrase_SaysHowLongAndWhichSession()
    {
        var phrase = InCulture("en-GB", () => HealthRules.Phrase(Read(Transaction(23))));

        Assert.Equal("transaction open 23 min in session 87", phrase);
    }

    [Fact]
    public void TheScoresFigure_SaysNoneOpen_OrUnderAMinute()
    {
        static HealthScoreCheck Check(OpenTransaction? t) =>
            HealthScore.From(HealthRules.Grade(Sample(t), HealthThresholds.Default), T0).Checks.Single(c => c.Indicator == HealthIndicator.OpenTransaction);

        var (none, young, old) = InCulture("en-GB", () =>
            (Check(null).ValueText, Check(Transaction(0.2)).ValueText, Check(Transaction(23)).ValueText));

        Assert.Equal(("None open", "< 1 min", "23 min"), (none, young, old));
    }

    [Fact]
    public void Regrade_KeepsWhoHoldsIt()
    {
        var reading  = Read(Transaction(8));
        var regraded = InCulture("en-GB", () => HealthRules.Regrade(reading,
                           HealthThresholds.Default.With(HealthIndicator.OpenTransaction, new HealthThreshold(2, 6))));

        Assert.Equal(HealthLevel.Critical, regraded.Level);
        Assert.Equal(reading.Reason, regraded.Reason);
    }

    // ── Alerts ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Transaction open 23 min (session 87 sleeping, sales_app)", 87)]
    [InlineData("Transaction open 1.5 h (session 1204, x)",                1204)]
    [InlineData("Transaction open 6 min (session 55)",                     55)]
    [InlineData("Transaction open 6 min",                                  null)]
    [InlineData("Transaction open 6 min (session x)",                      null)]
    [InlineData("Transaction open 6 min (session 0)",                      null)]
    [InlineData("Blocking 3.5 min (session 87)",                           null)]
    [InlineData("",                                                        null)]
    [InlineData(null,                                                      null)]
    public void TransactionSession_IsTheSessionTheCheckNamed(string? detail, int? expected) =>
        Assert.Equal(expected, AlertText.TransactionSession(detail));

    [Fact]
    public void TransactionSession_ReadsWhatTheHealthCheckWrites() =>
        Assert.Equal(87, AlertText.TransactionSession(Read(Transaction(30)).Reason));

    [Fact]
    public void AnAlert_IsWordedInMinutes_AndSaysWhatTheTransactionHolds()
    {
        static Alert Of(string detail) =>
            new(Guid.NewGuid(), Guid.NewGuid(), HealthIndicator.OpenTransaction, HealthLevel.Critical, T0, null, 23, 20, detail);
        var holding = Of("Transaction open 23 min (session 87 sleeping, sales_app, Sales log waiting on ACTIVE_TRANSACTION)");
        var reading = Of("Transaction open 23 min (session 87 sleeping, sales_app, Sales)");

        var (value, figure, happening, context, onlyLocks) = InCulture("en-GB", () =>
            (AlertText.Value(holding.Indicator, 75), AlertText.Figure(holding), AlertText.Happening(holding), AlertText.Context(holding),
             AlertText.Happening(reading)));

        Assert.Equal("1.3 h", value);
        Assert.Equal("23 min (threshold ≥ 20 min)", figure);
        Assert.Equal("A transaction has been open for 23 min (critical at ≥ 20 min): it holds its locks, and its database's log " +
                     "can't be reused past it. Transaction open 23 min (session 87 sleeping, sales_app, Sales log waiting on ACTIVE_TRANSACTION).",
                     happening);
        Assert.Equal(holding.Detail, context);
        // One that has written nothing doesn't hold the log: only its locks are said.
        Assert.Equal("A transaction has been open for 23 min (critical at ≥ 20 min): it holds its locks. " +
                     "Transaction open 23 min (session 87 sleeping, sales_app, Sales).", onlyLocks);
    }

    [Theory]
    [InlineData("10 min", 10)]
    [InlineData("10 mins", 10)]
    [InlineData(" 10 MIN ", 10)]
    [InlineData("10", 10)]
    public void ThresholdsTyped_InMinutes_AreRead(string typed, double expected) =>
        Assert.True(InCulture("en-GB", () => HealthThresholds.TryParse(HealthIndicator.OpenTransaction, typed, "30", out var t, out _) &&
                                             t == new HealthThreshold(expected, 30)));

    [Fact]
    public void ThresholdsTyped_InAnotherUnit_AreRefused()
    {
        // Seconds aren't minutes, and a check in seconds or % takes no minutes.
        Assert.False(InCulture("en-GB", () => HealthThresholds.TryParse(HealthIndicator.OpenTransaction, "300 s", "", out _, out _)));
        Assert.False(InCulture("en-GB", () => HealthThresholds.TryParse(HealthIndicator.Blocking, "2 min", "", out _, out _)));
        Assert.False(InCulture("en-GB", () => HealthThresholds.TryParse(HealthIndicator.Cpu, "80 min", "", out _, out _)));
        Assert.False(HealthThresholds.TryParse(HealthIndicator.OpenTransaction, "30", "10", out _, out _));
        // As before: seconds and % for the checks in them.
        Assert.True(InCulture("en-GB", () => HealthThresholds.TryParse(HealthIndicator.Blocking, "30 s", "120s", out _, out _)));
        Assert.True(InCulture("en-GB", () => HealthThresholds.TryParse(HealthIndicator.Cpu, "75 %", "90", out _, out _)));
    }

    // ── Processes' verdict ────────────────────────────────────────────────────

    private static readonly DateTime ServerNow = new(2026, 11, 14, 9, 0, 0);

    private static ProcessNode Node(int spid, string status, double? tranMinutes, int blockedBy = 0, long waitMs = 0) =>
        new(spid, blockedBy, status, "sales_app", "WEB01", "OrderService", blockedBy != 0 ? "LCK_M_X" : null, waitMs,
            null, null, "Sales", 0, 0, 0, DateTime.MinValue, DateTime.MinValue, tranMinutes is null ? 0 : 1, null, null, "",
            tranMinutes is { } m ? ServerNow.AddMinutes(-m) : null, ServerNow);

    private static PageVerdict Processes(params ProcessNode[] nodes) =>
        InCulture("en-GB", () => ProcessesVerdict.From(BlockingChains.Classify(nodes)));

    [Fact]
    public void Processes_WithNothingBlocked_NamesASessionSleepingWithATransactionOpen()
    {
        var verdict = Processes(Node(60, "running", 50), Node(87, "sleeping", 23), Node(91, "sleeping", 8), Node(95, "sleeping", null));

        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("No blocking, but session 87 has held a transaction open for 23 min", verdict.Headline);
        Assert.Equal("Session 87 (OrderService, sales_app) is sleeping with it open: the application most likely didn't commit. " +
                     "It holds its locks, and if it has written anything, Sales's log can't be reused past it. " +
                     "4 sessions, none waiting on another.", verdict.Why);
        Assert.Equal([new VerdictAction(ProcessesVerdict.GoToSession, "Go to session 87", 87)], verdict.Actions);
    }

    [Fact]
    public void Processes_ASleepingTransactionPastWarning_IsAWarning_AndOneUnderIt_IsNothing()
    {
        Assert.Equal(VerdictLevel.Warning, Processes(Node(87, "sleeping", 6)).Level);
        Assert.Equal((VerdictLevel.Ok, "No blocking"), (Processes(Node(87, "sleeping", 4)).Level, Processes(Node(87, "sleeping", 4)).Headline));
        // A running request's own transaction is a long statement, which the health check words.
        Assert.Equal(VerdictLevel.Ok, Processes(Node(87, "running", 40)).Level);
    }

    [Fact]
    public void Processes_TheConnectionsThreshold_DecidesWhenItIsNamed()
    {
        var nodes = BlockingChains.Classify([Node(87, "sleeping", 23)]);

        Assert.Equal(VerdictLevel.Ok, ProcessesVerdict.From(nodes, openTransaction: new HealthThreshold(30, 60)).Level);
        Assert.Equal(VerdictLevel.Ok, ProcessesVerdict.From(nodes, openTransaction: new HealthThreshold(null, null)).Level);
        Assert.Equal(VerdictLevel.Critical, ProcessesVerdict.From(nodes, openTransaction: new HealthThreshold(null, 10)).Level);
    }

    // ── The session an alert is looked into on ───────────────────────────────

    [Fact]
    public void TransactionHolder_IsTheNamedSession_WhileItHoldsOne()
    {
        var nodes = new[] { Node(87, "sleeping", 23), Node(91, "sleeping", 40) };

        Assert.Equal(87, SessionDetails.TransactionHolder(nodes, 87)?.SessionId);
    }

    [Fact]
    public void TransactionHolder_OnceTheNamedOneHoldsNone_IsTheOldestOpenNow()
    {
        // 87 committed, or its number was reused by a new connection holding nothing.
        var nodes = new[] { Node(87, "sleeping", null), Node(91, "sleeping", 8), Node(95, "running", 12), Node(99, "sleeping", null) };

        Assert.Equal(95, SessionDetails.TransactionHolder(nodes, 87)?.SessionId);
        Assert.Equal(95, SessionDetails.TransactionHolder(nodes, 200)?.SessionId);
        Assert.Null(SessionDetails.TransactionHolder([Node(87, "sleeping", null)], 87));
        Assert.Null(SessionDetails.TransactionHolder([], 87));
    }

    [Fact]
    public void Processes_WithBlocking_StillLeadsWithTheChain()
    {
        var verdict = Processes(Node(87, "sleeping", 30), Node(112, "suspended", null, blockedBy: 87, waitMs: 245_000));

        Assert.StartsWith("Session 87 is blocking 1 session", verdict.Headline);
    }
}
