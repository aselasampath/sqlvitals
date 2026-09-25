using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// One plotted point of the Live Metrics dashboard: cumulative counters from two successive
/// <see cref="LiveMetricSnapshot"/>s turned into per-second rates, plus the instantaneous gauges.
/// </summary>
public sealed record LiveMetricSample(
    DateTime Time,

    // Waits (ms waited per second of wall-clock time)
    double WaitCpuMsPerSec,
    double WaitIoMsPerSec,
    double WaitLockMsPerSec,
    double WaitMemoryMsPerSec,
    double WaitNetworkMsPerSec,
    double WaitOtherMsPerSec,

    // CPU
    double SqlCpuPct,

    // Throughput (per second)
    double BatchRequestsPerSec,
    double CompilationsPerSec,
    double RecompilationsPerSec,
    double TransactionsPerSec,

    // Physical I/O (per second)
    double PhysicalReadsPerSec,
    double PhysicalWritesPerSec,

    // Memory gauges
    double PageLifeExpectancySec,
    double MemoryGrantsPending,
    double BufferCacheHitRatio,

    // Health gauges, not kept in the history
    double  BlockedSessions = 0,
    double  LongestBlockSec = 0,
    double  LogUsedPct      = 0,
    string? LogUsedDatabase = null,
    double  TempDbUsedPct   = 0)
{
    /// <summary>
    /// Builds a sample from the current snapshot and the one before it. Rates are zero for the
    /// first snapshot, and never negative — counters restart from zero when the server does.
    /// </summary>
    public static LiveMetricSample From(LiveMetricSnapshot? previous, LiveMetricSnapshot current)
    {
        var seconds = previous is null ? 1.0 : (current.CaptureTime - previous.CaptureTime).TotalSeconds;
        if (seconds < 0.1) seconds = 1.0;   // avoid div/0 or huge spikes on rapid updates

        double Rate(long now, long before) =>
            previous is null ? 0 : Math.Max(0, now - before) / seconds;

        // Wait rates are whole ms/s, matching what the dashboard has always plotted.
        double WaitRate(long now, long before) => Math.Floor(Rate(now, before));

        return new LiveMetricSample(
            Time:                  current.CaptureTime,
            WaitCpuMsPerSec:       WaitRate(current.CpuWaitMs,     previous?.CpuWaitMs     ?? 0),
            WaitIoMsPerSec:        WaitRate(current.IoWaitMs,      previous?.IoWaitMs      ?? 0),
            WaitLockMsPerSec:      WaitRate(current.LockWaitMs,    previous?.LockWaitMs    ?? 0),
            WaitMemoryMsPerSec:    WaitRate(current.MemoryWaitMs,  previous?.MemoryWaitMs  ?? 0),
            WaitNetworkMsPerSec:   WaitRate(current.NetworkWaitMs, previous?.NetworkWaitMs ?? 0),
            WaitOtherMsPerSec:     WaitRate(current.OtherWaitMs,   previous?.OtherWaitMs   ?? 0),
            SqlCpuPct:             current.SqlCpuUtilizationPct,
            BatchRequestsPerSec:   Rate(current.BatchRequestsPerSec,     previous?.BatchRequestsPerSec     ?? 0),
            CompilationsPerSec:    Rate(current.SQLCompilationsPerSec,   previous?.SQLCompilationsPerSec   ?? 0),
            RecompilationsPerSec:  Rate(current.SQLRecompilationsPerSec, previous?.SQLRecompilationsPerSec ?? 0),
            TransactionsPerSec:    Rate(current.TransactionsPerSec,      previous?.TransactionsPerSec      ?? 0),
            PhysicalReadsPerSec:   Rate(current.PhysicalReadsPerSec,     previous?.PhysicalReadsPerSec     ?? 0),
            PhysicalWritesPerSec:  Rate(current.PhysicalWritesPerSec,    previous?.PhysicalWritesPerSec    ?? 0),
            PageLifeExpectancySec: current.PageLifeExpectancy,
            MemoryGrantsPending:   current.MemoryGrantsPending,
            BufferCacheHitRatio:   current.BufferCacheHitRatio,
            BlockedSessions:       current.BlockedSessions,
            LongestBlockSec:       current.LongestBlockMs / 1000.0,
            LogUsedPct:            current.LogUsedPct,
            LogUsedDatabase:       current.LogUsedDatabase,
            TempDbUsedPct:         current.TempDbUsedPct);
    }
}

public enum HealthLevel
{
    /// <summary>No sample yet.</summary>
    Unknown,
    Healthy,
    Warning,
    Critical,
    /// <summary>The last collection failed (server unreachable, login failed, missing permission…).</summary>
    Unavailable,
}

/// <summary>
/// One indicator of one sample, graded: its level, its figure, the thresholds it was graded on
/// and, when past one, the reason shown in the health dot's tooltip. <paramref name="Measured"/>
/// is false when the sample had no figure for it (nothing blocked, a counter not reported).
/// </summary>
public sealed record HealthReading(HealthIndicator Indicator, HealthLevel Level, double Value, HealthThreshold Limits, string? Reason,
                                   bool Measured = true);

/// <summary>Traffic-light rules applied to each background sample.</summary>
public static class HealthRules
{
    /// <summary>Grades a sample on the default thresholds.</summary>
    public static (HealthLevel Level, IReadOnlyList<string> Reasons) Evaluate(LiveMetricSample sample) =>
        Evaluate(sample, HealthThresholds.Default);

    /// <summary>
    /// Grades a sample: the worst level any indicator reached, and one reason per indicator
    /// past its warning or critical threshold.
    /// </summary>
    public static (HealthLevel Level, IReadOnlyList<string> Reasons) Evaluate(LiveMetricSample sample, HealthThresholds thresholds) =>
        Summarize(Grade(sample, thresholds));

    /// <summary>The worst level among the readings, and the reason of each one past a threshold.</summary>
    public static (HealthLevel Level, IReadOnlyList<string> Reasons) Summarize(IReadOnlyList<HealthReading> readings)
    {
        var past  = readings.Where(r => r.Level > HealthLevel.Healthy).ToList();
        var level = past.Count == 0 ? HealthLevel.Healthy : past.Max(r => r.Level);
        return (level, past.Select(r => r.Reason!).ToList());
    }

    /// <summary>
    /// Grades each indicator of a sample on its thresholds, in <see cref="HealthIndicator"/> order.
    /// An indicator the sample has no figure for (nothing blocked, a counter the server didn't
    /// report) reads as healthy.
    /// </summary>
    public static IReadOnlyList<HealthReading> Grade(LiveMetricSample sample, HealthThresholds thresholds)
    {
        var readings = new List<HealthReading>(6);

        void Check(HealthIndicator indicator, double value, bool measured, Func<string> reason)
        {
            var info      = HealthThresholds.Info(indicator);
            var threshold = thresholds.Get(indicator);

            bool Past(double? limit) =>
                measured && limit is { } l && (info.LowerIsWorse ? value < l : value >= l);

            var level = Past(threshold.Critical) ? HealthLevel.Critical
                      : Past(threshold.Warning)  ? HealthLevel.Warning
                      : HealthLevel.Healthy;

            readings.Add(new HealthReading(indicator, level, value, threshold,
                                           level == HealthLevel.Healthy ? null : reason(), measured));
        }

        Check(HealthIndicator.Cpu, sample.SqlCpuPct, measured: true, () => $"CPU {sample.SqlCpuPct:0}%");

        Check(HealthIndicator.Blocking, sample.LongestBlockSec, measured: sample.BlockedSessions > 0,
              () => $"Blocking {FormatSeconds(sample.LongestBlockSec)} ({sample.BlockedSessions:0} blocked)");

        // Zero means the counter wasn't reported (some Azure SQL tiers), not an empty buffer pool.
        Check(HealthIndicator.PageLifeExpectancy, sample.PageLifeExpectancySec, measured: sample.PageLifeExpectancySec > 0,
              () => $"PLE {sample.PageLifeExpectancySec:0}s");

        Check(HealthIndicator.MemoryGrantsPending, sample.MemoryGrantsPending, measured: true,
              () => $"{sample.MemoryGrantsPending:0} memory grant(s) pending");

        Check(HealthIndicator.LogSpace, sample.LogUsedPct, measured: true,
              () => string.IsNullOrWhiteSpace(sample.LogUsedDatabase)
                  ? $"Log {sample.LogUsedPct:0}% full"
                  : $"Log {sample.LogUsedPct:0}% full ({sample.LogUsedDatabase})");

        // Zero means TempDB's counters weren't reported (Azure SQL Database).
        Check(HealthIndicator.TempDbSpace, sample.TempDbUsedPct, measured: sample.TempDbUsedPct > 0,
              () => $"TempDB {sample.TempDbUsedPct:0}% full");

        return readings;
    }

    private static string FormatSeconds(double seconds) =>
        seconds < 60 ? $"{seconds:0}s" : $"{seconds / 60:0.#} min";

    /// <summary>
    /// Delay before the next collection. Doubles per consecutive failure, capped at five minutes,
    /// so an unreachable server isn't hammered every few seconds.
    /// </summary>
    public static TimeSpan NextDelay(TimeSpan interval, int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return interval;

        var cap   = TimeSpan.FromMinutes(5);
        var delay = interval.TotalSeconds * Math.Pow(2, Math.Min(consecutiveFailures, 10));
        return delay >= cap.TotalSeconds ? (interval > cap ? interval : cap) : TimeSpan.FromSeconds(delay);
    }
}
