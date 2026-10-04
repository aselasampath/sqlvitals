using System.Globalization;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>What a server's Overview (#143) works out: what is costing points, its alerts in plain words, its headline numbers, and what happened since it was last looked at.</summary>
public sealed class OverviewTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 14, 0, 0, DateTimeKind.Utc);

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── What is costing points ───────────────────────────────────────────────

    public static TheoryData<HealthIndicator, HealthLevel> EveryCheckState()
    {
        var data = new TheoryData<HealthIndicator, HealthLevel>();
        foreach (var indicator in Enum.GetValues<HealthIndicator>())
            foreach (var level in new[] { HealthLevel.Healthy, HealthLevel.Warning, HealthLevel.Critical })
                data.Add(indicator, level);
        return data;
    }

    // One check at the level, every other one healthy.
    private static HealthScore ScoreWith(HealthIndicator indicator, HealthLevel level) =>
        HealthScore.From(HealthThresholds.Indicators.Select(i => new HealthReading(
            i.Indicator, i.Indicator == indicator ? level : HealthLevel.Healthy, 1, i.Default, null)).ToList(), Now);

    [Theory]
    [MemberData(nameof(EveryCheckState))]
    public void CostingPoints_MatchesTheScoresWeights_ForEveryCheckState(HealthIndicator indicator, HealthLevel level)
    {
        var score    = ScoreWith(indicator, level);
        var costs    = ScoreCosts.Lowering(score);
        var passing  = ScoreCosts.Passing(score);
        var weight   = HealthScore.Weight(indicator);
        var expected = level switch
        {
            HealthLevel.Warning  => weight / 2,
            HealthLevel.Critical => weight,
            _                    => 0,
        };

        // The list adds up to what the score lost, and the check costs half or all of its weight.
        Assert.Equal(HealthScore.Max - score.Value, costs.Sum(c => c.Points));
        if (expected == 0)
        {
            Assert.Empty(costs);
            Assert.Equal(HealthThresholds.Indicators.Count, passing.Count);
        }
        else
        {
            var cost = Assert.Single(costs);
            Assert.Equal((indicator, level, weight, expected), (cost.Indicator, cost.Level, cost.Weight, cost.Points));
            Assert.DoesNotContain(passing, p => p.Indicator == indicator);
            Assert.Equal(HealthThresholds.Indicators.Count - 1, passing.Count);
        }
    }

    [Fact]
    public void CostingPoints_CostliestFirst_EachWithWhyInOneLine()
    {
        var sample = ServerTileTests.Sample(cpuPct: 82, blocked: 3, blockSec: 180);
        var score  = HealthScore.From(HealthRules.Grade(sample, HealthThresholds.Default), Now);

        var costs = InCulture("en-GB", () => ScoreCosts.Lowering(score));

        Assert.Equal([HealthIndicator.Blocking, HealthIndicator.Cpu], costs.Select(c => c.Indicator));
        Assert.Equal("Now 3 min, past critical (≥ 2 min): all 20 of its points.", costs[0].Why);
        Assert.Equal("Now 82 %, past warning (≥ 75 %): half of its 20 points.", costs[1].Why);
        Assert.Equal(["Page life expectancy", "Memory grants pending", "Log space used", "TempDB space used"],
                     ScoreCosts.Passing(score).Select(p => p.Name));
    }

    // ── Alerts in plain words ────────────────────────────────────────────────

    private static Alert ActiveAlert(HealthIndicator indicator, HealthLevel severity, double value, double? threshold, string detail = "") =>
        new(Guid.NewGuid(), Guid.NewGuid(), indicator, severity, Now.AddMinutes(-25), null, value, threshold, detail);

    [Fact]
    public void Happening_SaysWhatIsGoingOn_WithTheThresholdCrossed()
    {
        var texts = InCulture("en-GB", () => new[]
        {
            AlertText.Happening(ActiveAlert(HealthIndicator.Cpu, HealthLevel.Critical, 97, 90)),
            AlertText.Happening(ActiveAlert(HealthIndicator.Blocking, HealthLevel.Warning, 45, 30, "Blocking 45s (3 blocked)")),
            AlertText.Happening(ActiveAlert(HealthIndicator.PageLifeExpectancy, HealthLevel.Warning, 120, 300)),
            AlertText.Happening(ActiveAlert(HealthIndicator.MemoryGrantsPending, HealthLevel.Warning, 1, 1)),
            AlertText.Happening(ActiveAlert(HealthIndicator.MemoryGrantsPending, HealthLevel.Warning, 4, 1)),
            AlertText.Happening(ActiveAlert(HealthIndicator.LogSpace, HealthLevel.Critical, 93, null, "Log 93% full (Sales)")),
            AlertText.Happening(ActiveAlert(HealthIndicator.TempDbSpace, HealthLevel.Warning, 85, 80)),
        });

        Assert.Equal(
        [
            "SQL Server's CPU reached 97 % (critical at ≥ 90 %).",
            "Requests are blocked: the longest has waited 45 s (warning at ≥ 30 s). Blocking 45s (3 blocked).",
            "Pages leave the buffer pool after only 120 s (warning at < 300 s): memory pressure.",
            "1 query is waiting for memory to start running (warning at ≥ 1).",
            "4 queries are waiting for memory to start running (warning at ≥ 1).",
            "The fullest transaction log is 93 % full. Log 93% full (Sales).",
            "TempDB's data files are 85 % full (warning at ≥ 80 %).",
        ], texts);
    }

    // ── Headline numbers ─────────────────────────────────────────────────────

    [Fact]
    public void Headlines_ColourCpuAndPageLifeByTheThresholds_AndLeaveTheOthersNeutral()
    {
        var samples = new[]
        {
            ServerTileTests.Sample(cpuPct: 40, ple: 5000) with { BatchRequestsPerSec = 1200, WaitingTasks = 2 },
            ServerTileTests.Sample(cpuPct: 92, ple: 250) with { BatchRequestsPerSec = 1500.4, WaitingTasks = 7 },
        };

        var headlines = InCulture("en-GB", () => Headlines.From(samples, HealthThresholds.Default));

        Assert.Equal([HeadlineKind.Cpu, HeadlineKind.BatchRequests, HeadlineKind.WaitingTasks, HeadlineKind.PageLifeExpectancy],
                     headlines.Select(h => h.Kind));
        Assert.Equal(("92 %", HealthLevel.Critical, "Warning ≥ 75 % · critical ≥ 90 %"), (headlines[0].ValueText, headlines[0].Level, headlines[0].Note));
        Assert.Equal([40.0, 92.0], headlines[0].Trend);
        Assert.Equal(("1,500", (HealthLevel?)null, "No threshold"), (headlines[1].ValueText, headlines[1].Level, headlines[1].Note));
        Assert.Equal(("7", (HealthLevel?)null), (headlines[2].ValueText, headlines[2].Level));
        Assert.Equal([2.0, 7.0], headlines[2].Trend);
        Assert.Equal(("250 s", HealthLevel.Warning, "Warning < 300 s"), (headlines[3].ValueText, headlines[3].Level, headlines[3].Note));
    }

    [Fact]
    public void Headlines_FollowTheConnectionsOwnThresholds()
    {
        var own = HealthThresholds.Default.With(HealthIndicator.Cpu, new HealthThreshold(30, null));

        var cpu = InCulture("en-GB", () => Headlines.From([ServerTileTests.Sample(cpuPct: 40)], own)[0]);

        Assert.Equal((HealthLevel.Warning, "Warning ≥ 30 %"), (cpu.Level, cpu.Note));
    }

    [Fact]
    public void Headlines_WithoutASample_OrAFigureTheServerDoesntReport()
    {
        var none = Headlines.From([], HealthThresholds.Default);
        Assert.All(none, h => Assert.Equal(("—", (HealthLevel?)null), (h.ValueText, h.Level)));

        var ple = Headlines.From([ServerTileTests.Sample(ple: 0)], HealthThresholds.Default)[3];
        Assert.Equal(("Not reported", (HealthLevel?)null), (ple.ValueText, ple.Level));
        Assert.Empty(ple.Trend);
    }

    // ── Since you last looked ────────────────────────────────────────────────

    [Fact]
    public void Window_RunsFromTheLastLook_OrTheLastDay_AndNoFurtherBackThanAWeek()
    {
        var since = SinceLastLooked.Window(Now.AddHours(-3), Now);
        Assert.Equal((Now.AddHours(-3), Now, false, false), (since.FromUtc, since.ToUtc, since.IsFirstLook, since.Capped));

        var first = SinceLastLooked.Window(null, Now);
        Assert.Equal((Now.AddHours(-24), true), (first.FromUtc, first.IsFirstLook));

        var long_ = SinceLastLooked.Window(Now.AddDays(-30), Now);
        Assert.Equal((Now.AddDays(-7), true, Now.AddDays(-30)), (long_.FromUtc, long_.Capped, long_.LastLookedUtc));

        // The clock went back since: nothing can be after now.
        var ahead = SinceLastLooked.Window(Now.AddHours(1), Now);
        Assert.Equal(Now, ahead.FromUtc);

        Assert.Equal((Now.AddHours(-3).AddDays(-7), Now.AddHours(-3)), SinceLastLooked.Baseline(since));
    }

    private static DeadlockReport Deadlock(DateTime utc) => new(utc, [], [], "<deadlock/>");

    [Fact]
    public void Deadlocks_InTheWindow_NewestFirst()
    {
        var window = SinceLastLooked.Window(Now.AddHours(-2), Now);
        var found  = SinceLastLooked.Deadlocks(
            [Deadlock(Now.AddHours(-3)), Deadlock(Now.AddMinutes(-90)), Deadlock(Now.AddMinutes(-5))], window);

        Assert.Equal([Now.AddMinutes(-5), Now.AddMinutes(-90)], found.Select(d => d.TimestampUtc));
    }

    // A job whose last run started at a time on the server's clock.
    private static AgentJob Job(string name, JobOutcome outcome, DateTime? serverStart, TimeSpan? duration = null) =>
        new(Guid.NewGuid(), name, true, null, null, null, outcome, serverStart, duration ?? TimeSpan.FromMinutes(2), null,
            Runs: 1, FailedRuns: outcome == JobOutcome.Failed ? 1 : 0, SucceededRuns: outcome == JobOutcome.Succeeded ? 1 : 0,
            AverageDuration: null, RunningSince: null, RunningFor: null, NextRun: null, NextRunText: "");

    [Fact]
    public void FailedJobs_WhoseLastRunEndedInTheWindow_OnTheServersClock()
    {
        // The server is two hours ahead of UTC; looked at an hour ago.
        var offset = TimeSpan.FromHours(2);
        var window = SinceLastLooked.Window(Now.AddHours(-1), Now);
        var server = Now + offset;

        var failed = SinceLastLooked.FailedJobs(
        [
            Job("Nightly ETL",  JobOutcome.Failed,    server.AddMinutes(-30)),
            Job("Backup logs",  JobOutcome.Succeeded, server.AddMinutes(-20)),
            Job("Old failure",  JobOutcome.Failed,    server.AddHours(-3)),
            // Started before the last look, failed after it.
            Job("Long rebuild", JobOutcome.Failed,    server.AddMinutes(-70), TimeSpan.FromMinutes(20)),
            Job("Never run",    JobOutcome.Failed,    null),
        ], window, offset);

        Assert.Equal(["Nightly ETL", "Long rebuild"], failed.Select(j => j.Name));
    }

    [Fact]
    public void ServerClock_RoundsTheOffsetToAQuarterHour()
    {
        Assert.Equal(TimeSpan.FromHours(2), ServerClock.Offset(Now.AddHours(2).AddSeconds(3), Now));
        Assert.Equal(TimeSpan.FromHours(5.5), ServerClock.Offset(Now.AddHours(5.5).AddSeconds(-2), Now));
        Assert.Equal(TimeSpan.FromHours(-7), ServerClock.Offset(Now.AddHours(-7).AddMilliseconds(400), Now));
        Assert.Equal(TimeSpan.Zero, ServerClock.Offset(Now.AddSeconds(1), Now));
    }

    // ── Chart scale ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.4, 1)]
    [InlineData(1.3, 2)]
    [InlineData(2.1, 2.5)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(10, 10)]
    [InlineData(1234, 2000)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 10000)]
    public void Scale_TopsOutAtARoundNumber(double highest, double top) =>
        Assert.Equal(top, MetricScale.Top(highest));

    [Fact]
    public void Scale_PlacesValuesFromTheTopDown()
    {
        Assert.Equal(0.0, MetricScale.Y(100, 100));
        Assert.Equal(1.0, MetricScale.Y(0, 100));
        Assert.Equal(0.75, MetricScale.Y(25, 100));
        Assert.Equal(0.0, MetricScale.Y(250, 100));   // clamped
        Assert.Equal(100, MetricScale.Top(42, floor: 100));
    }
}
