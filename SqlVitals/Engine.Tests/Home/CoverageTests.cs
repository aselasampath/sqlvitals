using System.Globalization;
using SqlVitals.Engine.Home;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>
/// How much of a window was monitored (#170), from score times: gaps in the middle and at either
/// end, short breaks that aren't gaps, and the words for it.
/// </summary>
public sealed class CoverageTests
{
    private static readonly DateTime To     = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From   = To.AddHours(-24);
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    // A score every bucket from one time to another, both included.
    private static IEnumerable<DateTime> Every5Min(DateTime from, DateTime to)
    {
        for (var t = from; t <= to; t += Bucket)
            yield return t;
    }

    private static Coverage Of(IEnumerable<DateTime> times) => Coverage.Of(times, From, To, Bucket);

    private static string Words(Coverage coverage)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return coverage.Text(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ScoresAllDay_AreAllMonitored()
    {
        var coverage = Of(Every5Min(From, To));

        Assert.Empty(coverage.Gaps);
        Assert.Equal(TimeSpan.FromHours(24), coverage.Monitored);
        Assert.Equal("Monitored all of the last 24 h", Words(coverage));
    }

    [Fact]
    public void NoScores_IsOneGap_AndNotMonitored()
    {
        var coverage = Of([]);

        Assert.Equal([new CoverageGap(From, To)], coverage.Gaps);
        Assert.Equal("Not monitored in the last 24 h", Words(coverage));
    }

    [Fact]
    public void ANightAsleep_IsAGapInTheMiddle()
    {
        var evening = To.AddHours(-18);   // 18:00 the day before
        var morning = To.AddHours(-8);    // 04:00

        var coverage = Of(Every5Min(From, evening).Concat(Every5Min(morning, To)));

        Assert.Equal([new CoverageGap(evening, morning)], coverage.Gaps);
        Assert.Equal(TimeSpan.FromHours(14), coverage.Monitored);
        Assert.Equal("Monitored 14 h of the last 24 h", Words(coverage));
    }

    [Fact]
    public void StartedLate_AndStoppedEarly_AreGapsAtTheEnds()
    {
        var started = From.AddHours(6);
        var stopped = To.AddHours(-3);

        var coverage = Of(Every5Min(started, stopped));

        Assert.Equal([new CoverageGap(From, started), new CoverageGap(stopped, To)], coverage.Gaps);
        Assert.Equal(TimeSpan.FromHours(15), coverage.Monitored);
    }

    [Fact]
    public void ABreakOfAFewBuckets_IsntAGap()
    {
        // Two missing buckets (a busy server, a slow sample): a 15-minute step, three times the usual.
        var gone  = new[] { To.AddHours(-5), To.AddHours(-5).AddMinutes(5) };
        var times = Every5Min(From, To).Except(gone).ToList();

        Assert.Empty(Of(times).Gaps);
        // One more is a gap.
        Assert.Single(Of(times.Except([To.AddHours(-5).AddMinutes(10)])).Gaps);
    }

    [Fact]
    public void ScoresOutsideTheWindow_DontCount()
    {
        var coverage = Of(Every5Min(From.AddHours(-10), From.AddMinutes(-5)).Concat(Every5Min(To.AddHours(-1), To)));

        Assert.Equal([new CoverageGap(From, To.AddHours(-1))], coverage.Gaps);
        Assert.Equal("Monitored 1 h of the last 24 h", Words(coverage));
    }

    [Fact]
    public void Hours_AreRoundedToTheNearest_ButNeverToTheWholeWindow()
    {
        Assert.Equal("Monitored 14 h of the last 24 h", Words(Of(Every5Min(To.AddHours(-13).AddMinutes(-40), To))));
        Assert.Equal("Monitored 23 h of the last 24 h", Words(Of(Every5Min(To.AddHours(-23).AddMinutes(-30), To))));
    }

    [Fact]
    public void UnderAnHour_IsSaidInMinutes()
    {
        Assert.Equal("Monitored 25 min of the last 24 h", Words(Of(Every5Min(To.AddMinutes(-25), To))));
    }

    [Fact]
    public void TheLongestGap_IsSaidForATooltip()
    {
        var coverage = Of(Every5Min(From, To.AddHours(-18)).Concat(Every5Min(To.AddHours(-8), To.AddHours(-2))));

        Assert.Equal("Longest gap 18:00–04:00 (10 h 00 min)",
                     coverage.LongestGapText(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        Assert.Null(Of(Every5Min(From, To)).LongestGapText(t => ""));
    }

    [Fact]
    public void AnEmptyWindow_HasNothingToSay()
    {
        var coverage = Coverage.Of([To], To, To, Bucket);

        Assert.Empty(coverage.Gaps);
        Assert.Equal(TimeSpan.Zero, coverage.Monitored);
    }

    [Fact]
    public void OnTheChart_GapsAreFractionsOfItsWidth_AndAnEmptyChartHasNone()
    {
        var points = Every5Min(From, From.AddHours(6)).Concat(Every5Min(To.AddHours(-6), To))
            .Select(t => new ScorePoint(t, 90, Reachable: true)).ToList();

        var (from, to) = Assert.Single(ScoreTrend.GapSpans(points, From, To, Bucket));

        Assert.Equal(0.25, from, 3);
        Assert.Equal(0.75, to, 3);
        Assert.Empty(ScoreTrend.GapSpans([], From, To, Bucket));
    }

    [Fact]
    public void AServerThatCouldntBeReached_WasStillMonitored()
    {
        var points = Every5Min(From, To).Select((t, i) => new ScorePoint(t, 0, Reachable: i % 2 == 0)).ToList();

        Assert.Empty(ScoreTrend.GapSpans(points, From, To, Bucket));
        Assert.NotEmpty(ScoreTrend.UnreachableSpans(points, From, To, Bucket));
    }
}
