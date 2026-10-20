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

    // Graded as a collector does: the sample's six checks and the slow three (#174), healthy.
    private static HealthScore Score(LiveMetricSample sample, HealthThresholds? thresholds = null) =>
        HealthScore.From(TestChecks.All(sample, thresholds), T0);

    private static T InCulture<T>(string name, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── Weights ───────────────────────────────────────────────────────────────

    [Fact]
    public void Weights_CoverEveryIndicator_AndTheChecksEveryServerHasAddUpTo100()
    {
        var weights = Enum.GetValues<HealthIndicator>().Select(HealthScore.Weight).ToList();

        Assert.All(weights, w => Assert.True(w > 0));
        // Azure IO (#234) is only graded on Azure SQL Database, so a SQL Server's points are its weights.
        Assert.Equal(HealthScore.Max, Enum.GetValues<HealthIndicator>().Where(i => !HealthRules.IsAzureOnly(i)).Sum(HealthScore.Weight));
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
        Assert.Equal(Enum.GetValues<HealthIndicator>().Where(i => !HealthRules.IsAzureOnly(i)), score.Checks.Select(c => c.Indicator));
        Assert.Equal(T0, score.Time);
    }

    [Fact]
    public void AWarningLosesHalfTheChecksWeight()
    {
        var score = Score(Sample(cpuPct: 80));

        Assert.Equal(92, score.Value);
        Assert.Equal(HealthLevel.Warning, score.Level);
        var cpu = Assert.Single(score.Lowering);
        Assert.Equal((HealthIndicator.Cpu, 16, 8), (cpu.Indicator, cpu.Weight, cpu.PointsLost));
    }

    [Fact]
    public void ChecksAddUp_AndTheLevelIsTheWorstCheck()
    {
        // Blocking critical (−16), PLE warning (−5), TempDB warning (−4).
        var score = Score(Sample(blocked: 3, blockSec: 200, ple: 120, tempDbPct: 85));

        Assert.Equal(75, score.Value);
        Assert.Equal(HealthLevel.Critical, score.Level);
    }

    [Fact]
    public void Lowering_CostliestFirstThenInIndicatorOrder()
    {
        // CPU warning −8, log warning −7, blocking critical −16.
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

        var slow = TestChecks.Readings(new ServerCheckFigures([new VolumeSpace(@"D:\", 100, 1)], null, ["Sales"], null, ["Nightly"], null),
                                       thresholds with { FailedJobs = new HealthThreshold(null, 1) });

        var score = HealthScore.From(TestChecks.All(Sample(cpuPct: 99, ple: 100, grants: 5, blocked: 2, blockSec: 600, logPct: 99, tempDbPct: 99),
                                                    thresholds, slow), T0);

        Assert.Equal(0, score.Value);
        Assert.Equal(9, score.Checks.Count);
        Assert.All(score.Checks, c => Assert.Equal(c.Weight, c.PointsLost));
    }

    [Fact]
    public void UsesTheConnectionsThresholds()
    {
        // A reporting server that always runs hot, with its own CPU thresholds.
        var hot = HealthThresholds.Default.With(HealthIndicator.Cpu, new HealthThreshold(95, null));

        Assert.Equal(84,  Score(Sample(cpuPct: 92)).Value);
        Assert.Equal(100, Score(Sample(cpuPct: 92), hot).Value);
    }

    [Fact]
    public void AnIndicatorWithoutAFigure_IsLeftOut_AndSaysSo()
    {
        // PLE and TempDB not reported (Azure SQL); nothing blocked (#173).
        var score = InCulture("en-GB", () => Score(Sample(ple: 0, tempDbPct: 0, blocked: 0, blockSec: 500)));

        Assert.Equal(100, score.Value);
        var ple = score.Checks.Single(c => c.Indicator == HealthIndicator.PageLifeExpectancy);
        Assert.Equal((HealthLevel.Unknown, "No data", 0, 0), (ple.Level, ple.ValueText, ple.Worth, ple.PointsLost));
        Assert.Equal("No data",         score.Checks.Single(c => c.Indicator == HealthIndicator.TempDbSpace).ValueText);
        Assert.Equal("Nothing blocked", score.Checks.Single(c => c.Indicator == HealthIndicator.Blocking).ValueText);
        Assert.Equal([HealthIndicator.PageLifeExpectancy, HealthIndicator.TempDbSpace], score.LeftOut.Select(c => c.Indicator));
        Assert.Equal("2 checks left out of the score, no data: Page life expectancy, TempDB space used", score.LeftOutText);
        Assert.Equal("Healthy: 7 of 9 checks pass, 2 have no data", score.Verdict);
    }

    [Fact]
    public void TheScore_IsScaledOverTheChecksThatReported()
    {
        // TempDB not reported: the other eight share 100 points among their 92 of weight, so CPU
        // past warning loses half of its 18 (16 × 100 ÷ 92, rounded).
        var score = Score(Sample(cpuPct: 80, tempDbPct: 0));

        var cpu = score.Checks.Single(c => c.Indicator == HealthIndicator.Cpu);
        Assert.Equal((16, 18, 9), (cpu.Weight, cpu.Worth, cpu.PointsLost));
        Assert.Equal(91, score.Value);
        Assert.Equal(HealthScore.Max - score.Checks.Sum(c => c.PointsLost), score.Value);
    }

    [Theory]
    [InlineData(10, 5000)]   // every check reported: the default weights, as they are
    [InlineData(0,  5000)]   // TempDB not reported
    [InlineData(10, 0)]      // PLE not reported
    [InlineData(0,  0)]      // neither
    public void TheWorths_AlwaysAddUpTo100(double tempDbPct, double ple)
    {
        var score = Score(Sample(tempDbPct: tempDbPct, ple: ple));

        Assert.Equal(HealthScore.Max, score.Checks.Sum(c => c.Worth));
    }

    [Fact]
    public void TheWorths_WithTempDbMissing_AreTheRoundedShares()
    {
        var score = Score(Sample(tempDbPct: 0));

        Assert.Equal([18, 17, 11, 9, 15, 0, 15, 11, 4], score.Checks.Select(c => c.Worth));
    }

    [Fact]
    public void MissingData_NeverRaisesTheScore_AboveWhatTheChecksThatReportedEarn()
    {
        // Blocking critical, PLE and TempDB unreported: 0 of blocking's share.
        var score = Score(Sample(blocked: 2, blockSec: 600, ple: 0, tempDbPct: 0));

        // 16 of 82 reported weight is 19 points.
        Assert.Equal(81, score.Value);
    }

    // ── Custom weights (#173) ─────────────────────────────────────────────────

    [Fact]
    public void AWeightOf0_LeavesTheCheckOutOfTheScore_ButNotTheDot()
    {
        var weights = HealthWeights.Default.With(HealthIndicator.Cpu, 0);

        var score = HealthScore.From(HealthRules.Grade(Sample(cpuPct: 99), HealthThresholds.Default), T0, weights);

        Assert.Equal(100, score.Value);
        Assert.Equal(HealthLevel.Critical, score.Level);
        var cpu = score.Checks.Single(c => c.Indicator == HealthIndicator.Cpu);
        Assert.Equal((0, 0, 0, false), (cpu.Weight, cpu.Worth, cpu.PointsLost, cpu.IsScored));
        Assert.Equal("Critical: CPU at 99 %", InCulture("en-GB", () => score.Verdict));
    }

    [Fact]
    public void CustomWeights_AreRelative_AndTheScoreStaysOutOf100()
    {
        // Every check weighs 10: each is worth 11 of the 100 (10 × 100 ÷ 90), and the point left
        // over goes to the first, so CPU critical loses 12.
        var weights = new HealthWeights(10, 10, 10, 10, 10, 10, 10, 10, 10);

        var score = HealthScore.From(TestChecks.All(Sample(cpuPct: 95)), T0, weights);

        Assert.Equal(88, score.Value);
        Assert.Equal(12, score.Checks.Single(c => c.Indicator == HealthIndicator.Cpu).Worth);
    }

    [Fact]
    public void TheDefaultWeights_GiveTheSameScoresAsBefore()
    {
        var sample = Sample(cpuPct: 80, logPct: 85, blocked: 1, blockSec: 150);

        Assert.Equal(Score(sample).Value,
                     HealthScore.From(TestChecks.All(sample), T0, HealthThresholds.Default.Weights).Value);
        Assert.Equal(69, Score(sample).Value);
    }

    // ── Verdict: the worst state first (#173) ─────────────────────────────────

    [Fact]
    public void Verdict_LeadsWithTheWorstStateAndItsReason()
    {
        var (blocking, several, healthy) = InCulture("en-GB", () => (
            Score(Sample(blocked: 3, blockSec: 240)).Verdict,
            Score(Sample(cpuPct: 80, blocked: 1, blockSec: 150, logPct: 85)).Verdict,
            Score(Sample()).Verdict));

        Assert.Equal("Critical: blocking for 4 min", blocking);
        Assert.Equal("Critical: blocking for 2.5 min (+2 more)", several);
        Assert.Equal("Healthy: every check passes", healthy);
        Assert.Equal("Unreachable: the server can't be reached or read", HealthScore.Unavailable("x", T0).Verdict);
    }

    [Fact]
    public void Verdict_OfALevelWithNoCheckToExplainIt_IsTheLevel_NeverHealthy()
    {
        Assert.Equal("Warning", new HealthScore(84, HealthLevel.Warning, [], T0).Verdict);
        Assert.Equal("Critical", new HealthScore(60, HealthLevel.Critical, [], T0).Verdict);
    }

    [Fact]
    public void Verdict_AmongWarnings_NamesTheCostliest()
    {
        // PLE (10) and TempDB (8) past warning: PLE costs more.
        var verdict = InCulture("en-GB", () => Score(Sample(ple: 120, tempDbPct: 85)).Verdict);

        Assert.Equal("Warning: page life expectancy 120 s (+1 more)", verdict);
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

        Assert.Equal("Critical: blocking for 2.5 min (+1 more) · health score 76/100, lowered by Blocking −16 · CPU −8", summary);
    }

    [Fact]
    public void Summary_FullAndUnavailableScores()
    {
        Assert.Equal("Healthy: every check passes · health score 100/100", Score(Sample()).Summary());
        Assert.Equal("Unreachable · health score 0/100: the server couldn't be reached or read",
                     HealthScore.Unavailable("x", T0).Summary());
        Assert.Equal("Healthy: 8 of 9 checks pass, 1 has no data · health score 100/100 · 1 check left out of the score, no data: TempDB space used",
                     Score(Sample(tempDbPct: 0)).Summary());
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
