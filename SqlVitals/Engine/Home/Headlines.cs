using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Home;

/// <summary>The Overview's headline numbers (#143), in the order shown.</summary>
public enum HeadlineKind
{
    Cpu,
    BatchRequests,
    WaitingTasks,
    PageLifeExpectancy,
}

/// <summary>
/// One headline number: the latest sample's figure, its level on the connection's thresholds,
/// and the samples' figures for its sparkline.
/// </summary>
/// <param name="ValueText">"12 %"; "—" with no sample, "Not reported" when the server doesn't report it.</param>
/// <param name="Level">Its level on the thresholds; null for a figure that has none (throughput, waiting tasks) or no figure.</param>
/// <param name="Note">The thresholds it is coloured by, or that it has none.</param>
/// <param name="Trend">The samples' figures, oldest first.</param>
public sealed record Headline(HeadlineKind Kind, string Label, string ValueText, HealthLevel? Level, string Note, IReadOnlyList<double> Trend);

/// <summary>
/// CPU, batch requests/s, waiting tasks and page life expectancy from a connection's live
/// samples (#143). CPU and page life expectancy are coloured by the same thresholds as the health
/// score, page life expectancy's scaled to the latest sample's buffer pool as the score's are
/// (#173); batch requests and waiting tasks have no threshold, so they stay neutral.
/// </summary>
public static class Headlines
{
    public static IReadOnlyList<Headline> From(IReadOnlyList<LiveMetricSample> samples, HealthThresholds thresholds) =>
    [
        Graded(HeadlineKind.Cpu, "CPU", HealthIndicator.Cpu, samples, s => s.SqlCpuPct, thresholds, measured: _ => true),
        Neutral(HeadlineKind.BatchRequests, "Batch requests/s", samples, s => s.BatchRequestsPerSec, Rate),
        Neutral(HeadlineKind.WaitingTasks, "Waiting tasks", samples, s => s.WaitingTasks,
                v => v.ToString("N0", CultureInfo.CurrentCulture)),
        // Zero means the counter wasn't reported (some Azure SQL tiers), as the health score reads it.
        Graded(HeadlineKind.PageLifeExpectancy, "Page life expectancy", HealthIndicator.PageLifeExpectancy, samples,
               s => s.PageLifeExpectancySec, thresholds, measured: v => v > 0),
    ];

    private static Headline Graded(HeadlineKind kind, string label, HealthIndicator indicator, IReadOnlyList<LiveMetricSample> samples,
                                   Func<LiveMetricSample, double> value, HealthThresholds thresholds, Func<double, bool> measured)
    {
        if (samples.Count == 0)
            return new Headline(kind, label, "—", null, Note(indicator, thresholds.Get(indicator)), []);

        var bufferPoolGb = samples[^1].BufferPoolMb / 1024;
        var threshold    = thresholds.For(indicator, bufferPoolGb);
        var note         = Note(indicator, threshold) +
                           (threshold != thresholds.Get(indicator) ? $" (for its {HealthRules.Gigabytes(bufferPoolGb)} buffer pool)" : string.Empty);

        var latest = value(samples[^1]);
        return measured(latest)
            ? new Headline(kind, label, AlertText.Value(indicator, latest),
                           HealthRules.LevelOf(indicator, latest, measured: true, threshold), note, samples.Select(value).ToList())
            : new Headline(kind, label, "Not reported", null, note, []);
    }

    private static Headline Neutral(HeadlineKind kind, string label, IReadOnlyList<LiveMetricSample> samples,
                                    Func<LiveMetricSample, double> value, Func<double, string> format) =>
        samples.Count == 0
            ? new Headline(kind, label, "—", null, "No threshold", [])
            : new Headline(kind, label, format(value(samples[^1])), null, "No threshold", samples.Select(value).ToList());

    // "Warning ≥ 75 % · critical ≥ 90 %", "Warning < 300 s", or "No threshold" with both levels off.
    private static string Note(HealthIndicator indicator, HealthThreshold threshold)
    {
        var parts = new List<string>();
        if (threshold.Warning is { } warning)
            parts.Add($"warning {AlertText.Threshold(indicator, warning)}");
        if (threshold.Critical is { } critical)
            parts.Add($"critical {AlertText.Threshold(indicator, critical)}");
        if (parts.Count == 0)
            return "No threshold";
        var text = string.Join(" · ", parts);
        return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    // "1,234", "12.5": whole numbers once they're big enough for the decimals not to matter.
    private static string Rate(double value) =>
        value.ToString(value >= 100 ? "N0" : "0.#", CultureInfo.CurrentCulture);
}
