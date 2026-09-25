using System.Globalization;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

public class HealthScoreTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 9, 0, 0);

    // A quiet server: nothing near any default threshold.
    private static LiveMetricSample Sample(
        double cpuPct = 10, double ple = 5000, double grants = 0,
        double blocked = 0, double blockSec = 0,
        double logPct = 5, string? logDb = "Sales", double tempDbPct = 10) =>
        new(T0, 0, 0, 0, 0, 0, 0, cpuPct, 0, 0, 0, 0, 0, 0, ple, grants, 99.9,
            blocked, blockSec, logPct, logDb, tempDbPct);

    private static HealthScore Score(LiveMetricSample sample, HealthThresholds? thresholds = null) =>
        HealthScore.From(HealthRules.Grade(sample, thresholds ?? HealthThresholds.Default), T0);

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── Weights ───────────────────────────────────────────────────────────────

    [Fact]
    public void Weights_CoverEveryIndicatorAndAddUpTo100()
    {
        var weights = Enum.GetValues<HealthIndicator>().Select(HealthScore.Weight).ToList();

        Assert.All(weights, w => Assert.True(w > 0));
        Assert.Equal(HealthScore.Max, weights.Sum());
    }

    [Fact]
    public void Weights_AreEvenSoAWarningLosesWholePoints()
    {
        Assert.All(Enum.GetValues<HealthIndicator>(), i => Assert.Equal(0, HealthScore.Weight(i) % 2));
    }

    [Theory]
    [InlineData(HealthLevel.Healthy,  0)]
    [InlineData(HealthLevel.Warning,  10)]
    [InlineData(HealthLevel.Critical, 20)]
    public void PointsLost_HalfForAWarningAllForCritical(HealthLevel level, int expected)
    {
        Assert.Equal(expected, HealthScore.PointsLost(level, 20));
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    [Fact]
    public void QuietServer_Scores100WithNothingLoweringIt()
    {
        var score = Score(Sample());

        Assert.Equal(100, score.Value);
        Assert.Equal(HealthLevel.Healthy, score.Level);
        Assert.Empty(score.Lowering);
        Assert.Equal(Enum.GetValues<HealthIndicator>(), score.Checks.Select(c => c.Indicator));
        Assert.Equal(T0, score.Time);
    }

    [Fact]
    public void AWarningLosesHalfTheChecksWeight()
    {
        var score = Score(Sample(cpuPct: 80));

        Assert.Equal(90, score.Value);
        Assert.Equal(HealthLevel.Warning, score.Level);
        var cpu = Assert.Single(score.Lowering);
        Assert.Equal((HealthIndicator.Cpu, 20, 10), (cpu.Indicator, cpu.Weight, cpu.PointsLost));
    }

    [Fact]
    public void ChecksAddUp_AndTheLevelIsTheWorstCheck()
    {
        // Blocking critical (−20), PLE warning (−8), TempDB warning (−6).
        var score = Score(Sample(blocked: 3, blockSec: 200, ple: 120, tempDbPct: 85));

        Assert.Equal(66, score.Value);
        Assert.Equal(HealthLevel.Critical, score.Level);
    }

    [Fact]
    public void Lowering_CostliestFirstThenInIndicatorOrder()
    {
        // CPU warning −10, log warning −10, blocking critical −20.
        var score = Score(Sample(cpuPct: 80, logPct: 85, blocked: 1, blockSec: 150));

        Assert.Equal(new[] { HealthIndicator.Blocking, HealthIndicator.Cpu, HealthIndicator.LogSpace },
                     score.Lowering.Select(c => c.Indicator));
    }

    [Fact]
    public void EveryCheckCritical_ScoresZero()
    {
        var thresholds = HealthThresholds.Default
            .With(HealthIndicator.PageLifeExpectancy,  new HealthThreshold(600, 300))
            .With(HealthIndicator.MemoryGrantsPending, new HealthThreshold(1, 2));

        var score = Score(Sample(cpuPct: 99, ple: 100, grants: 5, blocked: 2, blockSec: 600, logPct: 99, tempDbPct: 99), thresholds);

        Assert.Equal(0, score.Value);
        Assert.All(score.Checks, c => Assert.Equal(c.Weight, c.PointsLost));
    }

    [Fact]
    public void UsesTheConnectionsThresholds()
    {
        // A reporting server that always runs hot, with its own CPU thresholds.
        var hot = HealthThresholds.Default.With(HealthIndicator.Cpu, new HealthThreshold(95, null));

        Assert.Equal(80,  Score(Sample(cpuPct: 92)).Value);
        Assert.Equal(100, Score(Sample(cpuPct: 92), hot).Value);
    }

    [Fact]
    public void AnIndicatorWithoutAFigure_KeepsItsPointsAndSaysSo()
    {
        // PLE and TempDB not reported (Azure SQL); nothing blocked.
        var score = Score(Sample(ple: 0, tempDbPct: 0, blocked: 0, blockSec: 500));

        Assert.Equal(100, score.Value);
        Assert.Equal("Not reported",    score.Checks.Single(c => c.Indicator == HealthIndicator.PageLifeExpectancy).ValueText);
        Assert.Equal("Not reported",    score.Checks.Single(c => c.Indicator == HealthIndicator.TempDbSpace).ValueText);
        Assert.Equal("Nothing blocked", score.Checks.Single(c => c.Indicator == HealthIndicator.Blocking).ValueText);
    }

    [Fact]
    public void AgreesWithTheHealthDot()
    {
        foreach (var sample in new[] { Sample(), Sample(cpuPct: 80), Sample(logPct: 95), Sample(ple: 100, grants: 2) })
        {
            var (level, _) = HealthRules.Evaluate(sample, HealthThresholds.Default);
            Assert.Equal(level, Score(sample).Level);
        }
    }

    [Fact]
    public void Unavailable_ScoresZeroAndKeepsTheProblem()
    {
        var score = HealthScore.Unavailable("Login failed.", T0);

        Assert.Equal(0, score.Value);
        Assert.Equal(HealthLevel.Unavailable, score.Level);
        Assert.Empty(score.Checks);
        Assert.Equal("Login failed.", score.Problem);
    }

    // ── Text ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_NamesTheChecksThatLoweredIt()
    {
        var summary = InCulture("en-US", () => Score(Sample(cpuPct: 80, blocked: 1, blockSec: 150)).Summary());

        Assert.Equal("Health score 70/100, lowered by Blocking −20 · CPU −10", summary);
    }

    [Fact]
    public void Summary_FullAndUnavailableScores()
    {
        Assert.Equal("Health score 100/100: every check is within its thresholds", Score(Sample()).Summary());
        Assert.Equal("Health score 0/100: the server couldn't be reached or read",
                     HealthScore.Unavailable("x", T0).Summary());
    }

    [Fact]
    public void CheckText_ShowsTheFigureAndThresholds()
    {
        var (value, warning, critical, pleCritical) = InCulture("en-US", () =>
        {
            var score = Score(Sample(logPct: 93.4, ple: 120));
            var log   = score.Checks.Single(c => c.Indicator == HealthIndicator.LogSpace);
            var ple   = score.Checks.Single(c => c.Indicator == HealthIndicator.PageLifeExpectancy);
            return (log.ValueText, log.WarningText, log.CriticalText, ple.CriticalText);
        });

        Assert.Equal(("93 %", "≥ 80 %", "≥ 90 %", "off"), (value, warning, critical, pleCritical));
    }
}
