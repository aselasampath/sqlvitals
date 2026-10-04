using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Home;

/// <summary>A check that is lowering a health score (#143): its points, and why in one line.</summary>
/// <param name="Points">The points it costs: half its weight past warning, all of it past critical.</param>
public sealed record ScoreCost(HealthIndicator Indicator, string Name, HealthLevel Level, int Weight, int Points, string Why);

/// <summary>A check within its thresholds, for the Overview's compact list: "CPU 12 %".</summary>
public sealed record PassingCheck(HealthIndicator Indicator, string Name, string ValueText);

/// <summary>
/// What is costing a health score its points (#143), in the words the Overview uses: each check
/// that lowers it with its points and why, the costliest first, and the checks that pass.
/// The points are <see cref="HealthScore"/>'s own, so the list always adds up to the score.
/// </summary>
public static class ScoreCosts
{
    public static IReadOnlyList<ScoreCost> Lowering(HealthScore score) =>
        score.Lowering
             .Select(c => new ScoreCost(c.Indicator, c.Name, c.Level, c.Weight, c.PointsLost, Why(c)))
             .ToList();

    public static IReadOnlyList<PassingCheck> Passing(HealthScore score) =>
        score.Checks
             .Where(c => c.PointsLost == 0)
             .Select(c => new PassingCheck(c.Indicator, c.Name, c.ValueText))
             .ToList();

    /// <summary>
    /// "Now 99 %, past critical (≥ 90 %): all 20 of its points." or
    /// "Now 82 %, past warning (≥ 75 %): half of its 20 points."
    /// </summary>
    public static string Why(HealthScoreCheck check)
    {
        var culture = CultureInfo.CurrentCulture;
        var (level, limit) = check.Level == HealthLevel.Critical
            ? ("critical", check.Reading.Limits.Critical)
            : ("warning", check.Reading.Limits.Warning);
        var threshold = AlertText.Threshold(check.Indicator, limit);
        var past      = threshold.Length == 0 ? $"past {level}" : $"past {level} ({threshold})";
        var points    = check.PointsLost == check.Weight
            ? string.Format(culture, "all {0} of its points", check.Weight)
            : string.Format(culture, "half of its {0} points", check.Weight);
        return $"Now {check.ValueText}, {past}: {points}.";
    }
}
