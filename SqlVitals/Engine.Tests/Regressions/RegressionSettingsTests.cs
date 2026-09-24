using System.Globalization;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Engine.Tests.Regressions;

public sealed class RegressionSettingsTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PreviousWeek_IsTheSevenDaysJustBeforeTheRecentPeriod()
    {
        var w = new RegressionWindows(TimeSpan.FromHours(24), RegressionBaselineKind.PreviousWeek);

        var (recentFrom, recentTo, baselineFrom, baselineTo) = w.Resolve(Now);

        Assert.Equal(Now.AddDays(-1), recentFrom);
        Assert.Equal(Now, recentTo);
        Assert.Equal(Now.AddDays(-8), baselineFrom);
        Assert.Equal(Now.AddDays(-1), baselineTo);
        Assert.Equal("Last 24 hours compared with the 7 days before", w.Describe());
    }

    [Fact]
    public void SameTimeLastWeek_IsTheRecentPeriodMovedAWeekBack()
    {
        var w = new RegressionWindows(TimeSpan.FromHours(1), RegressionBaselineKind.SameTimeLastWeek);

        var (recentFrom, _, baselineFrom, baselineTo) = w.Resolve(Now);

        Assert.Equal(Now.AddHours(-1), recentFrom);
        Assert.Equal(Now.AddDays(-7).AddHours(-1), baselineFrom);
        Assert.Equal(Now.AddDays(-7), baselineTo);
        Assert.Equal(TimeSpan.FromDays(7).Add(TimeSpan.FromHours(1)), w.BaselineStartAgo);
        Assert.Equal(TimeSpan.FromDays(7), w.BaselineEndAgo);
        Assert.Equal("Last hour compared with the same time last week", w.Describe());
    }

    [Fact]
    public void RecentLabel_NamesEachChoice()
    {
        Assert.Equal(
            new[] { "Last hour", "Last 4 hours", "Last 24 hours" },
            RegressionWindows.RecentChoices.Select(r => new RegressionWindows(r, RegressionBaselineKind.PreviousWeek).RecentLabel));
    }

    [Theory]
    [InlineData("50", "5", 50, 5)]
    [InlineData(" 25.5 % ", "1", 25.5, 1)]
    [InlineData("1,000", "1,000", 1000, 1000)]
    public void TryParse_AcceptsAPercentageAndAWholeNumber(string pct, string min, double expectedPct, long expectedMin)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.True(RegressionCriteria.TryParse(pct, min, out var criteria, out var error), error);
            Assert.Equal(new RegressionCriteria(expectedPct, expectedMin), criteria);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("", "5")]
    [InlineData("0", "5")]
    [InlineData("-10", "5")]
    [InlineData("abc", "5")]
    [InlineData("100001", "5")]
    [InlineData("50", "0")]
    [InlineData("50", "2.5")]
    [InlineData("50", "")]
    public void TryParse_RejectsWhatCantBeUsed(string pct, string min)
    {
        Assert.False(RegressionCriteria.TryParse(pct, min, out var criteria, out var error));
        Assert.Null(criteria);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(0, RegressionCriteria.DefaultThresholdPct)]
    [InlineData(-5, RegressionCriteria.DefaultThresholdPct)]
    [InlineData(double.NaN, RegressionCriteria.DefaultThresholdPct)]
    [InlineData(1e9, RegressionCriteria.DefaultThresholdPct)]
    [InlineData(120, 120)]
    public void NormalizeThreshold_FallsBackToTheDefault(double saved, double expected) =>
        Assert.Equal(expected, RegressionCriteria.NormalizeThreshold(saved));

    [Theory]
    [InlineData(0, RegressionCriteria.DefaultMinExecutions)]
    [InlineData(2_000_000, RegressionCriteria.DefaultMinExecutions)]
    [InlineData(1, 1)]
    public void NormalizeMinExecutions_FallsBackToTheDefault(long saved, long expected) =>
        Assert.Equal(expected, RegressionCriteria.NormalizeMinExecutions(saved));
}
