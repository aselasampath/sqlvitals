using SqlVitals.Engine.TableStatistics;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Engine.Tests.TableStatistics;

/// <summary>What Table Statistics works out from a statistics object (#243): findings, ratings and the auto-update rule.</summary>
public class StatisticsFindingsTests
{
    private static readonly DateTime Monday = new(2026, 3, 2);

    private static StatisticsProperties Built(long rows = 100_000, long? sampled = null, long modifications = 0, DateTime? updated = null) =>
        new(updated ?? Monday, rows, sampled ?? rows, 150, modifications);

    /// <summary>A step in a histogram; its average is worked out when it isn't given.</summary>
    private static HistogramStep Step(int number, double range = 0, double eq = 1, double distinct = 1, double? avg = null, string? key = "k") =>
        new(number, key, range, eq, distinct, avg ?? (distinct > 0 ? range / distinct : 0));

    private static StatisticsDetails Details(StatisticsProperties? properties, IReadOnlyList<HistogramStep>? histogram = null, double? density = null, int? compatibility = 160) =>
        new(properties, histogram ?? [], density is { } d ? [new DensityEntry("K", d, 4)] : [], compatibility, false, []);

    private static IEnumerable<StatisticsFindingKind> Kinds(StatisticsDetails details) =>
        StatisticsFindings.Assess(details).Findings.Select(f => f.Kind);

    private static StatisticsFinding Finding(StatisticsDetails details, StatisticsFindingKind kind) =>
        StatisticsFindings.Assess(details).Findings.Single(f => f.Kind == kind);

    // ── Skew ──────────────────────────────────────────────────────────

    [Fact]
    public void AStepHoldingALargeShare_FarAboveTheAveragePerValue_IsSkew()
    {
        // 1,000 values, and one of them holds 90,000 of the 100,000 rows.
        var details = Details(Built(), [Step(1, eq: 90_000), Step(2, range: 5_000, eq: 5, distinct: 500), Step(3, range: 4_995, eq: 5, distinct: 499)], density: 0.001);

        var skew = Finding(details, StatisticsFindingKind.Skew);

        Assert.Equal(StatisticsRating.Good, skew.Severity);
        Assert.Equal([1], skew.Steps);
        Assert.Contains("Step 1", skew.What, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEvenlySpreadIdentity_WithAHugeRangeStep_IsNotSkew()
    {
        // One row per value, so the step holding nearly all the rows averages one row per value too.
        var details = Details(Built(1_000_000),
            [Step(1, eq: 1), Step(2, range: 999_997, eq: 1, distinct: 999_997), Step(3, eq: 1)], density: 0.000001);

        Assert.DoesNotContain(StatisticsFindingKind.Skew, Kinds(details));
    }

    [Fact]
    public void WithoutADensity_ARangeStepsSizeSaysNothing_ButAKeysRowsStillDo()
    {
        var identity = Details(Built(1_000_000), [Step(1, eq: 1), Step(2, range: 999_998, eq: 1, distinct: 999_998)]);
        var frequent = Details(Built(100_000), [Step(1, eq: 90_000), Step(2, range: 10_000, eq: 1, distinct: 10_000)]);

        Assert.DoesNotContain(StatisticsFindingKind.Skew, Kinds(identity));
        Assert.Equal([1], Finding(frequent, StatisticsFindingKind.Skew).Steps);
    }

    [Fact]
    public void ASmallShare_IsNotSkew_HoweverFarAboveTheAverage()
    {
        // 20 % of the rows on one key.
        var details = Details(Built(), [Step(1, eq: 20_000), Step(2, range: 80_000, eq: 1, distinct: 80_000)], density: 0.00001);

        Assert.DoesNotContain(StatisticsFindingKind.Skew, Kinds(details));
    }

    [Fact]
    public void TheNullStep_IsNeverSkew()
    {
        var details = Details(Built(), [Step(1, eq: 90_000, key: null), Step(2, range: 10_000, eq: 1, distinct: 10_000)], density: 0.0001);

        Assert.DoesNotContain(StatisticsFindingKind.Skew, Kinds(details));
    }

    // ── Parameter sensitivity ─────────────────────────────────────────

    [Fact]
    public void KeysFarAboveOrBelowTheUnknownEstimate_AreParameterSensitive()
    {
        // The estimate is 0.001 x 100,000 = 100 rows.
        var details = Details(Built(), [Step(1, eq: 1_000), Step(2, eq: 100), Step(3, eq: 10), Step(4, eq: 999), Step(5, eq: 11), Step(6, range: 98_000, eq: 880, distinct: 980)], density: 0.001);

        var finding = Finding(details, StatisticsFindingKind.ParameterSensitivity);

        Assert.Equal(StatisticsRating.Good, finding.Severity);
        Assert.Equal([1, 3], finding.Steps);
        Assert.Contains("2 step keys", finding.What, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNullStep_IsNotParameterSensitive_AsEqualsNeverMatchesNull()
    {
        var details = Details(Built(), [Step(1, eq: 50_000, key: null), Step(2, range: 49_900, eq: 100, distinct: 499)], density: 0.002);

        Assert.DoesNotContain(StatisticsFindingKind.ParameterSensitivity, Kinds(details));
        Assert.False(StatisticsFindings.IsFarFromUnknown(Step(1, eq: 50_000, key: null), 200));
    }

    [Theory]
    [InlineData(1_000.0, 100.0, true)]
    [InlineData(999.0, 100.0, false)]
    [InlineData(10.0, 100.0, true)]
    [InlineData(11.0, 100.0, false)]
    [InlineData(1_000.0, null, false)]
    [InlineData(1_000.0, 0.0, false)]
    public void FarFromUnknown_IsTenTimesEitherWay(double eq, double? estimate, bool far) =>
        Assert.Equal(far, StatisticsFindings.IsFarFromUnknown(Step(1, eq: eq), estimate));

    [Fact]
    public void TheUnknownEstimate_IsTheLeadingColumnsDensityTimesTheRows()
    {
        var details = new StatisticsDetails(Built(1_000), [], [new DensityEntry("A", 0.5, 4), new DensityEntry("A, B", 0.001, 8)], 160, false, []);

        Assert.Equal(500, StatisticsFindings.UnknownEstimate(details));
        Assert.Null(StatisticsFindings.UnknownEstimate(Details(Built())));
        Assert.Null(StatisticsFindings.UnknownEstimate(Details(null, density: 0.5)));
    }

    [Fact]
    public void AboveAverageRange_ComparesKeyedStepsOnly()
    {
        var histogram = new[] { Step(1, avg: 1_000, key: null), Step(2, avg: 10), Step(3, avg: 30) };

        Assert.True(StatisticsFindings.IsAboveAverageRange(histogram[2], histogram));
        Assert.False(StatisticsFindings.IsAboveAverageRange(histogram[1], histogram));
        Assert.False(StatisticsFindings.IsAboveAverageRange(histogram[0], histogram));
        Assert.False(StatisticsFindings.IsAboveAverageRange(Step(1, avg: 5), []));
    }

    // ── A frequent value hidden in a range ────────────────────────────

    [Fact]
    public void ARangeAveragingFarAboveItsNeighbours_MayHideAFrequentValue()
    {
        var details = Details(Built(), [Step(1, range: 1_000, distinct: 100), Step(2, range: 50_000, distinct: 100), Step(3, range: 1_000, distinct: 100)], density: 0.001);

        var hidden = Finding(details, StatisticsFindingKind.HiddenFrequentValue);

        Assert.Equal(StatisticsRating.Fair, hidden.Severity);
        Assert.Equal([2], hidden.Steps);
    }

    [Fact]
    public void ARangeAboveOnlyOneNeighbour_HidesNothing()
    {
        var details = Details(Built(), [Step(1, range: 1_000, distinct: 100), Step(2, range: 50_000, distinct: 100), Step(3, range: 50_000, distinct: 100)], density: 0.001);

        Assert.DoesNotContain(StatisticsFindingKind.HiddenFrequentValue, Kinds(details));
    }

    [Fact]
    public void ALowAverage_IsNoFrequentValue_WhateverItsNeighboursSay()
    {
        // 99 rows per value, above the 1 of each neighbour, and below the 100 that counts.
        var details = Details(Built(), [Step(1, range: 1_000, distinct: 1_000), Step(2, range: 9_900, distinct: 100), Step(3, range: 1_000, distinct: 1_000)], density: 0.001);

        Assert.DoesNotContain(StatisticsFindingKind.HiddenFrequentValue, Kinds(details));
    }

    [Fact]
    public void TheEdgeSteps_HaveOneNeighbourToCompareWith_AndARangeOnItsOwnHasNone()
    {
        var edge = Details(Built(), [Step(1, range: 50_000, distinct: 100), Step(2, range: 1_000, distinct: 100)], density: 0.001);
        var alone = Details(Built(), [Step(1, range: 50_000, distinct: 100), Step(2, eq: 50)], density: 0.001);

        Assert.Equal([1], Finding(edge, StatisticsFindingKind.HiddenFrequentValue).Steps);
        Assert.DoesNotContain(StatisticsFindingKind.HiddenFrequentValue, Kinds(alone));
    }

    // ── Few steps ─────────────────────────────────────────────────────

    [Fact]
    public void FewStepsForManyDistinctValues_AreMentioned_WithoutLoweringTheRating()
    {
        // 10,000 values on 2 steps; the NULL step isn't counted.
        var details = Details(Built(), [Step(1, key: null, eq: 1), Step(2, eq: 1), Step(3, range: 99_998, eq: 1, distinct: 9_998)], density: 0.0001);

        var few = Finding(details, StatisticsFindingKind.FewSteps);

        Assert.Equal(StatisticsRating.Good, few.Severity);
        Assert.Contains("2 steps", few.What, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyStepsOrFewValuesPerStep_AreNotFew()
    {
        var many = Enumerable.Range(1, StatisticsFindings.FewSteps).Select(n => Step(n, range: 1_000, eq: 1, distinct: 999)).ToList();
        var fewValues = new[] { Step(1, eq: 1), Step(2, range: 99_998, eq: 1, distinct: 1_000) };

        Assert.DoesNotContain(StatisticsFindingKind.FewSteps, Kinds(Details(Built(), many, density: 0.00001)));
        Assert.DoesNotContain(StatisticsFindingKind.FewSteps, Kinds(Details(Built(), fewValues, density: 0.001)));
        Assert.DoesNotContain(StatisticsFindingKind.FewSteps, Kinds(Details(Built(), fewValues)));
    }

    // ── Sample rate ───────────────────────────────────────────────────

    [Fact]
    public void ALowSample_WithFewRowsPerValue_IsLow()
    {
        // 1 % of 1,000,000 rows over 100,000 values: 0.1 rows per value.
        var low = Finding(Details(Built(1_000_000, sampled: 10_000), density: 0.00001), StatisticsFindingKind.LowSampleRate);

        Assert.Equal(StatisticsRating.Fair, low.Severity);
    }

    [Fact]
    public void ALowSampleOfANearlyUniqueColumn_IsExempt()
    {
        // 95 % of the rows are distinct: an average of one row per value is what a sample would find.
        var details = Details(Built(1_000_000, sampled: 10_000), density: 1 / 950_000.0);

        Assert.DoesNotContain(StatisticsFindingKind.LowSampleRate, Kinds(details));
    }

    [Fact]
    public void ALowSample_ReadingPlentyPerValue_IsExempt()
    {
        // 1 % of 1,000,000 rows over 100 values: 100 rows per value.
        Assert.DoesNotContain(StatisticsFindingKind.LowSampleRate, Kinds(Details(Built(1_000_000, sampled: 10_000), density: 0.01)));
    }

    [Fact]
    public void AHighSample_OrNoDensityToCompareWith_IsNotLow()
    {
        Assert.DoesNotContain(StatisticsFindingKind.LowSampleRate, Kinds(Details(Built(1_000_000, sampled: 100_000), density: 0.00001)));
        Assert.DoesNotContain(StatisticsFindingKind.LowSampleRate, Kinds(Details(Built(1_000_000, sampled: 10_000))));
    }

    // ── NULL share ────────────────────────────────────────────────────

    [Fact]
    public void ALargeNullShare_IsMentioned_WithoutLoweringTheRating()
    {
        var details = Details(Built(), [Step(1, eq: 25_000, key: null), Step(2, range: 74_999, eq: 1, distinct: 70_000)], density: 0.00001);

        var nulls = Finding(details, StatisticsFindingKind.NullShare);

        Assert.Equal(StatisticsRating.Good, nulls.Severity);
        Assert.Equal([1], nulls.Steps);
    }

    [Fact]
    public void ASmallNullShare_OrNoNullStep_IsNotMentioned()
    {
        var some = Details(Built(), [Step(1, eq: 24_000, key: null), Step(2, range: 75_999, eq: 1, distinct: 70_000)], density: 0.00001);
        var none = Details(Built(), [Step(1, range: 99_999, eq: 1, distinct: 70_000)], density: 0.00001);

        Assert.DoesNotContain(StatisticsFindingKind.NullShare, Kinds(some));
        Assert.DoesNotContain(StatisticsFindingKind.NullShare, Kinds(none));
    }

    [Fact]
    public void ASmallHistogram_HasNoShapeWorthMentioning()
    {
        // Skewed and half NULL, in 999 rows.
        var details = Details(Built(999), [Step(1, eq: 400, key: null), Step(2, eq: 590), Step(3, range: 8, eq: 1, distinct: 8)], density: 0.1);

        Assert.Empty(Kinds(details));
    }

    // ── The auto-update threshold ─────────────────────────────────────

    [Theory]
    // A small table: 500 + 20 % is lower, at any level.
    [InlineData(1_000L, 120, 700)]
    [InlineData(1_000L, 130, 700)]
    [InlineData(1_000L, null, 700)]
    // A large table: SQRT(1000 x rows) from 130, and null reads as the rule of current servers.
    [InlineData(1_000_000L, 120, 200_500)]
    [InlineData(1_000_000L, 130, 31_622.776601683792)]
    [InlineData(1_000_000L, 160, 31_622.776601683792)]
    [InlineData(1_000_000L, null, 31_622.776601683792)]
    [InlineData(1_000_000L, 129, 200_500)]
    public void TheThreshold_FollowsTheCompatibilityLevel(long rows, int? level, double expected) =>
        Assert.Equal(expected, StatisticsFindings.AutoUpdateThreshold(rows, level), 6);

    [Fact]
    public void TheThreshold_OfNoRows_IsZeroFromLevel130_AndChangesAreNotCountedAgainstIt()
    {
        Assert.Equal(0, StatisticsFindings.AutoUpdateThreshold(0, 160));
        Assert.Equal(500, StatisticsFindings.AutoUpdateThreshold(0, 120));
        Assert.False(StatisticsFindings.IsPastAutoUpdate(Built(0, modifications: 1_000), 160));
    }

    [Theory]
    [InlineData(700L, false)]
    [InlineData(701L, true)]
    public void PastTheThreshold_IsAboveIt(long modifications, bool past) =>
        Assert.Equal(past, StatisticsFindings.IsPastAutoUpdate(Built(1_000, modifications: modifications), 130));

    [Fact]
    public void ChangesPastTheThreshold_AreAFindingThatRatesItPoor_AtTheLevelsOwnThreshold()
    {
        var properties = Built(1_000_000, modifications: 100_000);

        Assert.DoesNotContain(StatisticsFindingKind.PastAutoUpdateThreshold, Kinds(Details(properties, compatibility: 120)));

        var past = Finding(Details(properties, compatibility: 130), StatisticsFindingKind.PastAutoUpdateThreshold);
        Assert.Equal(StatisticsRating.Poor, past.Severity);
        Assert.Empty(past.Steps);
    }

    // ── Nothing to go on ──────────────────────────────────────────────

    [Fact]
    public void Properties_OfNoRows_HaveNoPercentages()
    {
        var properties = Built(0, sampled: 0, modifications: 5);

        Assert.Null(properties.SamplePct);
        Assert.Null(properties.ChangedPct);
    }

    [Fact]
    public void Properties_WorkOutTheirPercentages()
    {
        var properties = Built(1_000, sampled: 250, modifications: 50);

        Assert.Equal(25, properties.SamplePct);
        Assert.Equal(5, properties.ChangedPct);
    }

    [Fact]
    public void NoRows_RateGood_WithNothingToSay()
    {
        var assessment = StatisticsFindings.Assess(Details(Built(0, sampled: 0, modifications: 5)));

        Assert.Equal(StatisticsRating.Good, assessment.Rating);
        Assert.Empty(assessment.Findings);
    }

    [Fact]
    public void AnEmptyHistogram_FindsNoShape_AndKeepsWhatTheHeaderSays()
    {
        var details = Details(Built(1_000, modifications: 900), [], density: 0.001);

        Assert.Equal([StatisticsFindingKind.PastAutoUpdateThreshold], Kinds(details));
    }

    [Fact]
    public void NeverBuilt_HasNothingToRate()
    {
        var assessment = StatisticsFindings.Assess(Details(null));

        Assert.Null(assessment.Rating);
        Assert.Empty(assessment.Findings);
    }

    // ── Rating and verdict ────────────────────────────────────────────

    [Fact]
    public void TheWorstFinding_RatesIt_AndComesFirst()
    {
        // Good, Fair and Poor findings at once: a NULL share, a hidden value and past the threshold.
        var details = Details(Built(100_000, modifications: 90_000),
            [Step(1, eq: 40_000, key: null), Step(2, range: 1_000, distinct: 100), Step(3, range: 50_000, distinct: 100), Step(4, range: 1_000, distinct: 100)],
            density: 0.001);

        var assessment = StatisticsFindings.Assess(details);

        Assert.Equal(StatisticsRating.Poor, assessment.Rating);
        Assert.Equal(StatisticsRating.Poor, assessment.Findings[0].Severity);
        Assert.Equal(assessment.Findings.OrderByDescending(f => f.Severity).Select(f => f.Severity), assessment.Findings.Select(f => f.Severity));
        Assert.Contains(assessment.Findings, f => f.Severity == StatisticsRating.Fair);
        Assert.Contains(assessment.Findings, f => f.Severity == StatisticsRating.Good);
    }

    [Fact]
    public void ShapeFindingsAlone_LeaveItGood()
    {
        var details = Details(Built(), [Step(1, eq: 40_000, key: null), Step(2, range: 59_999, eq: 1, distinct: 59_999)], density: 1 / 60_000.0);

        Assert.Equal(StatisticsRating.Good, StatisticsFindings.Assess(details).Rating);
        Assert.Contains(StatisticsFindingKind.NullShare, Kinds(details));
    }

    [Theory]
    [InlineData(StatisticsRating.Poor, VerdictLevel.Critical, "IX: Poor")]
    [InlineData(StatisticsRating.Fair, VerdictLevel.Warning, "IX: Fair")]
    [InlineData(StatisticsRating.Good, VerdictLevel.Info, "IX: Good")]
    public void TheVerdict_FollowsTheRating_AndCountsTheFindings(StatisticsRating rating, VerdictLevel level, string headline)
    {
        var finding = new StatisticsFinding(StatisticsFindingKind.Skew, rating, "What", "Try", []);

        var verdict = StatisticsFindings.Verdict("IX", new StatisticsAssessment(rating, [finding, finding]));

        Assert.Equal(level, verdict.Level);
        Assert.Equal(headline, verdict.Headline);
        Assert.Contains("2 findings below", verdict.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdict_WithNoFindings_IsOk()
    {
        var verdict = StatisticsFindings.Verdict("IX", new StatisticsAssessment(StatisticsRating.Good, []));

        Assert.Equal(VerdictLevel.Ok, verdict.Level);
        Assert.Equal("IX: Good", verdict.Headline);
    }

    [Fact]
    public void TheVerdict_WithNothingToRate_IsUnknown()
    {
        var verdict = StatisticsFindings.Verdict("IX", new StatisticsAssessment(null, []));

        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.Contains("IX", verdict.Headline, StringComparison.Ordinal);
    }

    // ── The table's line in the grid ──────────────────────────────────

    [Fact]
    public void ATableWithNoStatistics_HasNoRating()
    {
        var health = StatisticsFindings.RateTable(7, [], 160);

        Assert.Equal(new TableStatisticsHealth(7, StatisticsHealthState.NoStatistics, null, null, null), health);
    }

    [Fact]
    public void AllNeverBuilt_RatesGood_AsNothingSaysItHasRows()
    {
        var health = StatisticsFindings.RateTable(7, [null, null], 160);

        Assert.Equal(StatisticsHealthState.Rated, health.State);
        Assert.Equal(StatisticsRating.Good, health.Rating);
        Assert.Null(health.MostChangedPct);
        Assert.Null(health.OldestUpdate);
    }

    [Fact]
    public void OneNeverBuilt_BesideOneWithRows_IsFair()
    {
        Assert.Equal(StatisticsRating.Fair, StatisticsFindings.RateTable(7, [Built(), null], 160).Rating);
        Assert.Equal(StatisticsRating.Good, StatisticsFindings.RateTable(7, [Built(0, sampled: 0), null], 160).Rating);
    }

    [Theory]
    [InlineData(1_000_000L, 9_999L, StatisticsRating.Fair)]
    [InlineData(1_000_000L, 10_000L, StatisticsRating.Good)]
    [InlineData(999_999L, 1L, StatisticsRating.Good)]
    public void ALowSampleOfALargeTable_IsFair(long rows, long sampled, StatisticsRating expected) =>
        Assert.Equal(expected, StatisticsFindings.RateTable(7, [Built(rows, sampled: sampled)], 160).Rating);

    [Fact]
    public void PastTheThreshold_IsPoor_AtTheLevelsOwnThreshold()
    {
        var properties = Built(1_000_000, modifications: 100_000);

        Assert.Equal(StatisticsRating.Poor, StatisticsFindings.RateTable(7, [properties], 160).Rating);
        Assert.Equal(StatisticsRating.Good, StatisticsFindings.RateTable(7, [properties], 120).Rating);
    }

    [Fact]
    public void TheWorstStatistics_RateTheTable()
    {
        var good = Built();
        var fair = Built(1_000_000, sampled: 100);
        var poor = Built(1_000, modifications: 900);

        Assert.Equal(StatisticsRating.Poor, StatisticsFindings.RateTable(7, [good, fair, poor, null], 160).Rating);
        Assert.Equal(StatisticsRating.Fair, StatisticsFindings.RateTable(7, [good, fair], 160).Rating);
    }

    [Fact]
    public void TheLine_HoldsTheMostChangedAndTheOldest()
    {
        var older = Built(1_000, modifications: 10, updated: Monday.AddDays(-30));
        var changed = Built(1_000, modifications: 600, updated: Monday);

        var health = StatisticsFindings.RateTable(7, [changed, null, older], 160);

        Assert.Equal(7, health.ObjectId);
        Assert.Equal(60, health.MostChangedPct);
        Assert.Equal(Monday.AddDays(-30), health.OldestUpdate);
    }

    // ── Privacy ───────────────────────────────────────────────────────

    [Fact]
    public void NoFindingOrVerdict_EverHoldsAKey()
    {
        // Every finding at once, each about steps whose keys are real values from the table.
        var histogram = new[]
        {
            Step(1, eq: 90_000, key: "alice@example.com"),
            Step(2, range: 1_000, distinct: 100, key: "Bob Secretname"),
            Step(3, range: 50_000, distinct: 100, key: "carol@example.com"),
            Step(4, range: 1_000, distinct: 100, key: "Dave Secretname"),
            Step(5, eq: 90_000, key: null),
        };
        var details = Details(Built(1_000_000, sampled: 10_000, modifications: 900_000), histogram, density: 0.00001);

        var assessment = StatisticsFindings.Assess(details);
        var verdict = StatisticsFindings.Verdict("IX_Customers_Email", assessment);

        Assert.Equal(Enum.GetValues<StatisticsFindingKind>().Order(), assessment.Findings.Select(f => f.Kind).Order());
        var words = assessment.Findings.SelectMany(f => new[] { f.What, f.Try }).Append(verdict.Headline).Append(verdict.Why!);
        foreach (var text in words)
            foreach (var step in histogram.Where(s => !s.IsNullStep))
                Assert.DoesNotContain(step.Key!, text, StringComparison.OrdinalIgnoreCase);
    }
}
