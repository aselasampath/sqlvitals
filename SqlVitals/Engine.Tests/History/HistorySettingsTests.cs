using System.Globalization;
using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Tests.History;

public class HistorySettingsTests
{
    [Fact]
    public void Defaults_AreAmongTheChoices()
    {
        Assert.Contains(HistorySettings.DefaultIntervalMinutes, HistorySettings.IntervalChoicesMinutes);
        Assert.Contains(HistorySettings.DefaultRetentionDays, HistorySettings.RetentionChoicesDays);
    }

    [Theory]
    [InlineData(0, 5)]       // missing from an older settings file
    [InlineData(-3, 5)]
    [InlineData(1, 1)]
    [InlineData(15, 15)]
    [InlineData(10, 5)]      // halfway: the lighter choice
    [InlineData(12, 15)]
    [InlineData(1440, 30)]
    public void NormalizeInterval_PicksTheClosestChoice(int saved, int expected) =>
        Assert.Equal(expected, HistorySettings.NormalizeInterval(saved));

    [Theory]
    [InlineData(0, 14)]
    [InlineData(7, 7)]
    [InlineData(20, 14)]
    [InlineData(60, 30)]     // halfway: the smaller file
    [InlineData(70, 90)]
    [InlineData(3650, 90)]
    public void NormalizeRetention_PicksTheClosestChoice(int saved, int expected) =>
        Assert.Equal(expected, HistorySettings.NormalizeRetention(saved));

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(900L, "900 bytes")]
    [InlineData(820L * 1024, "820 KB")]
    [InlineData(13_002_342L, "12.4 MB")]
    [InlineData(1_342_177_280L, "1.25 GB")]
    public void FormatSize_UsesTheLargestFittingUnit(long bytes, string expected)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, HistorySettings.FormatSize(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
