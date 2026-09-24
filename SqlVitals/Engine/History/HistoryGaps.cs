namespace SqlVitals.Engine.History;

/// <summary>
/// Finds the gaps in a history series (SqlVitals closed, monitoring paused, server unreachable)
/// so a chart can break its line there instead of drawing a straight line across hours of no data.
/// </summary>
public static class HistoryGaps
{
    /// <summary>
    /// The longest step between two points that is still continuous data: three times the usual
    /// spacing (the median, so a few gaps don't raise it), and never under three buckets.
    /// </summary>
    public static TimeSpan Threshold(IReadOnlyList<DateTime> times, TimeSpan bucket) =>
        TimeSpan.FromTicks(3 * Math.Max(TypicalStep(times).Ticks, bucket.Ticks));

    /// <summary>The median step between consecutive times; zero with fewer than two.</summary>
    public static TimeSpan TypicalStep(IReadOnlyList<DateTime> times)
    {
        var steps = new List<long>(Math.Max(0, times.Count - 1));
        for (var i = 1; i < times.Count; i++)
            steps.Add((times[i] - times[i - 1]).Ticks);
        steps.Sort();

        return TimeSpan.FromTicks(steps.Count == 0 ? 0 : steps[steps.Count / 2]);
    }

    /// <summary>
    /// The items in order, with a null item wherever the step to the next one is a gap. The
    /// null's time is halfway across the gap.
    /// </summary>
    public static IEnumerable<(DateTime Time, T? Item)> WithGaps<T>(IReadOnlyList<T> items, Func<T, DateTime> timeOf, TimeSpan bucket)
        where T : class
    {
        var times     = items.Select(timeOf).ToList();
        var threshold = Threshold(times, bucket);

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0 && times[i] - times[i - 1] > threshold)
                yield return (times[i - 1] + (times[i] - times[i - 1]) / 2, null);
            yield return (times[i], items[i]);
        }
    }
}
