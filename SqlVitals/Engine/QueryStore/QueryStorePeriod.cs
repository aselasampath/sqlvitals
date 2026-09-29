using System.Globalization;

namespace SqlVitals.Engine.QueryStore;

public enum QueryStorePeriodKind { LastHour, Last24Hours, Last7Days, Last30Days, Custom }

/// <summary>
/// The period the Query Store page reads (#111). A preset ends at the server's "now" each time it's
/// read, so it's sent as minutes back and placed on the server's clock, as Query Regressions does.
/// A custom period is a fixed start and end in UTC.
/// </summary>
public sealed record QueryStorePeriod
{
    private QueryStorePeriod(QueryStorePeriodKind kind, TimeSpan length, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        Kind    = kind;
        Length  = length;
        FromUtc = fromUtc;
        ToUtc   = toUtc;
    }

    public QueryStorePeriodKind Kind { get; }

    /// <summary>How long the period is.</summary>
    public TimeSpan Length { get; }

    /// <summary>A custom period's start, in UTC; null for a preset.</summary>
    public DateTime? FromUtc { get; }

    /// <summary>A custom period's end (excluded), in UTC; null for a preset.</summary>
    public DateTime? ToUtc { get; }

    public static QueryStorePeriod LastHour    { get; } = new(QueryStorePeriodKind.LastHour,    TimeSpan.FromHours(1));
    public static QueryStorePeriod Last24Hours { get; } = new(QueryStorePeriodKind.Last24Hours, TimeSpan.FromDays(1));
    public static QueryStorePeriod Last7Days   { get; } = new(QueryStorePeriodKind.Last7Days,   TimeSpan.FromDays(7));
    public static QueryStorePeriod Last30Days  { get; } = new(QueryStorePeriodKind.Last30Days,  TimeSpan.FromDays(30));

    public static QueryStorePeriod Default => Last24Hours;

    /// <summary>The presets in the order the period picker offers them.</summary>
    public static IReadOnlyList<QueryStorePeriod> Presets { get; } = [LastHour, Last24Hours, Last7Days, Last30Days];

    public bool IsCustom => Kind == QueryStorePeriodKind.Custom;

    /// <summary>A fixed period, in UTC. Throws unless it starts before it ends.</summary>
    public static QueryStorePeriod Custom(DateTime fromUtc, DateTime toUtc)
    {
        fromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        toUtc   = DateTime.SpecifyKind(toUtc,   DateTimeKind.Utc);
        if (toUtc <= fromUtc)
            throw new ArgumentException("The period must end after it starts.", nameof(toUtc));
        return new QueryStorePeriod(QueryStorePeriodKind.Custom, toUtc - fromUtc, fromUtc, toUtc);
    }

    /// <summary>The UTC times the period covers on this PC's clock, the end excluded: for chart axes.</summary>
    public (DateTime FromUtc, DateTime ToUtc) Resolve(DateTime nowUtc)
    {
        if (IsCustom)
            return (FromUtc!.Value, ToUtc!.Value);
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return (nowUtc - Length, nowUtc);
    }

    /// <summary>
    /// The SQL parameters every period query takes: minutes back from the server's now for a preset,
    /// or a fixed start and end. The SQL starts with <c>ISNULL(@fromUtc, …)</c>, so one of them is null.
    /// </summary>
    public object SqlParameters => new
    {
        minutes = IsCustom ? 0 : (int)Length.TotalMinutes,
        fromUtc = FromUtc is { } from ? new DateTimeOffset(from, TimeSpan.Zero) : (DateTimeOffset?)null,
        toUtc   = ToUtc   is { } to   ? new DateTimeOffset(to,   TimeSpan.Zero) : (DateTimeOffset?)null,
    };

    /// <summary>"Last hour" … "Last 30 days", or "3 Sep 14:00 – 4 Sep 09:30" in local time.</summary>
    public string Label => Kind switch
    {
        QueryStorePeriodKind.LastHour    => "Last hour",
        QueryStorePeriodKind.Last24Hours => "Last 24 hours",
        QueryStorePeriodKind.Last7Days   => "Last 7 days",
        QueryStorePeriodKind.Last30Days  => "Last 30 days",
        _ => FormatRange(FromUtc!.Value, ToUtc!.Value),
    };

    // "3 Sep 14:00 – 15:00" when both ends are on the same day.
    private static string FormatRange(DateTime fromUtc, DateTime toUtc)
    {
        var from = fromUtc.ToLocalTime();
        var to   = toUtc.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return from.Date == to.Date
            ? $"{from.ToString("d MMM HH:mm", culture)} – {to.ToString("HH:mm", culture)}"
            : $"{from.ToString("d MMM HH:mm", culture)} – {to.ToString("d MMM HH:mm", culture)}";
    }

    /// <summary>
    /// Builds a custom period from what the user typed: two local dates, each with a time as H:mm.
    /// Returns false with a message to show when the input can't be used.
    /// </summary>
    public static bool TryParseCustom(DateTime? fromDate, string fromTime, DateTime? toDate, string toTime,
                                      DateTime nowUtc, out QueryStorePeriod? period, out string error)
    {
        period = null;

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
            error = "The start is in the future, so Query Store has nothing for it yet.";
            return false;
        }

        period = Custom(fromUtc, toUtc);
        error  = string.Empty;
        return true;
    }

    private static bool TryParseTime(string text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out time)
        && time < TimeSpan.FromDays(1);
}
