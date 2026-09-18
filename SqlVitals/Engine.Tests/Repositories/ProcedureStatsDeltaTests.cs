using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class ProcedureStatsDeltaTests
{
    private static readonly DateTime Cached = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private static ProcStatsSnapshot Row(
        long execCount,
        long elapsedUs        = 0,
        long workerUs         = 0,
        long logicalReads     = 0,
        DateTime? cachedTime  = null,
        string procedureName  = "usp_GetOrders",
        long maxElapsedUs     = 0)
    {
        var key = new ProcStatsKey(7, 1001, cachedTime ?? Cached);
        return new ProcStatsSnapshot(
            key, "SalesDb", "dbo", procedureName,
            execCount, workerUs, elapsedUs, maxElapsedUs,
            logicalReads, 0, 0, null);
    }

    private static Dictionary<ProcStatsKey, ProcStatsSnapshot> Set(params ProcStatsSnapshot[] rows) =>
        rows.ToDictionary(r => r.Key);

    [Fact]
    public void Compute_ReturnsNothingForTheFirstSnapshot()
    {
        // With no baseline, every procedure's whole lifetime history would otherwise be
        // reported as if it had just happened.
        var rows = ProcedureStatsDelta.Compute(Set(), Set(Row(execCount: 500, elapsedUs: 10_000_000)));

        Assert.Empty(rows);
    }

    [Fact]
    public void Compute_ReportsOnlyActivityInTheWindow()
    {
        var previous = Set(Row(execCount: 100, elapsedUs: 5_000_000, workerUs: 2_000_000, logicalReads: 10_000));
        var current  = Set(Row(execCount: 110, elapsedUs: 6_000_000, workerUs: 2_400_000, logicalReads: 12_000));

        var row = Assert.Single(ProcedureStatsDelta.Compute(previous, current));

        Assert.Equal(10, row.ExecCount);
        Assert.Equal(1000, row.TotalDurationMs);      // 1s of elapsed time, in ms
        Assert.Equal(100, row.AvgDurationMs);         // 1000ms over 10 executions
        Assert.Equal(40, row.AvgCpuMs);               // 400ms of CPU over 10 executions
        Assert.Equal(2000, row.TotalLogicalReads);
        Assert.Equal(200, row.AvgLogicalReads);
    }

    [Fact]
    public void Compute_SkipsProceduresThatDidNotRun()
    {
        var unchanged = Row(execCount: 100, elapsedUs: 5_000_000);

        Assert.Empty(ProcedureStatsDelta.Compute(Set(unchanged), Set(unchanged)));
    }

    [Fact]
    public void Compute_TreatsARecompileAsANewRowRatherThanANegativeDelta()
    {
        // A recompile re-caches the plan and resets the counters. CachedTime is part of the
        // key, so the post-recompile row must be reported on its own totals.
        var previous = Set(Row(execCount: 900, elapsedUs: 90_000_000, cachedTime: Cached));
        var current  = Set(Row(execCount: 5,   elapsedUs: 500_000,    cachedTime: Cached.AddMinutes(10)));

        var row = Assert.Single(ProcedureStatsDelta.Compute(previous, current));

        Assert.Equal(5, row.ExecCount);
        Assert.Equal(500, row.TotalDurationMs);
    }

    [Fact]
    public void Compute_ClampsCountersThatMoveBackwards()
    {
        // DBCC FREEPROCCACHE between polls can reset totals without changing CachedTime.
        var previous = Set(Row(execCount: 100, elapsedUs: 5_000_000, logicalReads: 50_000));
        var current  = Set(Row(execCount: 110, elapsedUs: 1_000_000, logicalReads: 10_000));

        var row = Assert.Single(ProcedureStatsDelta.Compute(previous, current));

        Assert.Equal(10, row.ExecCount);
        Assert.Equal(0, row.TotalDurationMs);
        Assert.Equal(0, row.TotalLogicalReads);
    }

    [Fact]
    public void Compute_ReportsMaxElapsedAsTheLifetimeHighWaterMark()
    {
        var previous = Set(Row(execCount: 100, maxElapsedUs: 3_000_000));
        var current  = Set(Row(execCount: 101, maxElapsedUs: 3_000_000));

        var row = Assert.Single(ProcedureStatsDelta.Compute(previous, current));

        Assert.Equal(3000, row.MaxDurationMs);
    }

    [Fact]
    public void Compute_OrdersByTotalDurationDescending()
    {
        var slow = new ProcStatsSnapshot(
            new ProcStatsKey(7, 2002, Cached), "SalesDb", "dbo", "usp_Slow",
            10, 0, 9_000_000, 0, 0, 0, 0, null);
        var fast = new ProcStatsSnapshot(
            new ProcStatsKey(7, 3003, Cached), "SalesDb", "dbo", "usp_Fast",
            10, 0, 1_000_000, 0, 0, 0, 0, null);

        var previousSlow = slow with { ExecutionCount = 0, TotalElapsedTimeUs = 0 };
        var previousFast = fast with { ExecutionCount = 0, TotalElapsedTimeUs = 0 };

        var rows = ProcedureStatsDelta.Compute(
            Set(previousSlow, previousFast),
            Set(slow, fast));

        Assert.Collection(rows,
            r => Assert.Equal("usp_Slow", r.ProcedureName),
            r => Assert.Equal("usp_Fast", r.ProcedureName));
    }

    [Fact]
    public void Compute_TreatsAPlanCachedSinceTheLastPollAsAllNewActivity()
    {
        var existing = Row(execCount: 100, elapsedUs: 5_000_000);
        var freshlyCached = new ProcStatsSnapshot(
            new ProcStatsKey(7, 4004, Cached.AddMinutes(5)), "SalesDb", "dbo", "usp_New",
            3, 0, 300_000, 0, 0, 0, 0, null);

        var rows = ProcedureStatsDelta.Compute(Set(existing), Set(existing, freshlyCached));

        var row = Assert.Single(rows);
        Assert.Equal("usp_New", row.ProcedureName);
        Assert.Equal(3, row.ExecCount);
        Assert.Equal(300, row.TotalDurationMs);
    }

    [Fact]
    public void FullName_QualifiesWithSchemaWhenPresent()
    {
        var previous = Set(Row(execCount: 1));
        var current  = Set(Row(execCount: 2));

        Assert.Equal("dbo.usp_GetOrders", ProcedureStatsDelta.Compute(previous, current)[0].FullName);
    }
}
