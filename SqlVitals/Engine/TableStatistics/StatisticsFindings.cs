using System.Globalization;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.TableStatistics;

/// <summary>
/// What Table Statistics works out from a statistics object (#243). Findings about the data's shape
/// (skew, parameter sensitivity, NULLs, few steps) leave the rating Good: the statistics describe the
/// data correctly, and what to do is in the query. Those that say the statistics may mislead lower it.
/// </summary>
public static class StatisticsFindings
{
    /// <summary>Below this many rows in a histogram, its shape doesn't move a plan enough to mention.</summary>
    public const double MinRowsForShape = 1_000;

    /// <summary>A step holding this share of the rows, far above the average per value, is skew.</summary>
    public const double SkewShare = 0.25;

    /// <summary>"Far" from an estimate: this many times above or below.</summary>
    public const double FarFactor = 10;

    /// <summary>An <c>AVG_RANGE_ROWS</c> below this isn't a frequent value, whatever its neighbours say.</summary>
    public const double MinHiddenRowsPerValue = 100;

    /// <summary>A histogram can have 200 steps: under this many, it may be compressing the values.</summary>
    public const int FewSteps = 100;

    /// <summary>From this many distinct values per step, one step is averaging a lot of them.</summary>
    public const double ManyValuesPerStep = 1_000;

    /// <summary>A sample under this percent, with few rows read per value, is low.</summary>
    public const double LowSamplePct = 10;

    /// <summary>Fewer rows read per distinct value than this can miss values.</summary>
    public const double MinSampledPerValue = 10;

    /// <summary>From this share of the rows being distinct, the column is nearly unique, and a low sample does it little harm.</summary>
    public const double NearlyUnique = 0.9;

    /// <summary>The grid has no density to compare with: a sample under this percent of this many rows is low.</summary>
    public const double GridLowSamplePct = 1;

    public const long GridLowSampleRows = 1_000_000;

    /// <summary>A NULL step holding this share of the rows is worth mentioning.</summary>
    public const double NullShare = 0.25;

    /// <summary>
    /// How many changes start an automatic update. At compatibility level 130 and later, the lower of
    /// 500 + 20 % of rows and SQRT(1000 x rows); below 130, 500 + 20 % of rows.
    /// </summary>
    public static double AutoUpdateThreshold(long rows, int? compatibilityLevel)
    {
        var classic = 500 + 0.2 * rows;
        return compatibilityLevel < 130 ? classic : Math.Min(classic, Math.Sqrt(1000.0 * rows));
    }

    /// <summary>Past the threshold, in a statistics object that has rows to compare with.</summary>
    public static bool IsPastAutoUpdate(StatisticsProperties properties, int? compatibilityLevel) =>
        properties.Rows > 0 && properties.Modifications > AutoUpdateThreshold(properties.Rows, compatibilityLevel);

    /// <summary>The estimate for a value the optimizer can't see, such as a local variable: all density x rows of the leading column.</summary>
    public static double? UnknownEstimate(StatisticsDetails details) =>
        details.Properties is { } properties && details.Density.Count > 0
            ? details.Density[0].RowsPerValue(properties.Rows)
            : null;

    /// <summary>The key's rows are <see cref="FarFactor"/> times or more from the estimate. Never the NULL step: <c>= @value</c> doesn't match NULL.</summary>
    public static bool IsFarFromUnknown(HistogramStep step, double? estimate)
    {
        if (step.IsNullStep || estimate is not { } known || known <= 0)
            return false;
        return step.EqRows >= known * FarFactor || step.EqRows * FarFactor <= known;
    }

    /// <summary>The step's <c>AVG_RANGE_ROWS</c> is above the average of the histogram's steps with a key.</summary>
    public static bool IsAboveAverageRange(HistogramStep step, IReadOnlyList<HistogramStep> histogram)
    {
        var keyed = histogram.Where(s => !s.IsNullStep).ToList();
        return !step.IsNullStep && keyed.Count > 0 && step.AvgRangeRows > keyed.Average(s => s.AvgRangeRows);
    }

    /// <summary>
    /// A table's line in the health column, from the header of each statistics object it has, null
    /// for one never built. Reads no histogram, so it can run over a whole database: the rating an
    /// object shows once opened can be lower, from what only its histogram and density can say.
    /// </summary>
    public static TableStatisticsHealth RateTable(int objectId, IReadOnlyList<StatisticsProperties?> statistics, int? compatibilityLevel)
    {
        if (statistics.Count == 0)
            return new TableStatisticsHealth(objectId, StatisticsHealthState.NoStatistics, null, null, null);

        var built = statistics.OfType<StatisticsProperties>().ToList();
        var hasRows = built.Any(p => p.Rows > 0);
        var rating = StatisticsRating.Good;
        if (hasRows && built.Count < statistics.Count)
            rating = StatisticsRating.Fair;
        if (built.Any(p => p.Rows >= GridLowSampleRows && p.SamplePct < GridLowSamplePct))
            rating = StatisticsRating.Fair;
        if (built.Any(p => IsPastAutoUpdate(p, compatibilityLevel)))
            rating = StatisticsRating.Poor;

        return new TableStatisticsHealth(objectId, StatisticsHealthState.Rated, rating,
                                         built.Max(p => p.ChangedPct), built.Min(p => p.LastUpdated));
    }

    /// <summary>The rating and findings of one statistics object, from the whole of it.</summary>
    public static StatisticsAssessment Assess(StatisticsDetails details)
    {
        if (details.Properties is not { } properties)
            return new StatisticsAssessment(null, []);

        var findings = new List<StatisticsFinding>();
        if (PastThreshold(properties, details.CompatibilityLevel) is { } past)
            findings.Add(past);
        if (LowSample(properties, details.Density) is { } low)
            findings.Add(low);

        var histogram = details.Histogram;
        if (histogram.Sum(s => s.TotalRows) >= MinRowsForShape)
        {
            var estimate = UnknownEstimate(details);
            AddIfAny(findings, Skew(histogram, estimate));
            AddIfAny(findings, ParameterSensitivity(histogram, estimate));
            AddIfAny(findings, Hidden(histogram));
            AddIfAny(findings, Few(histogram, details.Density));
            AddIfAny(findings, Nulls(histogram));
        }

        var ordered = findings.OrderByDescending(f => f.Severity).ToList();
        return new StatisticsAssessment(ordered.Count == 0 ? StatisticsRating.Good : ordered[0].Severity, ordered);
    }

    /// <summary>The Good, Fair or Poor line above the findings.</summary>
    public static PageVerdict Verdict(string statisticsName, StatisticsAssessment assessment)
    {
        var culture = CultureInfo.CurrentCulture;
        if (assessment.Rating is not { } rating)
            return new PageVerdict(VerdictLevel.Unknown, $"{statisticsName} has nothing to rate yet");

        var count = assessment.Findings.Count;
        return rating switch
        {
            StatisticsRating.Poor => new PageVerdict(VerdictLevel.Critical, $"{statisticsName}: Poor",
                "These statistics are likely to mislead the optimizer. " + Say(culture, count)),
            StatisticsRating.Fair => new PageVerdict(VerdictLevel.Warning, $"{statisticsName}: Fair",
                "These statistics may mislead the optimizer for some values. " + Say(culture, count)),
            _ when count == 0 => new PageVerdict(VerdictLevel.Ok, $"{statisticsName}: Good", "Nothing stands out."),
            _ => new PageVerdict(VerdictLevel.Info, $"{statisticsName}: Good",
                "These statistics describe the data, but its shape can still affect plans. " + Say(culture, count)),
        };
    }

    private static string Say(CultureInfo culture, int count) =>
        string.Format(culture, "{0} below.", Sizes.Plural(count, "finding", "findings"));

    private static void AddIfAny(List<StatisticsFinding> findings, StatisticsFinding? finding)
    {
        if (finding is not null)
            findings.Add(finding);
    }

    private static StatisticsFinding? PastThreshold(StatisticsProperties properties, int? compatibilityLevel)
    {
        if (!IsPastAutoUpdate(properties, compatibilityLevel))
            return null;
        var culture = CultureInfo.CurrentCulture;
        return new StatisticsFinding(StatisticsFindingKind.PastAutoUpdateThreshold, StatisticsRating.Poor,
            string.Format(culture, "{0:N0} changes since the last update, past the {1:N0} that start an automatic update.",
                          properties.Modifications, AutoUpdateThreshold(properties.Rows, compatibilityLevel)),
            "Plans compiled now use a histogram that misses those changes. Check that AUTO_UPDATE_STATISTICS is on and that a query has compiled since, or generate an UPDATE STATISTICS script.",
            []);
    }

    private static StatisticsFinding? LowSample(StatisticsProperties properties, IReadOnlyList<DensityEntry> density)
    {
        if (properties.SamplePct is not { } pct || pct >= LowSamplePct || density.Count == 0 || density[0].DistinctValues is not { } distinct)
            return null;
        if (distinct >= NearlyUnique * properties.Rows || properties.RowsSampled >= MinSampledPerValue * distinct)
            return null;
        return new StatisticsFinding(StatisticsFindingKind.LowSampleRate, StatisticsRating.Fair,
            string.Format(CultureInfo.CurrentCulture, "Built from {0:0.##} % of the rows, about {1:0.#} rows per distinct value, so values can be missed or misjudged.",
                          pct, properties.RowsSampled / distinct),
            "Try UPDATE STATISTICS WITH FULLSCAN, or a higher SAMPLE with PERSIST_SAMPLE_PERCENT = ON so later updates keep it.",
            []);
    }

    private static StatisticsFinding? Skew(IReadOnlyList<HistogramStep> histogram, double? estimate)
    {
        var total = histogram.Sum(s => s.TotalRows);
        // Without an average per value to compare with, a huge range can be an identity column: only a key's own rows say it's skew.
        var skewed = histogram.Where(s => !s.IsNullStep && s.TotalRows >= SkewShare * total
                                          && (estimate is > 0 ? Math.Max(s.EqRows, s.AvgRangeRows) >= estimate * FarFactor : s.EqRows >= SkewShare * total))
                              .OrderByDescending(s => s.TotalRows).ToList();
        if (skewed.Count == 0)
            return null;
        return new StatisticsFinding(StatisticsFindingKind.Skew, StatisticsRating.Good,
            string.Format(CultureInfo.CurrentCulture, "Step {0} holds {1:0.#} % of the rows: the values are skewed.", skewed[0].Number, 100 * skewed[0].TotalRows / total),
            "A query for those values and one for the others can need different plans. A filtered index or filtered statistics can give each a closer estimate.",
            skewed.Select(s => s.Number).ToList());
    }

    private static StatisticsFinding? ParameterSensitivity(IReadOnlyList<HistogramStep> histogram, double? estimate)
    {
        var far = histogram.Where(s => IsFarFromUnknown(s, estimate)).ToList();
        if (far.Count == 0)
            return null;
        return new StatisticsFinding(StatisticsFindingKind.ParameterSensitivity, StatisticsRating.Good,
            string.Format(CultureInfo.CurrentCulture, "{0} rows {1:0}x or more from the unknown-value estimate of {2:N1}.",
                          Sizes.Plural(far.Count, "step key has", "step keys have"), FarFactor, estimate),
            "A plan compiled for one of these values can suit others badly, and a local variable always gets the estimate. Try OPTION (RECOMPILE) on the query or OPTIMIZE FOR, and compare the plans.",
            far.Select(s => s.Number).ToList());
    }

    private static StatisticsFinding? Hidden(IReadOnlyList<HistogramStep> histogram)
    {
        var ranged = histogram.Where(s => !s.IsNullStep && s.RangeRows > 0).ToList();
        var hidden = new List<HistogramStep>();
        for (var i = 0; i < ranged.Count; i++)
        {
            var step = ranged[i];
            if (step.AvgRangeRows < MinHiddenRowsPerValue || ranged.Count < 2)
                continue;
            var above = (i == 0 || step.AvgRangeRows >= FarFactor * ranged[i - 1].AvgRangeRows)
                     && (i == ranged.Count - 1 || step.AvgRangeRows >= FarFactor * ranged[i + 1].AvgRangeRows);
            if (above)
                hidden.Add(step);
        }
        if (hidden.Count == 0)
            return null;
        return new StatisticsFinding(StatisticsFindingKind.HiddenFrequentValue, StatisticsRating.Fair,
            string.Format(CultureInfo.CurrentCulture, "Step {0} averages {1:N0} rows per value in its range, far above the steps beside it: a frequent value may be hidden there.",
                          hidden[0].Number, hidden[0].AvgRangeRows),
            "A value between two keys gets that average, whatever its real count. Try UPDATE STATISTICS WITH FULLSCAN, which can give it a step of its own, or filtered statistics for that range.",
            hidden.Select(s => s.Number).ToList());
    }

    private static StatisticsFinding? Few(IReadOnlyList<HistogramStep> histogram, IReadOnlyList<DensityEntry> density)
    {
        var steps = histogram.Count(s => !s.IsNullStep);
        if (steps == 0 || steps >= FewSteps || density.Count == 0 || density[0].DistinctValues is not { } distinct || distinct / steps < ManyValuesPerStep)
            return null;
        return new StatisticsFinding(StatisticsFindingKind.FewSteps, StatisticsRating.Good,
            string.Format(CultureInfo.CurrentCulture, "{0} for about {1:N0} distinct values.", Sizes.Plural(steps, "step", "steps"), distinct),
            "That's fine for evenly spread values, such as an identity column. If some values are much more frequent, look at AVG_RANGE_ROWS in the histogram, and try filtered statistics for the busy ranges.",
            []);
    }

    private static StatisticsFinding? Nulls(IReadOnlyList<HistogramStep> histogram)
    {
        if (histogram.FirstOrDefault(s => s.IsNullStep) is not { } nulls)
            return null;
        var share = nulls.EqRows / histogram.Sum(s => s.TotalRows);
        if (share < NullShare)
            return null;
        return new StatisticsFinding(StatisticsFindingKind.NullShare, StatisticsRating.Good,
            string.Format(CultureInfo.CurrentCulture, "{0:0.#} % of the rows are NULL.", share * 100),
            "Queries that skip NULLs read only the rest. A filtered index or filtered statistics with WHERE column IS NOT NULL can be smaller and closer.",
            [nulls.Number]);
    }
}
