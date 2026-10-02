using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Settings;

/// <summary>
/// What a connection's latest sample would score on thresholds being edited (#131), before they
/// are saved: "On the latest sample, UAT would score 70 → 80".
/// </summary>
/// <param name="Before">The score the sample got, on the thresholds it was graded on.</param>
/// <param name="After">The score on the edited thresholds.</param>
/// <param name="Regraded">The sample graded on the edited thresholds: each check's level and points.</param>
public sealed record ThresholdsPreview(int Before, int After, HealthScore Regraded)
{
    /// <summary>
    /// Grades the latest score's figures again on <paramref name="thresholds"/>. Null when there
    /// is nothing to grade: no sample yet, or the server couldn't be reached.
    /// </summary>
    public static ThresholdsPreview? For(HealthScore? latest, HealthThresholds thresholds)
    {
        if (latest is not { Level: not HealthLevel.Unavailable, Checks.Count: > 0 })
            return null;

        var regraded = Regrade(latest, thresholds);
        return new ThresholdsPreview(latest.Value, regraded.Value, regraded);
    }

    /// <summary>
    /// The same figures on other thresholds, by the dot's own rule (<see cref="HealthRules.LevelOf"/>)
    /// and the score's own weights, so the preview never disagrees with what saving would show.
    /// </summary>
    public static HealthScore Regrade(HealthScore score, HealthThresholds thresholds)
    {
        var readings = score.Checks.Select(check =>
        {
            var reading   = check.Reading;
            var threshold = thresholds.Get(reading.Indicator);
            var level     = HealthRules.LevelOf(reading.Indicator, reading.Value, reading.Measured, threshold);
            var reason    = level == HealthLevel.Healthy
                ? null
                : reading.Reason ?? $"{HealthThresholds.Info(reading.Indicator).Name} {AlertText.Value(reading.Indicator, reading.Value)}";
            return reading with { Level = level, Limits = threshold, Reason = reason };
        }).ToList();
        return HealthScore.From(readings, score.Time);
    }

    /// <summary>"On the latest sample, UAT would score 70 → 80", or "…would still score 70".</summary>
    public string Text(string connectionName) => Before == After
        ? string.Format(CultureInfo.CurrentCulture, "On the latest sample, {0} would still score {1}", connectionName, After)
        : string.Format(CultureInfo.CurrentCulture, "On the latest sample, {0} would score {1} → {2}", connectionName, Before, After);
}
