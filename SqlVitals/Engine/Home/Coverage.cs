using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Home;

/// <summary>A time SqlVitals wasn't watching a server: the PC slept, the app was closed, or monitoring was stopped.</summary>
public readonly record struct CoverageGap(DateTime FromUtc, DateTime ToUtc)
{
    public TimeSpan Length => ToUtc - FromUtc;
}

/// <summary>
/// How much of a window SqlVitals was watching a server (#170), from the times of its scores:
/// every sample stores one, reachable or not, so a stretch without any is a time nobody was
/// watching. A step between scores longer than usual (<see cref="HistoryGaps.Threshold"/>, at
/// least three buckets) is a gap, and so is the start or end of the window when the scores begin
/// or stop that much inside it. A server that couldn't be reached was still watched.
/// </summary>
/// <param name="Monitored">The window less its gaps.</param>
public sealed record Coverage(DateTime FromUtc, DateTime ToUtc, TimeSpan Monitored, IReadOnlyList<CoverageGap> Gaps)
{
    public TimeSpan Window => ToUtc - FromUtc;

    /// <summary>
    /// The window's coverage, from score times (any order, any outside it ignored), read in
    /// buckets of <paramref name="bucket"/>: one time per bucket with a score.
    /// </summary>
    public static Coverage Of(IEnumerable<DateTime> scoreTimesUtc, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        if (toUtc <= fromUtc)
            return new Coverage(fromUtc, toUtc, TimeSpan.Zero, []);

        var times = scoreTimesUtc.Where(t => t >= fromUtc && t <= toUtc).Order().ToList();
        if (times.Count == 0)
            return new Coverage(fromUtc, toUtc, TimeSpan.Zero, [new CoverageGap(fromUtc, toUtc)]);

        var threshold = HistoryGaps.Threshold(times, bucket);
        var gaps = new List<CoverageGap>();
        if (times[0] - fromUtc > threshold)
            gaps.Add(new CoverageGap(fromUtc, times[0]));
        for (var i = 1; i < times.Count; i++)
            if (times[i] - times[i - 1] > threshold)
                gaps.Add(new CoverageGap(times[i - 1], times[i]));
        if (toUtc - times[^1] > threshold)
            gaps.Add(new CoverageGap(times[^1], toUtc));

        var monitored = (toUtc - fromUtc) - TimeSpan.FromTicks(gaps.Sum(g => g.Length.Ticks));
        return new Coverage(fromUtc, toUtc, monitored, gaps);
    }

    /// <summary>
    /// "Monitored 14 h of the last 24 h", "Monitored all of the last 24 h", "Monitored 25 min of
    /// the last 24 h", "Not monitored in the last 24 h". Hours are rounded to the nearest, and
    /// "all" is only said when nothing over a few minutes is missing.
    /// </summary>
    public string Text()
    {
        var last = $"the last {Span(Window)}";
        if (Monitored <= TimeSpan.Zero)
            return $"Not monitored in {last}";
        if (Window - Monitored < TimeSpan.FromMinutes(5))
            return $"Monitored all of {last}";
        // To the nearest hour, but never the whole window: that's "all".
        var shown = Monitored >= TimeSpan.FromHours(1)
            ? TimeSpan.FromHours(Math.Min(Math.Round(Monitored.TotalHours), Math.Floor(Window.TotalHours) - 1))
            : Monitored;
        return $"Monitored {Span(shown)} of {last}";
    }

    /// <summary>"Longest gap 18:02–01:12 (7 h 10 min)", for a tooltip; null when none.</summary>
    public string? LongestGapText(Func<DateTime, string> clock)
    {
        if (Gaps.Count == 0)
            return null;
        var longest = Gaps.MaxBy(g => g.Length);
        return $"Longest gap {clock(longest.FromUtc)}–{clock(longest.ToUtc)} ({AlertText.Duration(longest.Length)})";
    }

    // "24 h", "14 h", "25 min": whole hours, minutes under an hour.
    private static string Span(TimeSpan span) =>
        span >= TimeSpan.FromHours(1)
            ? string.Format(CultureInfo.CurrentCulture, "{0} h", (int)span.TotalHours)
            : string.Format(CultureInfo.CurrentCulture, "{0} min", Math.Max(1, (int)span.TotalMinutes));
}
