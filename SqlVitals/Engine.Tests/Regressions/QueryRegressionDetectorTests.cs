using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Engine.Tests.Regressions;

public sealed class QueryRegressionDetectorTests
{
    private static readonly RegressionCriteria Fifty = new(50, 5);

    // Times are totals in microseconds; averages come out as totalUs / executions / 1000 ms.
    private static QueryPeriodStats Base(string key, long executions, double durationUs, double cpuUs, long? plan = null) =>
        new(key, false, plan, executions, durationUs, cpuUs);

    private static QueryPeriodStats Recent(string key, long executions, double durationUs, double cpuUs, long? plan = null) =>
        new(key, true, plan, executions, durationUs, cpuUs);

    [Fact]
    public void Detect_FlagsADurationRiseAboveTheThresholdWithBeforeAndAfterFigures()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("1",   100, 1_000_000, 500_000),   // 10 ms, 5 ms CPU
            Recent("1",  10,   300_000,  50_000),   // 30 ms, 5 ms CPU
        ], Fifty));

        Assert.Equal("1", r.QueryKey);
        Assert.Equal(100, r.BaselineExecutions);
        Assert.Equal(10, r.RecentExecutions);
        Assert.Equal(10, r.BaselineAvgDurationMs, 6);
        Assert.Equal(30, r.RecentAvgDurationMs, 6);
        Assert.Equal(200, r.DurationChangePct!.Value, 6);
        Assert.Equal(0, r.CpuChangePct!.Value, 6);
        Assert.True(r.DurationRegressed);
        Assert.False(r.CpuRegressed);
        Assert.Equal("Duration", r.RegressedOn);
        Assert.Equal(200, r.ExtraTimeMs, 6);   // 20 ms more, 10 times
    }

    [Fact]
    public void Detect_FlagsACpuRiseEvenWhenDurationHeldSteady()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("1",   10, 100_000, 20_000),   // 10 ms, 2 ms CPU
            Recent("1", 10, 100_000, 90_000),   // 10 ms, 9 ms CPU
        ], Fifty));

        Assert.False(r.DurationRegressed);
        Assert.True(r.CpuRegressed);
        Assert.Equal("CPU", r.RegressedOn);
        Assert.Equal(350, r.CpuChangePct!.Value, 6);
        Assert.Equal(70, r.ExtraTimeMs, 6);
    }

    [Fact]
    public void Detect_BothMetricsRegressedCountsTheLargerExtraTime()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("1",   10, 100_000, 50_000),    // 10 ms, 5 ms CPU
            Recent("1", 10, 400_000, 150_000),   // 40 ms, 15 ms CPU
        ], Fifty));

        Assert.Equal("Duration, CPU", r.RegressedOn);
        Assert.Equal(300, r.ExtraTimeMs, 6);   // duration: 30 ms × 10; CPU would be 100
    }

    [Theory]
    [InlineData(150_000, false)]   // +50 %: not more than the threshold
    [InlineData(151_000, true)]    // +51 %
    public void Detect_NeedsMoreThanTheThreshold(double recentDurationUs, bool flagged)
    {
        var found = QueryRegressionDetector.Detect(
        [
            Base("1",   10, 100_000, 0),
            Recent("1", 10, recentDurationUs, 0),
        ], Fifty);

        Assert.Equal(flagged, found.Count == 1);
    }

    [Fact]
    public void Detect_LeavesOutAQueryWithTooFewExecutionsInEitherPeriod()
    {
        var found = QueryRegressionDetector.Detect(
        [
            Base("few-before", 4, 40_000, 0),  Recent("few-before", 10, 1_000_000, 0),
            Base("few-after", 10, 100_000, 0), Recent("few-after",   4,   400_000, 0),
            Recent("only-recent", 50, 5_000_000, 0),
        ], Fifty);

        Assert.Empty(found);
    }

    [Fact]
    public void Detect_IgnoresAMetricWhoseRecentAverageIsUnderAMillisecond()
    {
        // 0.1 ms → 0.5 ms is +400 %, but too small to matter.
        var found = QueryRegressionDetector.Detect(
        [
            Base("1",   100, 10_000, 10_000),
            Recent("1", 100, 50_000, 50_000),
        ], Fifty);

        Assert.Empty(found);
    }

    [Fact]
    public void Detect_GivesNoPercentageWhenTheBaselineWasZero()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("1",   10, 100_000, 0),
            Recent("1", 10, 300_000, 50_000),
        ], Fifty));

        Assert.Null(r.CpuChangePct);
        Assert.False(r.CpuRegressed);
        Assert.True(r.DurationRegressed);
    }

    [Fact]
    public void Detect_CombinesEveryPlanOfAQueryAndSaysWhenTheRecentPlanIsNew()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("7",   90, 900_000, 0, plan: 70),
            Base("7",   10, 100_000, 0, plan: 71),
            Recent("7",  2,  20_000, 0, plan: 70),
            Recent("7",  8, 800_000, 0, plan: 72),   // the new plan does most of the work
        ], Fifty));

        Assert.Equal(100, r.BaselineExecutions);
        Assert.Equal(10, r.BaselineAvgDurationMs, 6);
        Assert.Equal(82, r.RecentAvgDurationMs, 6);   // (20 + 800) ms over 10
        Assert.Equal(70, r.BaselinePlanId);
        Assert.Equal(72, r.RecentPlanId);
        Assert.True(r.NewPlan);
        Assert.Equal("New plan", r.PlanChange);
    }

    [Fact]
    public void Detect_APlanThatAlsoRanInTheBaselineIsNotNew()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("7",   90, 900_000, 0, plan: 70),
            Base("7",   10, 100_000, 0, plan: 71),
            Recent("7", 10, 900_000, 0, plan: 71),
        ], Fifty));

        Assert.Equal(70, r.BaselinePlanId);
        Assert.Equal(71, r.RecentPlanId);
        Assert.False(r.NewPlan);
        Assert.True(r.PlanDiffers);
        Assert.Equal("Different plan", r.PlanChange);
    }

    [Fact]
    public void Detect_TheSamePlanInBothPeriodsIsNoPlanChange()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
        [
            Base("7",   10, 100_000, 0, plan: 70),
            Recent("7", 10, 900_000, 0, plan: 70),
        ], Fifty));

        Assert.False(r.NewPlan);
        Assert.False(r.PlanDiffers);
        Assert.Equal(string.Empty, r.PlanChange);
    }

    [Fact]
    public void Detect_WithoutPlansHasNoPlanIds()
    {
        var r = Assert.Single(QueryRegressionDetector.Detect(
            QueryRegressionDetector.FromHistory(
                [new HistoryQueryTotals("0x00000000000000AA", 10, 20_000, 100_000)],
                [new HistoryQueryTotals("0x00000000000000AA", 10, 20_000, 500_000)]),
            Fifty));

        Assert.Equal("0x00000000000000AA", r.QueryKey);
        Assert.Null(r.BaselinePlanId);
        Assert.Null(r.RecentPlanId);
        Assert.False(r.NewPlan);
        Assert.False(r.PlanDiffers);
        Assert.Equal(10, r.BaselineAvgDurationMs, 6);
        Assert.Equal(50, r.RecentAvgDurationMs, 6);
        Assert.Equal(2, r.RecentAvgCpuMs, 6);
    }

    [Fact]
    public void Detect_PutsTheCostliestRegressionFirst()
    {
        var found = QueryRegressionDetector.Detect(
        [
            // +900 % on 5 runs: 45 ms extra each, 225 ms in all.
            Base("rare", 5, 25_000, 0),         Recent("rare", 5, 250_000, 0),
            // +60 % on 1,000 runs: 3 ms extra each, 3 s in all.
            Base("busy", 1_000, 5_000_000, 0),  Recent("busy", 1_000, 8_000_000, 0),
        ], Fifty);

        Assert.Equal(new[] { "busy", "rare" }, found.Select(r => r.QueryKey));
    }

    [Fact]
    public void Detect_ReturnsAtMostTheMaximum()
    {
        var stats = Enumerable.Range(0, QueryRegressionDetector.MaxResults + 10).SelectMany(i => new[]
        {
            Base($"q{i}",   10, 100_000, 0),
            Recent($"q{i}", 10, 1_000_000 + i * 1_000, 0),
        });

        var found = QueryRegressionDetector.Detect(stats, Fifty);

        Assert.Equal(QueryRegressionDetector.MaxResults, found.Count);
        Assert.Equal($"q{QueryRegressionDetector.MaxResults + 9}", found[0].QueryKey);
    }
}
