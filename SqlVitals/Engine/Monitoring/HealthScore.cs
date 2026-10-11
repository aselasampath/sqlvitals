using System.Globalization;
using SqlVitals.Engine.Alerts;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// One check of a <see cref="HealthScore"/>: an indicator as graded, its weight, what that weight
/// is worth out of 100 among the checks that reported, and the points it cost.
/// </summary>
/// <param name="Weight">Its weight as set in Settings; 0 leaves it out of the score.</param>
/// <param name="PointsLost">Half of <paramref name="Worth"/> past warning, all of it past critical.</param>
/// <param name="Worth">Its share of the 100 points: its weight scaled over the checks that reported (#173). 0 when unknown or weighted 0.</param>
public sealed record HealthScoreCheck(HealthReading Reading, int Weight, int PointsLost, int Worth)
{
    public HealthIndicator Indicator => Reading.Indicator;
    public HealthLevel     Level     => Reading.Level;
    public string          Name      => HealthThresholds.Info(Indicator).Name;

    /// <summary>No figure to grade (#173): grey, and left out of the score.</summary>
    public bool IsUnknown => Level == HealthLevel.Unknown;

    /// <summary>It counts towards the score: it reported, and its weight isn't 0.</summary>
    public bool IsScored => !IsUnknown && Weight > 0;

    /// <summary>"82 %", "3.5 min"; "Nothing blocked"; "No data" when the server didn't report it.</summary>
    public string ValueText => !Reading.Measured
        ? "No data"
        : Indicator == HealthIndicator.Blocking && Reading.Value <= 0 ? "Nothing blocked" : AlertText.Value(Indicator, Reading.Value);

    /// <summary>"≥ 75 %", or "off" when that level isn't checked.</summary>
    public string WarningText  => ThresholdText(Reading.Limits.Warning);
    public string CriticalText => ThresholdText(Reading.Limits.Critical);

    /// <summary>What is wrong in a few words: "blocking for 4 min" (<see cref="HealthRules.Phrase"/>).</summary>
    public string Phrase => HealthRules.Phrase(Reading);

    private string ThresholdText(double? limit) =>
        limit is null ? "off" : AlertText.Threshold(Indicator, limit);
}

/// <summary>
/// A connection's health as one number from 0 to 100 (#36), so the server that needs attention
/// stands out without reading every indicator.
/// <para>
/// It is built from the same grading as the health dot (<see cref="HealthRules.Grade"/>), on the
/// same thresholds, so the two never disagree. Each <see cref="HealthIndicator"/> is a check with a
/// weight (<see cref="HealthWeights"/>). A check with no figure is <see cref="HealthLevel.Unknown"/>
/// and is left out (#173): the score is scaled over the checks that reported, so missing data
/// never counts as healthy. A check past its warning threshold loses half its share of the 100
/// points; past its critical threshold, all of it. A server that can't be reached scores 0.
/// </para>
/// </summary>
/// <param name="Value">0 to 100; 100 when every check that reported is within its thresholds.</param>
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

    /// <summary>A check's default weight; they add up to <see cref="Max"/>. See <see cref="HealthWeights.DefaultOf"/>.</summary>
    public static int Weight(HealthIndicator indicator) => HealthWeights.DefaultOf(indicator);

    /// <summary>Half the points for a warning, all of them for critical, nothing otherwise.</summary>
    public static int PointsLost(HealthLevel level, int worth) => level switch
    {
        HealthLevel.Warning  => worth / 2,
        HealthLevel.Critical => worth,
        _                    => 0,
    };

    /// <summary>Scores one sample's readings, taken at <paramref name="time"/>, on the default weights.</summary>
    public static HealthScore From(IReadOnlyList<HealthReading> readings, DateTime time) =>
        From(readings, time, HealthWeights.Default);

    /// <summary>
    /// Scores one sample's readings on <paramref name="weights"/>. Each check that reported gets
    /// its weight's share of 100 as whole points that add up to exactly 100; the score is 100 less
    /// the points the checks lost, so the breakdown always adds up to it.
    /// </summary>
    public static HealthScore From(IReadOnlyList<HealthReading> readings, DateTime time, HealthWeights? weights)
    {
        weights ??= HealthWeights.Default;
        var worths = Worths(readings.Select(r => r.Level == HealthLevel.Unknown ? 0 : weights.Get(r.Indicator)).ToList());

        var checks = readings.Select((r, i) =>
            new HealthScoreCheck(r, weights.Get(r.Indicator), PointsLost(r.Level, worths[i]), worths[i])).ToList();

        var value = Math.Clamp(Max - checks.Sum(c => c.PointsLost), 0, Max);
        return new HealthScore(value, HealthRules.Summarize(readings).Level, checks, time);
    }

    // The 100 points shared out in proportion to the weights, as whole points that add up to 100:
    // each gets its share rounded down, then the points left go to the largest remainders, the
    // first check first on a tie. All weights 0 gives every check 0.
    private static int[] Worths(IReadOnlyList<int> weights)
    {
        var total  = weights.Sum();
        var worths = new int[weights.Count];
        if (total == 0)
            return worths;

        var shares = weights.Select(w => w * (double)Max / total).ToList();
        for (var i = 0; i < worths.Length; i++)
            worths[i] = (int)Math.Floor(shares[i]);

        var left = Max - worths.Sum();
        foreach (var i in Enumerable.Range(0, worths.Length).OrderByDescending(i => shares[i] - worths[i]).ThenBy(i => i).Take(left))
            worths[i]++;
        return worths;
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

    /// <summary>The checks with no figure, left out of the score (#173).</summary>
    public IReadOnlyList<HealthScoreCheck> LeftOut => Checks.Where(c => c.IsUnknown).ToList();

    /// <summary>The checks that are past a threshold, the worst and costliest first.</summary>
    public IReadOnlyList<HealthScoreCheck> Past =>
        Checks.Where(c => c.Level is HealthLevel.Warning or HealthLevel.Critical)
              .OrderByDescending(c => c.Level)
              .ThenByDescending(c => c.PointsLost)
              .ThenByDescending(c => c.Weight)
              .ThenBy(c => c.Indicator)
              .ToList();

    /// <summary>"Critical", "Warning", "Healthy" or "Unreachable".</summary>
    public static string Label(HealthLevel level) => level switch
    {
        HealthLevel.Critical    => "Critical",
        HealthLevel.Warning     => "Warning",
        HealthLevel.Healthy     => "Healthy",
        HealthLevel.Unavailable => "Unreachable",
        _                       => "Unknown",
    };

    /// <summary>
    /// The worst state and its reason, which leads wherever the score is shown (#173):
    /// "Critical: blocking for 4 min", "Warning: CPU at 82 % (+1 more)", "Healthy: every check passes",
    /// "Healthy: 5 of 6 checks pass, 1 has no data".
    /// </summary>
    public string Verdict
    {
        get
        {
            if (Level == HealthLevel.Unavailable)
                return "Unreachable: the server can't be reached or read";

            var past = Past;
            if (past.Count > 0)
                return $"{Label(Level)}: {past[0].Phrase}" + (past.Count > 1 ? $" (+{past.Count - 1} more)" : string.Empty);
            // A level with no check to explain it (a score read back without its checks) is named as it is.
            if (Level is not HealthLevel.Healthy)
                return Label(Level);

            var leftOut = LeftOut.Count;
            return leftOut == 0
                ? "Healthy: every check passes"
                : $"Healthy: {Checks.Count - leftOut} of {Checks.Count} checks pass, {leftOut} {(leftOut == 1 ? "has" : "have")} no data";
        }
    }

    /// <summary>"1 check left out of the score, no data: TempDB space used"; null when every check reported.</summary>
    public string? LeftOutText
    {
        get
        {
            var leftOut = LeftOut;
            return leftOut.Count == 0
                ? null
                : $"{(leftOut.Count == 1 ? "1 check" : $"{leftOut.Count} checks")} left out of the score, no data: " +
                  string.Join(", ", leftOut.Select(c => c.Name));
        }
    }

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

    /// <summary>
    /// One line for tooltips, the verdict first: "Critical: blocking for 4 min · health score 70/100,
    /// lowered by Blocking −20 · CPU −10".
    /// </summary>
    public string Summary() => Level switch
    {
        HealthLevel.Unavailable => "Unreachable · health score 0/100: the server couldn't be reached or read",
        _ => string.Format(CultureInfo.CurrentCulture, "{0} · health score {1}/100", Verdict, Value) +
             (Lowering.Count > 0 ? $", lowered by {LoweredBy()}" : string.Empty) +
             (LeftOutText is { } leftOut ? $" · {leftOut}" : string.Empty),
    };
}
