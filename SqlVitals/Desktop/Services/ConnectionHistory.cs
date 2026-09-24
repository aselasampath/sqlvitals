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

    /// <summary>
    /// Reads the baseline under [<paramref name="fromUtc"/>, <paramref name="toUtc"/>): the same
    /// time last week (see <see cref="HistoryBaseline"/>), in the same buckets as the chart, on a
    /// background thread.
    /// </summary>
    public async Task<BaselineLoad<T>> LoadBaselineAsync<T>(
        DateTime fromUtc, DateTime toUtc, TimeSpan bucket,
        Func<HistoryReader, Guid, DateTime, DateTime, TimeSpan, IReadOnlyList<T>> read)
    {
        var (readFrom, readTo) = HistoryBaseline.ReadWindow(fromUtc, toUtc);
        var items = await Task.Run(() => read(Reader, ConnectionId, readFrom, readTo, bucket));
        return new BaselineLoad<T>(fromUtc, toUtc, bucket, items);
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

/// <summary>
/// One read of a baseline. <see cref="FromUtc"/> and <see cref="ToUtc"/> are the times it lines up
/// under, this week; <see cref="Items"/> are from a week earlier.
/// </summary>
public sealed record BaselineLoad<T>(DateTime FromUtc, DateTime ToUtc, TimeSpan Bucket, IReadOnlyList<T> Items)
{
    public bool Covers(DateTime fromUtc, DateTime toUtc) => FromUtc <= fromUtc && toUtc <= ToUtc;

    /// <summary>For a status line, saying what the dashed lines are, or why there are none.</summary>
    public string Describe() => Items.Count > 0
        ? "Dashed lines: the same time last week"
        : "No baseline: nothing was recorded at this time last week";
}

/// <summary>
/// Keeps a page's baseline loaded for what its charts show. A past range reads its baseline
/// once. Live data moves on, so a live baseline is read with half an hour to spare either side
/// and read again only once the live window runs past it.
/// </summary>
public sealed class BaselineTracker<T>(
    ConnectionHistory history, Func<HistoryReader, Guid, DateTime, DateTime, TimeSpan, IReadOnlyList<T>> read)
{
    private static readonly TimeSpan LiveSlack = TimeSpan.FromMinutes(30);

    private int _version;          // bumped per load and by Clear; a stale load is dropped
    private Task<bool>? _liveLoad;

    /// <summary>The baseline read last, or null before one is read and after <see cref="Clear"/>.</summary>
    public BaselineLoad<T>? Current { get; private set; }

    /// <summary>Forgets the baseline, and drops any read still running.</summary>
    public void Clear()
    {
        _version++;
        _liveLoad = null;
        Current   = null;
    }

    /// <summary>
    /// Reads the baseline under a past range. False when <see cref="Clear"/> or another load came
    /// meanwhile, so the result was dropped.
    /// </summary>
    public async Task<bool> LoadAsync(DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        var version = ++_version;
        var load    = await history.LoadBaselineAsync(fromUtc, toUtc, bucket, read);
        if (version != _version)
            return false;
        Current = load;
        return true;
    }

    /// <summary>
    /// Makes sure the baseline covers live data spanning the last <paramref name="span"/>. True
    /// when that meant reading a new one; false when the one loaded still covers it, or a read
    /// is already running.
    /// </summary>
    public Task<bool> EnsureLiveAsync(TimeSpan span)
    {
        var now = DateTime.UtcNow;
        if (_liveLoad is { IsCompleted: false } || Current?.Covers(now - span, now) == true)
            return Task.FromResult(false);

        var from = now - span - LiveSlack;
        var to   = now + LiveSlack;
        return _liveLoad = LoadAsync(from, to, HistoryRange.BucketFor(to - from));
    }
}
