using System.Globalization;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// The monitoring history of the connection a trend page shows. Pages get null when there is
/// none to read: an ad-hoc connection that isn't saved is never recorded.
/// </summary>
public sealed record ConnectionHistory(HistoryReader Reader, Guid ConnectionId)
{
    public const string UnavailableReason =
        "History is recorded for saved connections only. Save this connection in Settings to start recording it.";

    /// <summary>
    /// Resolves the range against the clock now and reads it on a background thread, so a long
    /// range never holds up the UI.
    /// </summary>
    public async Task<HistoryLoad<T>> LoadAsync<T>(
        HistoryRange range, Func<HistoryReader, Guid, DateTime, DateTime, TimeSpan, IReadOnlyList<T>> read)
    {
        var (fromUtc, toUtc) = range.Resolve(DateTime.UtcNow);
        var bucket = HistoryRange.BucketFor(toUtc - fromUtc);
        var items  = await Task.Run(() => read(Reader, ConnectionId, fromUtc, toUtc, bucket));
        return new HistoryLoad<T>(range, fromUtc, toUtc, bucket, items);
    }

    /// <summary>X-axis labels: time of day within a day, with the date on longer ranges.</summary>
    public static string AxisFormat(TimeSpan span) => span <= TimeSpan.FromDays(1) ? "HH:mm" : "d MMM HH:mm";

    /// <summary>"10 s", "5 min", "1 h", "1 day".</summary>
    public static string FormatBucket(TimeSpan bucket) => bucket switch
    {
        _ when bucket < TimeSpan.FromMinutes(1) => $"{bucket.TotalSeconds:0} s",
        _ when bucket < TimeSpan.FromHours(1)   => $"{bucket.TotalMinutes:0} min",
        _ when bucket < TimeSpan.FromDays(1)    => $"{bucket.TotalHours:0} h",
        _                                       => $"{bucket.TotalDays:0} day",
    };
}

/// <summary>One read of a history range: what was asked for and what came back.</summary>
public sealed record HistoryLoad<T>(HistoryRange Range, DateTime FromUtc, DateTime ToUtc, TimeSpan Bucket, IReadOnlyList<T> Items)
{
    public TimeSpan Span => ToUtc - FromUtc;

    /// <summary>
    /// For a status line: "Last 24 hours from history · 288 points, one every 5 min · times on
    /// this PC's clock". The spacing is what the data has: the bucket when samples were
    /// averaged into it, the snapshot interval when snapshots are further apart.
    /// </summary>
    public string Describe(IReadOnlyList<DateTime> pointTimes, string clock = "this PC's clock")
    {
        var step = HistoryGaps.TypicalStep(pointTimes);
        var text = string.Format(CultureInfo.CurrentCulture, "{0} from history · {1:N0} points", Range.Label, pointTimes.Count);
        if (pointTimes.Count > 1)
            text += $", one every {ConnectionHistory.FormatBucket(step > Bucket ? step : Bucket)}";
        return $"{text} · times on {clock}";
    }

    /// <summary>For an empty chart: why there is nothing, and what records it.</summary>
    public string NoData(string whatIsRecorded) =>
        (Range.Kind == HistoryRangeKind.Custom
            ? $"No history for {Range.Label}. "
            : $"No history for the {Range.Label.ToLower(CultureInfo.CurrentCulture)}. ") + whatIsRecorded;
}
