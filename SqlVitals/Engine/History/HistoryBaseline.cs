namespace SqlVitals.Engine.History;

/// <summary>
/// The baseline a trend chart can be compared with: the same time last week, read from the
/// monitoring history and moved a week forward so it lines up under the current data.
///
/// "The same time" is on the chart's wall clock, not exactly 168 hours back: across a
/// daylight-saving change, 09:00 today is compared with 09:00 last week, when the working day
/// started then too. A 168-hour window can be up to an hour off from that, so the baseline is
/// read an hour wider on each side, shifted on the wall clock, and clipped to the chart.
/// </summary>
public static class HistoryBaseline
{
    /// <summary>How far back the baseline is.</summary>
    public static readonly TimeSpan Lag = TimeSpan.FromDays(7);

    // Covers a daylight-saving change (at most an hour) between last week and now.
    private static readonly TimeSpan ClockMargin = TimeSpan.FromHours(1);

    /// <summary>
    /// The UTC range to read for the baseline under [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>): the same range a week earlier, an hour wider on each side.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) ReadWindow(DateTime fromUtc, DateTime toUtc) =>
        (DateTime.SpecifyKind(fromUtc - Lag - ClockMargin, DateTimeKind.Utc),
         DateTime.SpecifyKind(toUtc   - Lag + ClockMargin, DateTimeKind.Utc));

    /// <summary>
    /// Last week's points (in the chart's clock, with null items where the line breaks) moved to
    /// the same wall-clock time this week, keeping those within [<paramref name="from"/>,
    /// <paramref name="to"/>], the times the chart shows.
    /// </summary>
    public static IEnumerable<(DateTime Time, T? Item)> Shift<T>(IEnumerable<(DateTime Time, T? Item)> lastWeek, DateTime from, DateTime to)
        where T : class
    {
        foreach (var (time, item) in lastWeek)
        {
            // AddDays keeps the wall-clock time; the Kind (local or server time) carries over.
            var shifted = time.AddDays(Lag.TotalDays);
            if (shifted >= from && shifted <= to)
                yield return (shifted, item);
        }
    }
}
