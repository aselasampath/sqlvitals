using System.Globalization;
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
    double  TempDbUsedPct   = 0,

    // User tasks waiting on anything now (#143): the Overview's headline, not kept in the history
    double  WaitingTasks    = 0,

    // What the health score judges page life expectancy and log space by (#173), not kept in the history
    double  BufferPoolMb    = 0,
    double  LogSizeMb       = 0,
    string? LogReuseWait    = null)
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
            TempDbUsedPct:         current.TempDbUsedPct,
            WaitingTasks:          current.WaitingTasks,
            BufferPoolMb:          current.BufferPoolPages * 8 / 1024.0,
            LogSizeMb:             current.LogSizeMb,
            LogReuseWait:          current.LogReuseWait);
    }
}

public enum HealthLevel
{
    /// <summary>No sample yet; for one check, no figure to grade (#173): grey, and left out of the score.</summary>
    Unknown,
    Healthy,
    Warning,
    Critical,
    /// <summary>The last collection failed (server unreachable, login failed, missing permission…).</summary>
    Unavailable,
}

/// <summary>
/// What a sample says besides an indicator's own figure, that its grading depends on (#173): the
/// buffer pool's size for page life expectancy, and the fullest log's size and reuse wait.
/// </summary>
/// <param name="BufferPoolGb">The buffer pool's data pages, in GB; 0 when not reported.</param>
/// <param name="LogSizeMb">The fullest log's size; 0 when not reported.</param>
/// <param name="LogReuseWait">Its log_reuse_wait_desc, e.g. "LOG_BACKUP"; null when not reported.</param>
/// <param name="LogDatabase">The database whose log it is.</param>
/// <param name="BlockedSessions">Requests blocked now.</param>
public sealed record HealthFacts(
    double  BufferPoolGb    = 0,
    double  LogSizeMb       = 0,
    string? LogReuseWait    = null,
    string? LogDatabase     = null,
    double  BlockedSessions = 0)
{
    public static HealthFacts None { get; } = new();

    public static HealthFacts Of(LiveMetricSample sample) => new(
        sample.BufferPoolMb / 1024, sample.LogSizeMb, sample.LogReuseWait, sample.LogUsedDatabase, sample.BlockedSessions);
}

/// <summary>
/// One indicator of one sample, graded: its level, its figure, the thresholds it was graded on
/// and, when past one, the reason shown in the health dot's tooltip. <paramref name="Measured"/>
/// is false when the sample had no figure for it (a counter not reported): its level is then
/// <see cref="HealthLevel.Unknown"/>.
/// </summary>
/// <param name="Limits">The thresholds it was graded on: for a scaled page life expectancy, scaled to the buffer pool.</param>
/// <param name="Facts">What else the grading looked at, so Settings can grade the figure again (#173).</param>
/// <param name="Note">How the grading was adjusted, e.g. the log that is full but reusable; null when it wasn't.</param>
public sealed record HealthReading(HealthIndicator Indicator, HealthLevel Level, double Value, HealthThreshold Limits, string? Reason,
                                   bool Measured = true, HealthFacts? Facts = null, string? Note = null);

/// <summary>Traffic-light rules applied to each background sample.</summary>
public static class HealthRules
{
    /// <summary>
    /// A log smaller than this that SQL Server can reuse isn't a warning however full it is (#173):
    /// it is sized tight, and will wrap around rather than grow.
    /// </summary>
    public const double SmallLogMb = 1024;

    /// <summary>
    /// The log_reuse_wait_desc values that mean the log can be reused: nothing to wait for, or only
    /// the next checkpoint.
    /// </summary>
    public static IReadOnlyList<string> ReusableLogWaits { get; } = ["NOTHING", "CHECKPOINT"];

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
    /// An indicator the sample has no figure for (a counter the server didn't report) is
    /// <see cref="HealthLevel.Unknown"/>; nothing blocked is a figure, 0 s.
    /// </summary>
    public static IReadOnlyList<HealthReading> Grade(LiveMetricSample sample, HealthThresholds thresholds)
    {
        var facts = HealthFacts.Of(sample);
        return
        [
            Read(HealthIndicator.Cpu, sample.SqlCpuPct, measured: true, facts, thresholds),
            // Nothing blocked is a figure, 0 s: a wait time without a blocked request is ignored.
            Read(HealthIndicator.Blocking, sample.BlockedSessions > 0 ? sample.LongestBlockSec : 0, measured: true, facts, thresholds),
            // Zero means the counter wasn't reported (some Azure SQL tiers), not an empty buffer pool.
            Read(HealthIndicator.PageLifeExpectancy, sample.PageLifeExpectancySec, measured: sample.PageLifeExpectancySec > 0, facts, thresholds),
            Read(HealthIndicator.MemoryGrantsPending, sample.MemoryGrantsPending, measured: true, facts, thresholds),
            // No database means no log's counters were reported.
            Read(HealthIndicator.LogSpace, sample.LogUsedPct, measured: sample.LogUsedDatabase is not null || sample.LogUsedPct > 0, facts, thresholds),
            // Zero means TempDB's counters weren't reported (Azure SQL Database).
            Read(HealthIndicator.TempDbSpace, sample.TempDbUsedPct, measured: sample.TempDbUsedPct > 0, facts, thresholds),
        ];
    }

    /// <summary>
    /// Grades one figure. The sample's grading and Settings' preview (#131) both come here, so the
    /// preview never disagrees with the dot. Page life expectancy is graded on thresholds scaled
    /// to the buffer pool (<see cref="HealthThresholds.For"/>), and a small log SQL Server can
    /// reuse is healthy however full it is (#173).
    /// </summary>
    public static HealthReading Read(HealthIndicator indicator, double value, bool measured, HealthFacts facts, HealthThresholds thresholds)
    {
        var limits = thresholds.For(indicator, facts.BufferPoolGb);
        var level  = LevelOf(indicator, value, measured, limits);
        string? note = null;

        if (indicator == HealthIndicator.PageLifeExpectancy && thresholds.ScalesPageLifeExpectancy && facts.BufferPoolGb > 0)
            note = string.Format(CultureInfo.CurrentCulture, "Thresholds scaled to its {0} buffer pool: {1} s per {2:0} GB.",
                                 Gigabytes(facts.BufferPoolGb), PerFourGb(thresholds), HealthThresholds.PageLifeExpectancyPerGb);

        if (indicator == HealthIndicator.LogSpace && level > HealthLevel.Healthy && IsSmallReusableLog(facts))
        {
            level = HealthLevel.Healthy;
            note  = string.Format(CultureInfo.CurrentCulture,
                                  "Not a warning: the log{0} is only {1:N0} MB, and SQL Server can reuse it ({2}).",
                                  string.IsNullOrWhiteSpace(facts.LogDatabase) ? string.Empty : $" of {facts.LogDatabase}",
                                  facts.LogSizeMb, facts.LogReuseWait);
        }

        var reason = level is HealthLevel.Warning or HealthLevel.Critical ? ReasonFor(indicator, value, facts) : null;
        return new HealthReading(indicator, level, value, limits, reason, measured, facts, note);
    }

    /// <summary>A reading's figure graded again on other thresholds, as Settings' preview does (#131).</summary>
    public static HealthReading Regrade(HealthReading reading, HealthThresholds thresholds) =>
        Read(reading.Indicator, reading.Value, reading.Measured, reading.Facts ?? HealthFacts.None, thresholds);

    /// <summary>
    /// One figure's level on a threshold: critical, warning or healthy. A figure the sample didn't
    /// have (<paramref name="measured"/> false) is <see cref="HealthLevel.Unknown"/>.
    /// </summary>
    public static HealthLevel LevelOf(HealthIndicator indicator, double value, bool measured, HealthThreshold threshold)
    {
        if (!measured)
            return HealthLevel.Unknown;

        var info = HealthThresholds.Info(indicator);

        bool Past(double? limit) =>
            limit is { } l && (info.LowerIsWorse ? value < l : value >= l);

        return Past(threshold.Critical) ? HealthLevel.Critical
             : Past(threshold.Warning)  ? HealthLevel.Warning
             : HealthLevel.Healthy;
    }

    /// <summary>A log below <see cref="SmallLogMb"/> whose reuse waits on nothing or a checkpoint.</summary>
    public static bool IsSmallReusableLog(HealthFacts facts) =>
        facts.LogSizeMb > 0 && facts.LogSizeMb < SmallLogMb &&
        facts.LogReuseWait is { } wait && ReusableLogWaits.Contains(wait, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What is wrong, in a few words for "Critical: blocking for 4 min" (#173): "CPU at 92 %",
    /// "log 91 % full in Sales".
    /// </summary>
    public static string Phrase(HealthReading reading)
    {
        var culture = CultureInfo.CurrentCulture;
        var v       = reading.Value;
        var facts   = reading.Facts ?? HealthFacts.None;
        return reading.Indicator switch
        {
            HealthIndicator.Cpu                 => string.Format(culture, "CPU at {0:0} %", v),
            HealthIndicator.Blocking            => "blocking for " + FormatSeconds(v),
            HealthIndicator.PageLifeExpectancy  => string.Format(culture, "page life expectancy {0:N0} s", v),
            HealthIndicator.MemoryGrantsPending => v == 1 ? "1 memory grant pending" : string.Format(culture, "{0:N0} memory grants pending", v),
            HealthIndicator.LogSpace            => string.Format(culture, "log {0:0} % full", v) +
                                                   (string.IsNullOrWhiteSpace(facts.LogDatabase) ? string.Empty : $" in {facts.LogDatabase}"),
            HealthIndicator.TempDbSpace         => string.Format(culture, "TempDB {0:0} % full", v),
            _                                   => reading.Indicator.ToString(),
        };
    }

    // The health dot's tooltip line for an indicator past a threshold.
    private static string ReasonFor(HealthIndicator indicator, double value, HealthFacts facts) => indicator switch
    {
        HealthIndicator.Cpu                 => $"CPU {value:0}%",
        HealthIndicator.Blocking            => $"Blocking {FormatSeconds(value)} ({facts.BlockedSessions:0} blocked)",
        HealthIndicator.PageLifeExpectancy  => $"PLE {value:0}s",
        HealthIndicator.MemoryGrantsPending => $"{value:0} memory grant(s) pending",
        HealthIndicator.LogSpace            => $"Log {value:0}% full" + LogDetail(facts),
        HealthIndicator.TempDbSpace         => $"TempDB {value:0}% full",
        _                                   => indicator.ToString(),
    };

    // " (Sales, waiting on LOG_BACKUP)", " (Sales)", or nothing.
    private static string LogDetail(HealthFacts facts)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(facts.LogDatabase))
            parts.Add(facts.LogDatabase);
        if (!string.IsNullOrWhiteSpace(facts.LogReuseWait))
            parts.Add($"waiting on {facts.LogReuseWait}");
        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    // "300", or "300 / 150" with a critical threshold too: the thresholds as typed, per 4 GB.
    private static string PerFourGb(HealthThresholds thresholds)
    {
        var typed = thresholds.Get(HealthIndicator.PageLifeExpectancy);
        return typed.Critical is { } critical && typed.Warning is { } warning
            ? $"{HealthThresholds.Format(warning)} / {HealthThresholds.Format(critical)}"
            : HealthThresholds.Format(typed.Warning ?? typed.Critical);
    }

    // "64 GB", "1.5 GB", "512 MB".
    internal static string Gigabytes(double gb) =>
        gb >= 1
            ? string.Format(CultureInfo.CurrentCulture, gb >= 10 ? "{0:N0} GB" : "{0:0.#} GB", gb)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} MB", gb * 1024);

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
