using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Tests.History;

public class HistoryGapsTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Point(DateTime Time);

    private static List<Point> Every(TimeSpan step, int count, DateTime? start = null) =>
        Enumerable.Range(0, count).Select(i => new Point((start ?? T0) + step * i)).ToList();

    [Fact]
    public void WithGaps_LeavesContinuousDataAlone()
    {
        var points = Every(TimeSpan.FromSeconds(10), 30);

        var result = HistoryGaps.WithGaps(points, p => p.Time, TimeSpan.FromSeconds(10)).ToList();

        Assert.Equal(points, result.Select(r => r.Item));
    }

    [Fact]
    public void WithGaps_BreaksTheLineWhereTheAppWasClosed()
    {
        var before = Every(TimeSpan.FromSeconds(10), 20);
        var after  = Every(TimeSpan.FromSeconds(10), 20, T0.AddHours(3));

        var result = HistoryGaps.WithGaps(before.Concat(after).ToList(), p => p.Time, TimeSpan.FromSeconds(10)).ToList();

        Assert.Equal(41, result.Count);
        var gap = Assert.Single(result, r => r.Item is null);
        Assert.Equal(before[^1].Time + (after[0].Time - before[^1].Time) / 2, gap.Time);
        Assert.Same(after[0], result[result.IndexOf(gap) + 1].Item);
    }

    [Fact]
    public void Threshold_FollowsTheDataWhenItIsSparserThanTheBucket()
    {
        // Detail snapshots every 5 minutes, plotted on a 1-hour range with 10-second buckets.
        var times = Every(TimeSpan.FromMinutes(5), 12).Select(p => p.Time).ToList();

        Assert.Equal(TimeSpan.FromMinutes(15), HistoryGaps.Threshold(times, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Threshold_IsAtLeastThreeBuckets()
    {
        var times = Every(TimeSpan.FromSeconds(10), 3).Select(p => p.Time).ToList();

        Assert.Equal(TimeSpan.FromMinutes(90), HistoryGaps.Threshold(times, TimeSpan.FromMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(90), HistoryGaps.Threshold([], TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void TypicalStep_IsTheMedianStep()
    {
        var times = new[] { T0, T0.AddMinutes(5), T0.AddMinutes(10), T0.AddHours(5), T0.AddHours(5).AddMinutes(5) };

        Assert.Equal(TimeSpan.FromMinutes(5), HistoryGaps.TypicalStep(times));
        Assert.Equal(TimeSpan.Zero, HistoryGaps.TypicalStep([T0]));
    }

    [Fact]
    public void Threshold_IsNotRaisedByAFewGaps()
    {
        var times = Every(TimeSpan.FromSeconds(10), 20).Select(p => p.Time)
            .Concat(Every(TimeSpan.FromSeconds(10), 20, T0.AddHours(1)).Select(p => p.Time))
            .Concat(Every(TimeSpan.FromSeconds(10), 20, T0.AddHours(2)).Select(p => p.Time))
            .ToList();

        Assert.Equal(TimeSpan.FromSeconds(30), HistoryGaps.Threshold(times, TimeSpan.FromSeconds(10)));
    }
}
