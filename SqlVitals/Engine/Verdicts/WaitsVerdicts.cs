using System.Globalization;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Verdicts;

/// <summary>
/// Top Waits' verdict (#149): the wait category the server spends most of its waiting on since it
/// started, and what that usually means.
/// </summary>
public static class TopWaitsVerdict
{
    /// <summary>Opens Wait Trend, to see whether it is happening now.</summary>
    public const string Trend = "trend";

    /// <summary>Opens the page that shows the category's cause: Processes, Resource Queries, File I/O, Memory.</summary>
    public const string Cause = "cause";

    /// <summary>What a category of waits usually means, and where to look next.</summary>
    public static string Meaning(string category) => category switch
    {
        "CPU"     => "Queries wait for a CPU after using up their time slice (SOS_SCHEDULER_YIELD), or for parallel threads (CX…): the server is short of CPU, or queries do more work than they need. Resource Queries shows which use the most CPU.",
        "I/O"     => "Queries wait for pages read from disk (PAGEIOLATCH) or for the log to be written (WRITELOG): storage is slow, or queries read more than they need, often for a missing index. File I/O shows the latency of each file.",
        "Memory"  => "Queries wait for a memory grant (RESOURCE_SEMAPHORE) or for a page in memory (PAGELATCH), such as TempDB's allocation pages: memory pressure, or a hot page. Memory Grants shows who waits for memory.",
        "Locks"   => "Sessions wait for locks other sessions hold: blocking. Long transactions and missing indexes make it worse. Processes shows who blocks whom.",
        "Network" => "SQL Server waits for the client to take the results (ASYNC_NETWORK_IO): usually the application reads the rows one at a time while doing other work, rarely the network itself.",
        _         => "Waits outside the usual groups. The grid lists each type; look its name up before acting on it.",
    };

    /// <summary>The page <see cref="Cause"/> opens for a category; null when none says more.</summary>
    public static (string Tag, string Text)? CausePage(string category) => category switch
    {
        "CPU"     => ("ResourceQueries", "Resource Queries"),
        "I/O"     => ("FileIo", "File I/O"),
        "Memory"  => ("Memory", "Memory Grants"),
        "Locks"   => ("Processes", "Processes"),
        _         => null,
    };

    /// <param name="waits">The top wait types since the server started, benign ones left out.</param>
    public static PageVerdict From(IReadOnlyList<TopWaitType> waits)
    {
        var culture = CultureInfo.CurrentCulture;
        if (waits.Count == 0 || waits.Sum(w => w.WaitTimeMs) <= 0)
            return new PageVerdict(VerdictLevel.Ok, "Nothing has waited worth noting",
                "Since the server started, its only waits are the idle ones SQL Server always has.");

        var total = waits.Sum(w => (double)w.WaitTimeMs);
        var categories = waits
            .GroupBy(w => w.WaitCategory)
            .Select(g => (Category: g.Key, Share: g.Sum(w => w.WaitTimeMs) / total, Top: g.OrderByDescending(w => w.WaitTimeMs).First()))
            .OrderByDescending(c => c.Share).ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
        var top = categories[0];

        var headline = top.Category == "Other"
            ? string.Format(culture, "Most waiting is on less common waits: {0:0} %, mostly {1}", top.Share * 100, top.Top.WaitType)
            : string.Format(culture, "Most waiting is on {0}: {1:0} % of the wait time, mostly {2}", top.Category, top.Share * 100, top.Top.WaitType);
        var next = categories.Count > 1
            ? string.Format(culture, " Then {0}, {1:0} %.", categories[1].Category, categories[1].Share * 100)
            : "";
        var why = Meaning(top.Category) + next + " Counted since the server started; Wait Trend shows whether it is happening now.";

        // Locks at the top mean sessions spend most of their waiting blocked by others.
        var level = top.Category == "Locks" ? VerdictLevel.Warning : VerdictLevel.Info;

        List<VerdictAction> actions = [new(Trend, "Wait Trend")];
        if (CausePage(top.Category) is { } cause)
            actions.Insert(0, new(Cause, cause.Text));
        return new PageVerdict(level, headline, why, actions);
    }
}

/// <summary>One wait type's line on Wait Trend: its values per point, a null where nothing was recorded.</summary>
public sealed record WaitTrendSeries(string WaitType, IReadOnlyList<double?> Values);

/// <summary>What Wait Trend is showing, for its verdict.</summary>
public enum WaitTrendState
{
    /// <summary>Live, and not reading: Resume in the top bar starts it.</summary>
    NotStarted,

    /// <summary>Live, reading every few seconds.</summary>
    Live,

    /// <summary>A past range from the monitoring history.</summary>
    History,
}

/// <summary>Wait Trend's verdict (#149): the wait type that waits the most over the lines shown, and whether it is rising.</summary>
public static class WaitTrendVerdict
{
    /// <summary>Opens Top Waits.</summary>
    public const string TopWaits = "top-waits";

    /// <summary>Points needed before a line can be called rising.</summary>
    public const int PointsForTrend = 6;

    /// <param name="series">The lines on the chart.</param>
    /// <param name="unit">"ms of waiting per second", "waiting tasks per second".</param>
    /// <param name="over">"over the last 10 minutes", "over the last 24 hours".</param>
    /// <param name="noData">Why there is nothing to judge, when the lines have no values.</param>
    public static PageVerdict From(IReadOnlyList<WaitTrendSeries> series, WaitTrendState state, string unit, string over, string? noData = null)
    {
        var culture = CultureInfo.CurrentCulture;
        if (series.Count == 0)
            return new PageVerdict(VerdictLevel.Unknown, "No wait type is selected",
                "Tick wait types on the left, or press Top Waits.");

        var averages = series
            .Select(s => (s.WaitType, Values: s.Values.OfType<double>().ToList()))
            .Where(s => s.Values.Count > 0)
            .Select(s => (s.WaitType, s.Values, Average: s.Values.Average()))
            .ToList();
        if (averages.Count == 0)
            return state switch
            {
                WaitTrendState.NotStarted => new PageVerdict(VerdictLevel.Unknown, "Not trending yet",
                    noData ?? "Press Resume (▶) in the top bar: the waits are read every few seconds and drawn as they change."),
                WaitTrendState.Live => new PageVerdict(VerdictLevel.Info, "Reading the first samples",
                    noData ?? "A line needs two reads to show how much waiting happened between them."),
                _ => new PageVerdict(VerdictLevel.Unknown, "Nothing recorded in this range",
                    noData ?? "The monitoring history has no waits for this range."),
            };

        var ranked = averages.OrderByDescending(a => a.Average).ThenBy(a => a.WaitType, StringComparer.Ordinal).ToList();
        var top    = ranked[0];
        if (top.Average <= 0)
            return new PageVerdict(VerdictLevel.Ok, "No waiting on the wait types shown",
                string.Format(culture, "{0:N0} wait type{1} drawn, none waited {2}.", series.Count, series.Count == 1 ? "" : "s", over));

        var total  = ranked.Sum(a => a.Average);
        var rising = Rising(top.Values);
        var headline = string.Format(culture, "{0} waits the most: {1} {2} on average {3}", top.WaitType, Amount(top.Average), unit, over)
                     + (rising ? ", and rising" : "");
        var why = string.Format(culture, "{0:0} % of the waiting drawn.", top.Average / total * 100)
                + (ranked.Count > 1 ? string.Format(culture, " Then {0}, {1}.", ranked[1].WaitType, Amount(ranked[1].Average)) : "")
                + (rising ? " Its last readings are at least twice its first." : "");
        return new PageVerdict(rising ? VerdictLevel.Warning : VerdictLevel.Info, headline, why, [new(TopWaits, "Top Waits")]);
    }

    /// <summary>The last third of the line averages at least twice its first third.</summary>
    public static bool Rising(IReadOnlyList<double> values)
    {
        if (values.Count < PointsForTrend)
            return false;
        var third = values.Count / 3;
        var first = values.Take(third).Average();
        var last  = values.Skip(values.Count - third).Average();
        return last > 0 && last >= first * 2;
    }

    private static string Amount(double value) =>
        string.Format(CultureInfo.CurrentCulture, value >= 100 ? "{0:N0}" : "{0:0.#}", value);
}
