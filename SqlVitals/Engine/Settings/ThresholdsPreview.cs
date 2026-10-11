using System.Globalization;
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
    /// The same figures on other thresholds and weights, by the dot's own rule
    /// (<see cref="HealthRules.Regrade"/>, which scales page life expectancy and spares a small
    /// reusable log as a sample's grading does), so the preview never disagrees with what saving
    /// would show.
    /// </summary>
    public static HealthScore Regrade(HealthScore score, HealthThresholds thresholds)
    {
        var readings = score.Checks.Select(check => HealthRules.Regrade(check.Reading, thresholds)).ToList();
        return HealthScore.From(readings, score.Time, thresholds.Weights);
    }

    /// <summary>"On the latest sample, UAT would score 70 → 80", or "…would still score 70".</summary>
    public string Text(string connectionName) => Before == After
        ? string.Format(CultureInfo.CurrentCulture, "On the latest sample, {0} would still score {1}", connectionName, After)
        : string.Format(CultureInfo.CurrentCulture, "On the latest sample, {0} would score {1} → {2}", connectionName, Before, After);
}
