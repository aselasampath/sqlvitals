using System.Globalization;

namespace SqlVitals.Engine.History;

public enum HistoryRangeKind { Live, LastHour, Last24Hours, Last7Days, Custom }

/// <summary>
/// What a trend page shows: live data, or a past range read from the monitoring history. The
/// preset ranges end at "now" each time they are resolved, so refreshing moves them along.
/// </summary>
public sealed record HistoryRange
{
    /// <summary>Most points one chart series is given; a longer range is averaged into buckets.</summary>
    public const int MaxPoints = 600;

    private static readonly TimeSpan[] BucketSteps =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),  TimeSpan.FromMinutes(2),  TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),    TimeSpan.FromHours(2),    TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),    TimeSpan.FromHours(12),   TimeSpan.FromDays(1),
    ];

    private HistoryRange(HistoryRangeKind kind, DateTime fromUtc = default, DateTime toUtc = default)
    {
        Kind     = kind;
        _fromUtc = fromUtc;
        _toUtc   = toUtc;
    }

    private readonly DateTime _fromUtc;
    private readonly DateTime _toUtc;

    public HistoryRangeKind Kind { get; }

    public bool IsLive => Kind == HistoryRangeKind.Live;

    public static HistoryRange Live        { get; } = new(HistoryRangeKind.Live);
    public static HistoryRange LastHour    { get; } = new(HistoryRangeKind.LastHour);
    public static HistoryRange Last24Hours { get; } = new(HistoryRangeKind.Last24Hours);
    public static HistoryRange Last7Days   { get; } = new(HistoryRangeKind.Last7Days);

    /// <summary>A fixed range, in UTC. Throws unless it starts before it ends.</summary>
    public static HistoryRange Custom(DateTime fromUtc, DateTime toUtc)
    {
        fromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        toUtc   = DateTime.SpecifyKind(toUtc,   DateTimeKind.Utc);
        if (toUtc <= fromUtc)
            throw new ArgumentException("The range must end after it starts.", nameof(toUtc));
        return new HistoryRange(HistoryRangeKind.Custom, fromUtc, toUtc);
    }

    /// <summary>The UTC times this range covers, the end excluded. Throws for <see cref="Live"/>.</summary>
    public (DateTime FromUtc, DateTime ToUtc) Resolve(DateTime nowUtc)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return Kind switch
        {
            HistoryRangeKind.LastHour    => (nowUtc.AddHours(-1), nowUtc),
            HistoryRangeKind.Last24Hours => (nowUtc.AddDays(-1),  nowUtc),
            HistoryRangeKind.Last7Days   => (nowUtc.AddDays(-7),  nowUtc),
            HistoryRangeKind.Custom      => (_fromUtc, _toUtc),
            _ => throw new InvalidOperationException("Live data has no history range."),
        };
    }

    /// <summary>
    /// How much history one plotted point covers: the smallest round step that keeps the span
    /// within <see cref="MaxPoints"/> points. A day at most, however long the span.
    /// </summary>
    public static TimeSpan BucketFor(TimeSpan span)
    {
        foreach (var step in BucketSteps)
            if (span.Ticks <= step.Ticks * MaxPoints)
                return step;
        return BucketSteps[^1];
    }

    /// <summary>"Last hour", "Last 24 hours", "Last 7 days" or "3 Sep 14:00 – 4 Sep 09:30" in local time.</summary>
    public string Label => Kind switch
    {
        HistoryRangeKind.Live        => "Live",
        HistoryRangeKind.LastHour    => "Last hour",
        HistoryRangeKind.Last24Hours => "Last 24 hours",
        HistoryRangeKind.Last7Days   => "Last 7 days",
        _ => $"{FormatLocal(_fromUtc)} – {FormatLocal(_toUtc)}",
    };

    private static string FormatLocal(DateTime utc) =>
        utc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);

    /// <summary>
    /// Builds a custom range from what the user typed: two local dates, each with a time as
    /// H:mm. Returns false with a message to show when the input can't be used.
    /// </summary>
    public static bool TryParseCustom(DateTime? fromDate, string fromTime, DateTime? toDate, string toTime,
                                      DateTime nowUtc, out HistoryRange? range, out string error)
    {
        range = null;

        if (fromDate is null || toDate is null)
        {
            error = "Pick a start and an end date.";
            return false;
        }
        if (!TryParseTime(fromTime, out var from) || !TryParseTime(toTime, out var to))
        {
            error = "Enter times as hours and minutes, for example 09:30 or 17:05.";
            return false;
        }

        var fromUtc = DateTime.SpecifyKind(fromDate.Value.Date + from, DateTimeKind.Local).ToUniversalTime();
        var toUtc   = DateTime.SpecifyKind(toDate.Value.Date   + to,   DateTimeKind.Local).ToUniversalTime();

        if (toUtc <= fromUtc)
        {
            error = "The end must be after the start.";
            return false;
        }
        if (fromUtc >= DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))
        {
            error = "The start is in the future, so there is no history for it yet.";
            return false;
        }

        range = Custom(fromUtc, toUtc);
        error = string.Empty;
        return true;
    }

    private static bool TryParseTime(string text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out time)
        && time < TimeSpan.FromDays(1);
}
