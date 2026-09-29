using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Tests.QueryStore;

public class QueryStorePeriodAndPlansTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Presets_are_offered_in_order_with_24_hours_the_default()
    {
        Assert.Equal(["Last hour", "Last 24 hours", "Last 7 days", "Last 30 days"], QueryStorePeriod.Presets.Select(p => p.Label));
        Assert.Same(QueryStorePeriod.Last24Hours, QueryStorePeriod.Default);
    }

    [Fact]
    public void A_preset_is_minutes_back_on_the_servers_clock()
    {
        dynamic p = QueryStorePeriod.Last7Days.SqlParameters;

        Assert.Equal(7 * 24 * 60, (int)p.minutes);
        Assert.Null((DateTimeOffset?)p.fromUtc);
        Assert.Null((DateTimeOffset?)p.toUtc);
    }

    [Fact]
    public void A_custom_period_is_fixed()
    {
        var period = QueryStorePeriod.Custom(Now.AddHours(-3), Now.AddHours(-2));
        dynamic p = period.SqlParameters;

        Assert.Equal(TimeSpan.FromHours(1), period.Length);
        Assert.Equal(new DateTimeOffset(Now.AddHours(-3)), (DateTimeOffset?)p.fromUtc);
        Assert.Equal((Now.AddHours(-3), Now.AddHours(-2)), period.Resolve(Now));
    }

    [Fact]
    public void A_custom_period_must_end_after_it_starts_and_start_in_the_past()
    {
        var today = DateTime.Today;

        Assert.False(QueryStorePeriod.TryParseCustom(today, "10:00", today, "09:00", Now, out _, out var error));
        Assert.Equal("The end must be after the start.", error);

        Assert.False(QueryStorePeriod.TryParseCustom(today.AddDays(2), "10:00", today.AddDays(3), "10:00",
                                                     DateTime.UtcNow, out _, out error));
        Assert.Contains("future", error);

        Assert.False(QueryStorePeriod.TryParseCustom(today, "25:00", today, "26:00", Now, out _, out _));
        Assert.True(QueryStorePeriod.TryParseCustom(today.AddDays(-1), "9:30", today.AddDays(-1), "17:05",
                                                    DateTime.UtcNow, out var period, out _));
        Assert.Equal(TimeSpan.FromMinutes(455), period!.Length);
    }

    [Fact]
    public void Saved_chart_metrics_keep_known_ones_and_fall_back_to_the_defaults()
    {
        Assert.Equal([QueryStoreMetric.Rows, QueryStoreMetric.Cpu],
                     QueryStoreMetrics.ParseChartMetrics(["Rows", "nonsense", "cpu", "Rows"]));
        Assert.Equal(QueryStoreMetrics.DefaultChartMetrics, QueryStoreMetrics.ParseChartMetrics(null));
        Assert.Equal(QueryStoreMetrics.DefaultChartMetrics, QueryStoreMetrics.ParseChartMetrics(["x"]));
    }

    [Fact]
    public void Metric_units_are_converted_for_display()
    {
        var totals = new QueryStoreTotals(4, CpuUs: 8000, DurationUs: 2_000_000, 400, 0, 0, MemoryPages: 256, 0, LogBytes: 2 * 1024 * 1024, TempDbPages: 128);

        Assert.Equal(8, totals.Total(QueryStoreMetric.Cpu));             // µs → ms
        Assert.Equal(500, totals.Average(QueryStoreMetric.Duration));    // ms per execution
        Assert.Equal(2, totals.Total(QueryStoreMetric.Memory));          // 8 KB pages → MB
        Assert.Equal(2, totals.Total(QueryStoreMetric.LogUsed));         // bytes → MB
        Assert.Equal(1, totals.Total(QueryStoreMetric.TempDbUsed));
        Assert.Equal(100, totals.Average(QueryStoreMetric.LogicalReads));
        Assert.Equal(4, totals.Total(QueryStoreMetric.Executions));
        Assert.Null(QueryStoreTotals.Zero.Average(QueryStoreMetric.Cpu));
    }

    [Fact]
    public void The_plans_grid_sums_each_plans_intervals_and_keeps_an_unused_forced_plan()
    {
        var t0 = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);
        QueryStorePlanInterval Interval(DateTime start, long plan, long n, double durationUs, long failed = 0) =>
            new(start, plan, failed, QueryStoreTotals.Zero with { Executions = n, DurationUs = durationUs }, start.AddMinutes(5), start.AddMinutes(50));

        var rows = QueryStorePlanSummaries.From(
            [Interval(t0, 1, 10, 10_000), Interval(t0.AddHours(1), 1, 30, 90_000, failed: 2), Interval(t0, 2, 5, 50_000)],
            [new QueryStorePlanState(1, false, 0, null), new QueryStorePlanState(2, false, 0, null), new QueryStorePlanState(3, true, 4, "NO_INDEX")]);

        Assert.Equal([1L, 2L, 3L], rows.Select(r => r.PlanId));
        var first = rows[0];
        Assert.Equal(40, first.Executions);
        Assert.Equal(2.5, first.AvgDurationMs);   // 100,000 µs over 40 executions
        Assert.Equal(2, first.Failed);
        Assert.Equal(t0.AddMinutes(5), first.FirstUsedUtc);
        Assert.Equal(t0.AddHours(1).AddMinutes(50), first.LastUsedUtc);

        var forced = rows[2];
        Assert.True(forced.IsForced);
        Assert.Equal(0, forced.Executions);
        Assert.Equal(4, forced.ForceFailures);
    }
}
