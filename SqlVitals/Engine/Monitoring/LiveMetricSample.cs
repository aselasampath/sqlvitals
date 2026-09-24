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
    public static (HealthLevel Level, IReadOnlyList<string> Reasons) Evaluate(LiveMetricSample sample, HealthThresholds thresholds)
    {
        var reasons = new List<string>();
        var level   = HealthLevel.Healthy;

        void Check(HealthIndicator indicator, double value, Func<string> reason)
        {
            var info      = HealthThresholds.Info(indicator);
            var threshold = thresholds.Get(indicator);

            bool Past(double? limit) =>
                limit is { } l && (info.LowerIsWorse ? value < l : value >= l);

            var to = Past(threshold.Critical) ? HealthLevel.Critical
                   : Past(threshold.Warning)  ? HealthLevel.Warning
                   : HealthLevel.Healthy;
            if (to == HealthLevel.Healthy)
                return;

            reasons.Add(reason());
            if (to > level) level = to;
        }

        Check(HealthIndicator.Cpu, sample.SqlCpuPct, () => $"CPU {sample.SqlCpuPct:0}%");

        if (sample.BlockedSessions > 0)
            Check(HealthIndicator.Blocking, sample.LongestBlockSec,
                  () => $"Blocking {FormatSeconds(sample.LongestBlockSec)} ({sample.BlockedSessions:0} blocked)");

        // Zero means the counter wasn't reported (some Azure SQL tiers), not an empty buffer pool.
        if (sample.PageLifeExpectancySec > 0)
            Check(HealthIndicator.PageLifeExpectancy, sample.PageLifeExpectancySec,
                  () => $"PLE {sample.PageLifeExpectancySec:0}s");

        Check(HealthIndicator.MemoryGrantsPending, sample.MemoryGrantsPending,
              () => $"{sample.MemoryGrantsPending:0} memory grant(s) pending");

        Check(HealthIndicator.LogSpace, sample.LogUsedPct,
              () => string.IsNullOrWhiteSpace(sample.LogUsedDatabase)
                  ? $"Log {sample.LogUsedPct:0}% full"
                  : $"Log {sample.LogUsedPct:0}% full ({sample.LogUsedDatabase})");

        // Zero means TempDB's counters weren't reported (Azure SQL Database).
        if (sample.TempDbUsedPct > 0)
            Check(HealthIndicator.TempDbSpace, sample.TempDbUsedPct, () => $"TempDB {sample.TempDbUsedPct:0}% full");

        return (level, reasons);
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
