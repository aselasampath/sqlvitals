using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

public class LiveMetricSampleTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private static LiveMetricSnapshot Snap(
        DateTime time,
        long batches   = 0,
        long cpuWaitMs = 0,
        double cpuPct  = 10,
        long ple       = 5000,
        long mgPending = 0) =>
        new(time,
            BatchRequestsPerSec: batches, SQLCompilationsPerSec: 0, SQLRecompilationsPerSec: 0, TransactionsPerSec: 0,
            PhysicalReadsPerSec: 0, PhysicalWritesPerSec: 0,
            SqlCpuUtilizationPct: cpuPct,
            PageLifeExpectancy: ple, MemoryGrantsPending: mgPending, BufferCacheHitRatio: 99.9,
            TotalWaitMs: cpuWaitMs, CpuWaitMs: cpuWaitMs, IoWaitMs: 0, LockWaitMs: 0,
            MemoryWaitMs: 0, NetworkWaitMs: 0, OtherWaitMs: 0);

    [Fact]
    public void From_FirstSnapshotHasZeroRatesButRealGauges()
    {
        // Cumulative counters are meaningless without a baseline; gauges are valid immediately.
        var sample = LiveMetricSample.From(null, Snap(T0, batches: 1_000_000, cpuWaitMs: 50_000, cpuPct: 42));

        Assert.Equal(0, sample.BatchRequestsPerSec);
        Assert.Equal(0, sample.WaitCpuMsPerSec);
        Assert.Equal(42, sample.SqlCpuPct);
        Assert.Equal(5000, sample.PageLifeExpectancySec);
    }

    [Fact]
    public void From_DividesCounterDeltasByElapsedSeconds()
    {
        var sample = LiveMetricSample.From(
            Snap(T0,                 batches: 1000, cpuWaitMs: 100),
            Snap(T0.AddSeconds(10),  batches: 1500, cpuWaitMs: 125));

        Assert.Equal(50, sample.BatchRequestsPerSec);
        Assert.Equal(2, sample.WaitCpuMsPerSec);   // 2.5 ms/s truncated, as the dashboard always has
    }

    [Fact]
    public void From_ClampsNegativeDeltasAfterAServerRestart()
    {
        var sample = LiveMetricSample.From(
            Snap(T0,                batches: 9_000_000, cpuWaitMs: 9_000_000),
            Snap(T0.AddSeconds(10), batches: 10,        cpuWaitMs: 5));

        Assert.Equal(0, sample.BatchRequestsPerSec);
        Assert.Equal(0, sample.WaitCpuMsPerSec);
    }

    [Fact]
    public void Evaluate_HealthyWhenNothingIsAboveThreshold()
    {
        var (level, reasons) = HealthRules.Evaluate(LiveMetricSample.From(null, Snap(T0, cpuPct: 20)));

        Assert.Equal(HealthLevel.Healthy, level);
        Assert.Empty(reasons);
    }

    [Theory]
    [InlineData(74.9, HealthLevel.Healthy)]
    [InlineData(75,   HealthLevel.Warning)]
    [InlineData(89.9, HealthLevel.Warning)]
    [InlineData(90,   HealthLevel.Critical)]
    public void Evaluate_GradesCpu(double cpuPct, HealthLevel expected)
    {
        var (level, _) = HealthRules.Evaluate(LiveMetricSample.From(null, Snap(T0, cpuPct: cpuPct)));

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Evaluate_KeepsTheWorstLevelAndEveryReason()
    {
        var (level, reasons) = HealthRules.Evaluate(
            LiveMetricSample.From(null, Snap(T0, cpuPct: 95, ple: 120, mgPending: 3)));

        Assert.Equal(HealthLevel.Critical, level);
        Assert.Equal(3, reasons.Count);
    }

    [Fact]
    public void Evaluate_IgnoresAMissingPageLifeExpectancyCounter()
    {
        var (level, _) = HealthRules.Evaluate(LiveMetricSample.From(null, Snap(T0, ple: 0)));

        Assert.Equal(HealthLevel.Healthy, level);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 20)]
    [InlineData(3, 80)]
    [InlineData(6, 300)]    // 640 s capped at five minutes
    [InlineData(50, 300)]   // no overflow on long outages
    public void NextDelay_BacksOffExponentiallyUpToFiveMinutes(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HealthRules.NextDelay(TimeSpan.FromSeconds(10), failures));
    }
}
