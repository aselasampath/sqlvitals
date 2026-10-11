using System.Globalization;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>A connection as a Home tile shows it (#130): status, score, reason, bars and button.</summary>
public sealed class ServerTileTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 21, 55, 30);

    // A quiet server: nothing near any default threshold.
    internal static LiveMetricSample Sample(
        double cpuPct = 10, double ple = 5000, double grants = 0, double blocked = 0, double blockSec = 0,
        double logPct = 5, double tempDbPct = 10) =>
        new(Now, 0, 0, 0, 0, 0, 0, cpuPct, 0, 0, 0, 0, 0, 0, ple, grants, 99.9, blocked, blockSec, logPct, "Sales", tempDbPct);

    internal static HealthScore Score(LiveMetricSample sample, DateTime? time = null) =>
        HealthScore.From(Monitoring.TestChecks.All(sample), time ?? Now.AddSeconds(-16));

    internal static TileInput Input(string name = "UAT", HealthScore? score = null, bool monitored = true, bool active = false,
                                    Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, " uat-sql01 ", "Sales", active, monitored, score);

    private static ServerTile Tile(TileInput input) => ServerTile.From(input, Now);

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    [Fact]
    public void Critical_ShowsTheScoreAndWhatLoweredIt_CostliestFirst()
    {
        var tile = InCulture("en-GB", () => Tile(Input(score: Score(Sample(cpuPct: 81, blocked: 3, blockSec: 180)))));

        Assert.Equal(TileStatus.Critical, tile.Status);
        Assert.Equal("CRITICAL", tile.StatusLabel);
        Assert.Equal(HealthLevel.Critical, tile.Level);
        Assert.Equal("76", tile.ScoreText);
        Assert.Equal("Lowered by Blocking −16 · CPU −8.", tile.Reason);
        Assert.Equal("uat-sql01 / Sales", tile.ServerLine);
        Assert.Equal(TileAction.Open, tile.Action);
        Assert.Equal("Open", tile.ActionText);
        Assert.True(tile.CanAct);
    }

    [Fact]
    public void Warning_AndHealthy_HaveTheirOwnStatusAndReason()
    {
        var warning = Tile(Input(score: Score(Sample(ple: 95))));
        var healthy = Tile(Input(score: Score(Sample())));

        Assert.Equal((TileStatus.Warning, "WARNING", "95", "Lowered by Page life expectancy −5."),
                     (warning.Status, warning.StatusLabel, warning.ScoreText, warning.Reason));
        Assert.Equal((TileStatus.Healthy, "HEALTHY", "100", "Every check is within its thresholds."),
                     (healthy.Status, healthy.StatusLabel, healthy.ScoreText, healthy.Reason));
    }

    [Fact]
    public void Footer_SaysWhenTheSampleWasTaken_AndWhetherItsTheActiveConnection()
    {
        Assert.Equal("Sample 21:55:14 · active", Tile(Input(score: Score(Sample()), active: true)).Footer);
        Assert.Equal("Sample 21:55:14 · background", Tile(Input(score: Score(Sample()))).Footer);
    }

    [Fact]
    public void Footer_GivesTheDate_ForASampleFromAnotherDay()
    {
        var tile = InCulture("", () => Tile(Input(score: Score(Sample(), new DateTime(2026, 9, 30, 23, 59, 0)))));

        Assert.Equal("Sample 30 Sep 23:59 · background", tile.Footer);
    }

    [Fact]
    public void Unreachable_ScoresZero_SaysSinceWhen_AndOffersRetry()
    {
        var input = Input(score: HealthScore.Unavailable("Monitoring failed at 21:41:05: Login timeout expired.", Now)) with
        {
            FailingSince = new DateTime(2026, 10, 1, 21, 41, 5),
            LastSuccess  = new DateTime(2026, 10, 1, 21, 40, 58),
            LastError    = "Login timeout expired.",
        };

        var tile = Tile(input);

        Assert.Equal(TileStatus.Unreachable, tile.Status);
        Assert.Equal("UNREACHABLE", tile.StatusLabel);
        Assert.Equal("0", tile.ScoreText);
        Assert.Equal("Can't be reached or read since 21:41:05.", tile.Reason);
        Assert.Equal("Login timeout expired.", tile.ReasonToolTip);
        Assert.Equal("Last good sample 21:40:58", tile.Footer);
        Assert.Equal(TileAction.Retry, tile.Action);
        Assert.All(tile.Bars, b => Assert.Equal(HealthLevel.Unknown, b.Level));
    }

    [Fact]
    public void Unreachable_BeforeAnySampleWorked_SaysSo()
    {
        var tile = Tile(Input(score: HealthScore.Unavailable("failed", Now)));

        Assert.Equal("Can't be reached or read.", tile.Reason);
        Assert.Equal("failed", tile.ReasonToolTip);
        Assert.Equal("No sample has worked yet", tile.Footer);
    }

    [Fact]
    public void NotMonitored_HasNoScore_SaysWhy_AndOffersStart()
    {
        var input = Input(score: Score(Sample()), monitored: false) with
        {
            NotMonitoredReason = "Not monitored: the password isn't saved.",
            CanStart           = false,
        };

        var tile = Tile(input);

        Assert.Equal(TileStatus.Off, tile.Status);
        Assert.Equal("OFF", tile.StatusLabel);
        Assert.Equal("—", tile.ScoreText);
        Assert.Equal(HealthLevel.Unknown, tile.Level);
        Assert.Equal("Not monitored, so there's no score yet.", tile.Reason);
        Assert.Equal("Not monitored: the password isn't saved.", tile.Footer);
        Assert.Equal(TileAction.Start, tile.Action);
        Assert.Equal("▶ Start monitoring", tile.ActionText);
        Assert.False(tile.CanAct);
        Assert.All(tile.Bars, b => Assert.EndsWith("no figure, not monitored", b.ToolTip));
    }

    [Fact]
    public void Monitored_BeforeTheFirstSample_IsWaiting()
    {
        var tile = Tile(Input(score: null, active: true));

        Assert.Equal(TileStatus.Waiting, tile.Status);
        Assert.Equal("—", tile.ScoreText);
        Assert.Equal("Waiting for the first sample…", tile.Reason);
        Assert.Equal("Monitoring · active", tile.Footer);
        Assert.Equal(TileAction.Open, tile.Action);
    }

    // ── Bars ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Bars_AreTheNineChecks_InThePopupsOrder_ColouredByLevel_WithFigureAndThresholdsInTheTooltip()
    {
        var tile = InCulture("en-GB", () => Tile(Input(score: Score(Sample(cpuPct: 81, blocked: 3, blockSec: 180)))));

        Assert.Equal(["CPU", "Block", "PLE", "Grants", "Log", "Temp", "Disk", "RPO", "Jobs"], tile.Bars.Select(b => b.Label));
        Assert.Equal([HealthLevel.Warning, HealthLevel.Critical, .. Enumerable.Repeat(HealthLevel.Healthy, 7)],
                     tile.Bars.Select(b => b.Level));
        Assert.Equal("CPU: 81 % (warning ≥ 75 %, critical ≥ 90 %) · −8", tile.Bars[0].ToolTip);
        Assert.DoesNotContain("−", tile.Bars[2].ToolTip);
    }

    // ── The worst state first (#173) ──────────────────────────────────────────

    [Fact]
    public void Headline_LeadsWithTheWorstStateAndItsReason()
    {
        var critical = Tile(Input(score: Score(Sample(blocked: 3, blockSec: 240))));

        Assert.Equal("Critical: blocking for 4 min", critical.Headline);
        Assert.Equal("84", critical.ScoreText);
        Assert.Equal("Not monitored", Tile(Input(monitored: false)).Headline);
        Assert.Equal("Waiting for the first sample", Tile(Input(score: null)).Headline);
        Assert.Equal("Unreachable", Tile(Input(score: HealthScore.Unavailable("Login failed.", Now))).Headline);
    }

    [Fact]
    public void ACheckWithNoData_IsAGreyBar_AndTheReasonSaysItWasLeftOut()
    {
        var tile = Tile(Input(score: Score(Sample(cpuPct: 80, tempDbPct: 0))));

        var tempDb = tile.Bars.Single(b => b.Indicator == HealthIndicator.TempDbSpace);
        Assert.Equal((HealthLevel.Unknown, "TempDB space used: no data, so it's left out of the score"), (tempDb.Level, tempDb.ToolTip));
        Assert.Equal("Lowered by CPU −9. 1 check has no data.", tile.Reason);
        Assert.Equal("1 check left out of the score, no data: TempDB space used", tile.ReasonToolTip);
        Assert.Equal("Every check that reported is within its thresholds. 1 check has no data.",
                     Tile(Input(score: Score(Sample(tempDbPct: 0)))).Reason);
    }

    [Fact]
    public void ABarWeighted0_SaysItIsntScored()
    {
        var weights = HealthWeights.Default.With(HealthIndicator.Cpu, 0);
        var score   = HealthScore.From(HealthRules.Grade(Sample(cpuPct: 80), HealthThresholds.Default), Now, weights);

        var cpu = Tile(Input(score: score)).Bars.Single(b => b.Indicator == HealthIndicator.Cpu);

        Assert.Equal(HealthLevel.Warning, cpu.Level);
        Assert.EndsWith(" · not scored, weight 0", cpu.ToolTip);
    }

    [Fact]
    public void Bars_WithNoScore_AreAGreyBarPerCheck()
    {
        var tile = Tile(Input(score: null));

        Assert.Equal(Enum.GetValues<HealthIndicator>().Length, tile.Bars.Count);
        Assert.All(tile.Bars, b => Assert.Equal(HealthLevel.Unknown, b.Level));
    }

    // ── Words for screen readers ──────────────────────────────────────────────

    [Fact]
    public void AutomationName_SaysTheStatusAndScoreInWords()
    {
        Assert.Equal("UAT, critical: blocking for 3 min (+1 more), score 76 of 100",
                     Tile(Input(score: Score(Sample(cpuPct: 81, blocked: 3, blockSec: 180)))).AutomationName);
        Assert.Equal("Production, healthy: every check passes, score 100 of 100, the active connection",
                     Tile(Input("Production", Score(Sample()), active: true)).AutomationName);
        Assert.Equal("Dev, not monitored", Tile(Input("Dev", monitored: false)).AutomationName);
    }

    // ── Order ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Sort_PutsUnreachableAndCriticalFirst_ThenWarningHealthyAndOff_ByNameWithin()
    {
        var tiles = new[]
        {
            Tile(Input("Zulu off", monitored: false)),
            Tile(Input("Reporting", Score(Sample()))),
            Tile(Input("production", Score(Sample(ple: 95)), active: true)),
            Tile(Input("UAT", Score(Sample(blocked: 1, blockSec: 180)))),
            Tile(Input("DR replica", HealthScore.Unavailable("failed", Now))),
            Tile(Input("Alpha off", monitored: false)),
            Tile(Input("Waiting", score: null)),
        };

        Assert.Equal(["DR replica", "UAT", "production", "Reporting", "Waiting", "Alpha off", "Zulu off"],
                     TileOrder.Sort(tiles).Select(t => t.Name));
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_CountsTheConnectionsMonitoredAndEachStatus()
    {
        var tiles = new[]
        {
            Tile(Input("UAT", Score(Sample(blocked: 1, blockSec: 180)))),
            Tile(Input("DR", HealthScore.Unavailable("failed", Now))),
            Tile(Input("Prod", Score(Sample(ple: 95)))),
            Tile(Input("Rep", Score(Sample()))),
            Tile(Input("Dev", monitored: false)),
        };

        var summary = HomeSummary.From(tiles);

        Assert.Equal("5 connections · 4 monitored · sorted by most attention needed", summary.Headline);
        Assert.Equal(["1 unreachable", "1 critical", "1 warning", "1 healthy"], summary.Counts.Select(c => c.Text));
    }

    [Fact]
    public void Summary_OfOneConnection_DoesntTalkAboutSorting_AndListsNoEmptyCounts()
    {
        var summary = HomeSummary.From([Tile(Input("Dev", monitored: false))]);

        Assert.Equal("1 connection · 0 monitored", summary.Headline);
        Assert.Empty(summary.Counts);
    }
}
