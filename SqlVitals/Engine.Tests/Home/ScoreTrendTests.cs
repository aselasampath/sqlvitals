using System.Globalization;
using SqlVitals.Engine.Home;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>A health score's history laid out for the sparklines and the detail chart (#130).</summary>
public sealed class ScoreTrendTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static ScorePoint At(int minutes, int score, bool reachable = true) => new(T0.AddMinutes(minutes), score, reachable);

    [Fact]
    public void Merge_AddsTheLiveScoresTakenAfterTheHistory_OldestFirst()
    {
        var history = new[] { At(0, 100), At(1, 92) };
        var live    = new[] { At(1, 90), At(2, 70), At(3, 70) };   // the file lags: minute 1 is already there

        Assert.Equal([At(0, 100), At(1, 92), At(2, 70), At(3, 70)], ScoreTrend.Merge(history, live));
        Assert.Equal(live, ScoreTrend.Merge([], live));
    }

    [Fact]
    public void XAndY_Put100AtTheTop_0AtTheBottom_AndClampToTheRange()
    {
        Assert.Equal(0.0, ScoreTrend.Y(100));
        Assert.Equal(1.0, ScoreTrend.Y(0));
        Assert.Equal(0.3, ScoreTrend.Y(70), 6);
        Assert.Equal(0.5, ScoreTrend.X(T0.AddMinutes(30), T0, T0.AddHours(1)));
        Assert.Equal(0.0, ScoreTrend.X(T0.AddMinutes(-5), T0, T0.AddHours(1)));
        Assert.Equal(1.0, ScoreTrend.X(T0.AddHours(2), T0, T0.AddHours(1)));
    }

    [Fact]
    public void Segments_BreakTheLineWhereMonitoringStopped()
    {
        var points = Enumerable.Range(0, 10).Select(m => At(m, 100))
            .Concat(Enumerable.Range(40, 10).Select(m => At(m, 80)))
            .ToList();

        var segments = ScoreTrend.Segments(points, T0, T0.AddHours(1), Minute);

        Assert.Equal(2, segments.Count);
        Assert.Equal(10, segments[0].Count);
        Assert.Equal(new ChartPoint(0, 0), segments[0][0]);
        Assert.Equal(40 / 60.0, segments[1][0].X, 6);
        Assert.Equal(0.2, segments[1][0].Y, 6);
    }

    [Fact]
    public void Segments_LeaveOutPointsOutsideTheRange()
    {
        var segments = ScoreTrend.Segments([At(-10, 50), At(5, 100), At(6, 100), At(90, 50)], T0, T0.AddHours(1), Minute);

        Assert.Single(segments);
        Assert.Equal(2, segments[0].Count);
    }

    [Fact]
    public void UnreachableSpans_AreTheRunsOfUnreachablePoints_WidenedByHalfABucket()
    {
        var points = new[] { At(0, 100), At(10, 0, false), At(11, 0, false), At(12, 0, false), At(13, 100), At(30, 0, false) };

        var spans = ScoreTrend.UnreachableSpans(points, T0, T0.AddHours(1), Minute);

        Assert.Equal(2, spans.Count);
        Assert.Equal(9.5 / 60, spans[0].From, 6);
        Assert.Equal(12.5 / 60, spans[0].To, 6);
        Assert.Equal(29.5 / 60, spans[1].From, 6);
        Assert.Equal(30.5 / 60, spans[1].To, 6);
    }

    [Fact]
    public void Context_SaysWhatTheScoreWas_AndTheLowestToday()
    {
        var today = new[] { At(0, 100), At(5, 100), At(52, 70), At(53, 80), At(54, 80) };

        Assert.Equal("Was 70 at 20:52 · lowest today 70 at 20:52",
                     ScoreTrend.Context(today, current: 80, t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Context_WhenTheScoreHasntChangedToday_OnlyGivesTheLowest()
    {
        var today = new[] { At(0, 100), At(5, 100) };

        Assert.Equal("Lowest today 100", ScoreTrend.Context(today, current: 100, t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        Assert.Equal("", ScoreTrend.Context([], current: 100, t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Context_GivesTheLatestOfEqualLowestScores()
    {
        var today = new[] { At(1, 60), At(2, 100), At(3, 60), At(4, 100) };

        Assert.Equal("Was 60 at 20:03 · lowest today 60 at 20:03",
                     ScoreTrend.Context(today, current: 100, t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Ticks_AreRoundTimesOnTheShownClock_AtMostTheNumberAsked()
    {
        var from = new DateTime(2026, 10, 1, 20, 7, 0, DateTimeKind.Utc);

        // An hour from 20:07: every quarter of an hour, from the first one in it.
        Assert.Equal(new[] { "20:15", "20:30", "20:45", "21:00" },
                     ScoreTrend.Ticks(from, from.AddHours(1), TimeSpan.Zero).Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));

        // A day from 20:07 UTC, shown at UTC+05:30: whole local six hours, not whole UTC ones.
        var india = TimeSpan.FromHours(5.5);
        var day   = ScoreTrend.Ticks(from, from.AddDays(1), india);
        Assert.All(day, t => Assert.Equal(0, (t + india).Hour % 6 + (t + india).Minute));
        Assert.True(day.Count <= 5);

        Assert.Empty(ScoreTrend.Ticks(from, from, TimeSpan.Zero));
    }
}
