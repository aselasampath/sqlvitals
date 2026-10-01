using System.Globalization;
using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Home;

/// <summary>A point of a score's history (#130): the score at a time, and whether the server could be reached.</summary>
public sealed record ScorePoint(DateTime TimeUtc, int Score, bool Reachable)
{
    public static ScorePoint From(ScoreHistoryPoint point) => new(point.TimeUtc, point.Score, point.Reachable);
}

/// <summary>A place on a chart, as fractions of its width (0 left) and height (0 top).</summary>
public readonly record struct ChartPoint(double X, double Y);

/// <summary>
/// Lays out a health score's history (#130) for the Home sparklines and the server detail chart:
/// 100 at the top and 0 at the bottom, a break wherever monitoring stopped, and the times the
/// server couldn't be reached as spans to shade.
/// </summary>
public static class ScoreTrend
{
    /// <summary>
    /// The history read from the file, then the live scores taken since its last point, oldest
    /// first. The file lags the live scores by a moment, so the two overlap; the live ones win.
    /// </summary>
    public static IReadOnlyList<ScorePoint> Merge(IReadOnlyList<ScorePoint> history, IReadOnlyList<ScorePoint> live)
    {
        var after = history.Count == 0 ? DateTime.MinValue : history[^1].TimeUtc;
        return history.Concat(live.Where(p => p.TimeUtc > after)).OrderBy(p => p.TimeUtc).ToList();
    }

    /// <summary>Where a time falls across [<paramref name="fromUtc"/>, <paramref name="toUtc"/>], from 0 to 1.</summary>
    public static double X(DateTime timeUtc, DateTime fromUtc, DateTime toUtc) =>
        toUtc <= fromUtc ? 1 : Math.Clamp((timeUtc - fromUtc) / (toUtc - fromUtc), 0, 1);

    /// <summary>Where a score falls down the chart: 0 for 100, 1 for 0.</summary>
    public static double Y(int score) => 1 - Math.Clamp(score, 0, 100) / 100.0;

    /// <summary>
    /// The line through the points in the range, broken where the gap to the next point is much
    /// longer than usual (see <see cref="HistoryGaps"/>), so hours with no monitoring aren't drawn
    /// as a straight line. A lone point is a line of one point.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ChartPoint>> Segments(
        IReadOnlyList<ScorePoint> points, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        var inRange   = points.Where(p => p.TimeUtc >= fromUtc && p.TimeUtc <= toUtc).ToList();
        var threshold = HistoryGaps.Threshold(inRange.Select(p => p.TimeUtc).ToList(), bucket);

        var segments = new List<IReadOnlyList<ChartPoint>>();
        List<ChartPoint>? current = null;
        for (var i = 0; i < inRange.Count; i++)
        {
            if (current is null || inRange[i].TimeUtc - inRange[i - 1].TimeUtc > threshold)
                segments.Add(current = new List<ChartPoint>());
            current.Add(new ChartPoint(X(inRange[i].TimeUtc, fromUtc, toUtc), Y(inRange[i].Score)));
        }
        return segments;
    }

    /// <summary>
    /// The times the server couldn't be reached, as [from, to] fractions of the width: each run
    /// of unreachable points, widened by half a bucket either side so a single point shows.
    /// </summary>
    public static IReadOnlyList<(double From, double To)> UnreachableSpans(
        IReadOnlyList<ScorePoint> points, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        var spans = new List<(double, double)>();
        var half  = bucket / 2;
        DateTime? start = null, end = null;

        void Close()
        {
            if (start is { } s && end is { } e)
                spans.Add((X(s - half, fromUtc, toUtc), X(e + half, fromUtc, toUtc)));
            start = end = null;
        }

        foreach (var p in points.Where(p => p.TimeUtc >= fromUtc && p.TimeUtc <= toUtc))
        {
            if (p.Reachable) { Close(); continue; }
            start ??= p.TimeUtc;
            end     = p.TimeUtc;
        }
        Close();
        return spans;
    }

    /// <summary>
    /// "Was 100 at 21:45 · lowest today 70 at 21:52": the latest score that differs from the
    /// current one, and the lowest today (the latest of equals). Empty with nothing to say.
    /// </summary>
    /// <param name="today">Today's points, oldest first.</param>
    /// <param name="time">Formats a point's time for display, e.g. to this PC's local time.</param>
    public static string Context(IReadOnlyList<ScorePoint> today, int current, Func<DateTime, string> time)
    {
        var parts = new List<string>();

        if (today.LastOrDefault(p => p.Score != current) is { } before)
            parts.Add(string.Format(CultureInfo.CurrentCulture, "Was {0} at {1}", before.Score, time(before.TimeUtc)));

        if (today.Count > 0)
        {
            var lowest = today.Min(p => p.Score);
            var when   = today.Last(p => p.Score == lowest);
            parts.Add(lowest == current && parts.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, "Lowest today {0}", lowest)
                : string.Format(CultureInfo.CurrentCulture, "lowest today {0} at {1}", lowest, time(when.TimeUtc)));
        }

        var text = string.Join(" · ", parts);
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    /// <summary>
    /// Evenly spaced times to label the x-axis with, rounded to a whole step (an hour, six hours,
    /// a day…) on the clock they're shown on, so the labels read as round times; never more than
    /// <paramref name="most"/>.
    /// </summary>
    /// <param name="utcOffset">The shown clock's offset from UTC, e.g. this PC's local time's.</param>
    public static IReadOnlyList<DateTime> Ticks(DateTime fromUtc, DateTime toUtc, TimeSpan utcOffset, int most = 5)
    {
        var span = toUtc - fromUtc;
        if (span <= TimeSpan.Zero || most < 1)
            return [];

        TimeSpan[] steps =
        [
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6),
            TimeSpan.FromHours(12), TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
        ];
        var step  = steps.FirstOrDefault(s => span / s <= most, steps[^1]);
        var local = (fromUtc + utcOffset).Ticks;
        var first = new DateTime((local + step.Ticks - 1) / step.Ticks * step.Ticks, DateTimeKind.Utc) - utcOffset;

        var ticks = new List<DateTime>();
        for (var t = first; t <= toUtc && ticks.Count < most; t += step)
            ticks.Add(t);
        return ticks;
    }
}
