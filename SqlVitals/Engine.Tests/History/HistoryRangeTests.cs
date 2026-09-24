using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Tests.History;

public class HistoryRangeTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_PresetsEndNow()
    {
        Assert.Equal((Now.AddHours(-1), Now), HistoryRange.LastHour.Resolve(Now));
        Assert.Equal((Now.AddDays(-1),  Now), HistoryRange.Last24Hours.Resolve(Now));
        Assert.Equal((Now.AddDays(-7),  Now), HistoryRange.Last7Days.Resolve(Now));
    }

    [Fact]
    public void Resolve_PresetsMoveAlongWithTime()
    {
        var later = Now.AddMinutes(10);
        Assert.Equal((later.AddHours(-1), later), HistoryRange.LastHour.Resolve(later));
    }

    [Fact]
    public void Resolve_CustomIsFixed()
    {
        var range = HistoryRange.Custom(Now.AddDays(-3), Now.AddDays(-2));
        Assert.Equal((Now.AddDays(-3), Now.AddDays(-2)), range.Resolve(Now.AddYears(1)));
    }

    [Fact]
    public void Resolve_LiveHasNoRange()
    {
        Assert.True(HistoryRange.Live.IsLive);
        Assert.Throws<InvalidOperationException>(() => HistoryRange.Live.Resolve(Now));
    }

    [Fact]
    public void Custom_MustEndAfterItStarts()
    {
        Assert.Throws<ArgumentException>(() => HistoryRange.Custom(Now, Now));
        Assert.Throws<ArgumentException>(() => HistoryRange.Custom(Now, Now.AddSeconds(-1)));
    }

    [Fact]
    public void Equality_CustomRangesCompareByTheirTimes()
    {
        Assert.Equal(HistoryRange.Custom(Now.AddHours(-2), Now), HistoryRange.Custom(Now.AddHours(-2), Now));
        Assert.NotEqual(HistoryRange.Custom(Now.AddHours(-2), Now), HistoryRange.Custom(Now.AddHours(-3), Now));
        Assert.NotEqual(HistoryRange.LastHour, HistoryRange.Custom(Now.AddHours(-1), Now));
    }

    [Theory]
    [InlineData(60,         10)]        // 1 h: every 10 s sample, 360 points
    [InlineData(100,        10)]
    [InlineData(101,        15)]
    [InlineData(24 * 60,    300)]       // 24 h: 5 min, 288 points
    [InlineData(7 * 24 * 60, 1800)]     // 7 days: 30 min, 336 points
    [InlineData(90 * 24 * 60, 21600)]   // 90 days: 6 h
    [InlineData(10 * 365 * 24 * 60, 86400)]   // never coarser than a day
    public void BucketFor_KeepsASeriesWithinMaxPoints(int spanMinutes, int expectedSeconds)
    {
        var span   = TimeSpan.FromMinutes(spanMinutes);
        var bucket = HistoryRange.BucketFor(span);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), bucket);
        if (bucket < TimeSpan.FromDays(1))
            Assert.True(span.Ticks / bucket.Ticks <= HistoryRange.MaxPoints);
    }

    [Fact]
    public void TryParseCustom_ReadsLocalDatesAndTimes()
    {
        var ok = HistoryRange.TryParseCustom(new DateTime(2026, 9, 20), "9:05", new DateTime(2026, 9, 21), "17:30",
                                             Now, out var range, out var error);

        Assert.True(ok, error);
        var expectedFrom = new DateTime(2026, 9, 20, 9, 5, 0, DateTimeKind.Local).ToUniversalTime();
        var expectedTo   = new DateTime(2026, 9, 21, 17, 30, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal((expectedFrom, expectedTo), range!.Resolve(Now));
        Assert.Equal(HistoryRangeKind.Custom, range.Kind);
    }

    [Theory]
    [InlineData("",      "17:30", "times")]
    [InlineData("9",     "17:30", "times")]
    [InlineData("25:00", "17:30", "times")]
    [InlineData("9:5",   "17:30", "times")]
    [InlineData("18:00", "17:30", "after the start")]
    public void TryParseCustom_RejectsBadInput(string fromTime, string toTime, string expectedInError)
    {
        var day = new DateTime(2026, 9, 20);

        Assert.False(HistoryRange.TryParseCustom(day, fromTime, day, toTime, Now, out var range, out var error));
        Assert.Null(range);
        Assert.Contains(expectedInError, error);
    }

    [Fact]
    public void TryParseCustom_NeedsBothDates()
    {
        Assert.False(HistoryRange.TryParseCustom(null, "09:00", new DateTime(2026, 9, 20), "10:00", Now, out _, out var error));
        Assert.Contains("date", error);
    }

    [Fact]
    public void TryParseCustom_RejectsARangeStartingInTheFuture()
    {
        var tomorrow = Now.ToLocalTime().Date.AddDays(1);

        Assert.False(HistoryRange.TryParseCustom(tomorrow, "09:00", tomorrow, "10:00", Now, out _, out var error));
        Assert.Contains("future", error);
    }

    [Fact]
    public void Label_NamesTheRange()
    {
        Assert.Equal("Last hour", HistoryRange.LastHour.Label);
        Assert.Equal("Last 7 days", HistoryRange.Last7Days.Label);
        Assert.Contains("–", HistoryRange.Custom(Now.AddHours(-2), Now).Label);
    }
}
