using System.Globalization;
using SqlVitals.Engine.Alerts;

namespace SqlVitals.Engine.Monitoring;

/// <summary>One check of a <see cref="HealthScore"/>: an indicator as graded, its weight and the points it cost.</summary>
public sealed record HealthScoreCheck(HealthReading Reading, int Weight, int PointsLost)
{
    public HealthIndicator Indicator => Reading.Indicator;
    public HealthLevel     Level     => Reading.Level;
    public string          Name      => HealthThresholds.Info(Indicator).Name;

    /// <summary>"82 %", "3.5 min"; "Nothing blocked" or "Not reported" when the sample had no figure.</summary>
    public string ValueText => Reading.Measured
        ? AlertText.Value(Indicator, Reading.Value)
        : Indicator == HealthIndicator.Blocking ? "Nothing blocked" : "Not reported";

    /// <summary>"≥ 75 %", or "off" when that level isn't checked.</summary>
    public string WarningText  => ThresholdText(Reading.Limits.Warning);
    public string CriticalText => ThresholdText(Reading.Limits.Critical);

    private string ThresholdText(double? limit) =>
        limit is null ? "off" : AlertText.Threshold(Indicator, limit);
}

/// <summary>
/// A connection's health as one number from 0 to 100 (#36), so the server that needs attention
/// stands out without reading every indicator.
/// <para>
/// It is built from the same grading as the health dot (<see cref="HealthRules.Grade"/>), on the
/// same thresholds, so the two never disagree. Each <see cref="HealthIndicator"/> is a check worth
/// <see cref="Weight"/> points out of 100. A check past its warning threshold loses half its
/// weight; past its critical threshold, all of it. A check the sample had no figure for keeps its
/// points, as it doesn't count against the dot either. A server that can't be reached scores 0.
/// </para>
/// </summary>
/// <param name="Value">0 to 100; 100 when every check is within its thresholds.</param>
/// <param name="Level">The health dot's level: the worst level of any check, or unavailable.</param>
/// <param name="Checks">Every check, in <see cref="HealthIndicator"/> order; none when unavailable.</param>
/// <param name="Time">When the sample it was graded on was taken, on this PC's clock.</param>
/// <param name="Problem">Why the server couldn't be graded; null unless <see cref="Level"/> is unavailable.</param>
public sealed record HealthScore(
    int                             Value,
    HealthLevel                     Level,
    IReadOnlyList<HealthScoreCheck> Checks,
    DateTime                        Time,
    string?                         Problem = null)
{
    public const int Max = 100;

    /// <summary>
    /// Points a check is worth; they add up to <see cref="Max"/>. CPU, blocking and a full log
    /// stop users' work outright; memory pressure slows everything; queued memory grants and a
    /// full TempDB hit fewer queries.
    /// </summary>
    public static int Weight(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => 20,
        HealthIndicator.Blocking            => 20,
        HealthIndicator.LogSpace            => 20,
        HealthIndicator.PageLifeExpectancy  => 16,
        HealthIndicator.MemoryGrantsPending => 12,
        HealthIndicator.TempDbSpace         => 12,
        _                                   => 0,
    };

    /// <summary>Half the weight for a warning, all of it for critical, nothing otherwise.</summary>
    public static int PointsLost(HealthLevel level, int weight) => level switch
    {
        HealthLevel.Warning  => weight / 2,
        HealthLevel.Critical => weight,
        _                    => 0,
    };

    /// <summary>Scores one sample's readings, taken at <paramref name="time"/>.</summary>
    public static HealthScore From(IReadOnlyList<HealthReading> readings, DateTime time)
    {
        var checks = readings
            .Select(r => new HealthScoreCheck(r, Weight(r.Indicator), PointsLost(r.Level, Weight(r.Indicator))))
            .ToList();
        var value = Math.Clamp(Max - checks.Sum(c => c.PointsLost), 0, Max);
        return new HealthScore(value, HealthRules.Summarize(readings).Level, checks, time);
    }

    /// <summary>The server couldn't be reached or read at <paramref name="time"/>: 0, and why.</summary>
    public static HealthScore Unavailable(string problem, DateTime time) =>
        new(0, HealthLevel.Unavailable, [], time, problem);

    /// <summary>The checks that cost points, the costliest first.</summary>
    public IReadOnlyList<HealthScoreCheck> Lowering =>
        Checks.Where(c => c.PointsLost > 0)
              .OrderByDescending(c => c.PointsLost)
              .ThenBy(c => c.Indicator)
              .ToList();

    /// <summary>"Blocking −20 · CPU −10"; "nothing" when no check cost points.</summary>
    public string LoweredBy()
    {
        if (Level == HealthLevel.Unavailable)
            return "the server couldn't be reached or read";

        var lowering = Lowering;
        return lowering.Count == 0
            ? "nothing"
            : string.Join(" · ", lowering.Select(c => string.Format(CultureInfo.CurrentCulture, "{0} −{1}", c.Name, c.PointsLost)));
    }

    /// <summary>One line for tooltips: "Health score 70/100, lowered by Blocking −20 · CPU −10".</summary>
    public string Summary() => Level switch
    {
        HealthLevel.Unavailable => "Health score 0/100: the server couldn't be reached or read",
        _ when Value == Max     => "Health score 100/100: every check is within its thresholds",
        _                       => string.Format(CultureInfo.CurrentCulture, "Health score {0}/100, lowered by {1}", Value, LoweredBy()),
    };
}
