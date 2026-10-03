using System.Globalization;

namespace SqlVitals.Desktop.Services;

/// <summary>How a page's data relates to time (#140): the top bar's badge.</summary>
public enum PageKind
{
    /// <summary>Not a view of a server's data: Settings, Export. No badge.</summary>
    None,

    /// <summary>Updates on its own, every so often; it can be paused.</summary>
    Live,

    /// <summary>Read once when the page opens, and again on Refresh.</summary>
    Snapshot,

    /// <summary>A time window from the monitoring history or Query Store.</summary>
    History,
}

/// <summary>
/// What the top bar says about the page on screen (#140): its kind, and how fresh it is. MainWindow
/// builds it from the page's <c>IPageContext</c> and from when the page last loaded.
/// </summary>
/// <param name="Interval">How often a live page updates; null when it isn't one interval.</param>
/// <param name="Paused">A live page whose updates are off.</param>
/// <param name="AsOf">When a live page last updated.</param>
/// <param name="Taken">When a snapshot was read.</param>
/// <param name="Window">A history page's window, when the bar has no picker for it.</param>
/// <param name="CompareWith">What a history page compares its window with, where it does.</param>
/// <param name="DataTo">The newest data a history page shows.</param>
public sealed record PageFreshness(PageKind Kind, TimeSpan? Interval = null, bool Paused = false,
                                   DateTime? AsOf = null, DateTime? Taken = null,
                                   string? Window = null, string? CompareWith = null, DateTime? DataTo = null);

/// <summary>The top bar's words for a page's freshness (#140). Pure, with the clock passed in.</summary>
public static class PageContextText
{
    /// <summary>A snapshot this old is shown in amber: worth reading again before acting on it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>"Live", "Paused", "Snapshot" or "History"; empty for a page with no kind.</summary>
    public static string Badge(PageFreshness f) => f.Kind switch
    {
        PageKind.Live     => f.Paused ? "Paused" : "Live",
        PageKind.Snapshot => "Snapshot",
        PageKind.History  => "History",
        _                 => string.Empty,
    };

    /// <summary>
    /// Beside the badge: "every 5 s · As of 14:02:36", "Taken 14:01:10 · 2 min ago",
    /// "Last 24 hours · compared with the 7 days before · Data to 11:55". Empty when there is nothing to say yet.
    /// </summary>
    public static string Detail(PageFreshness f, DateTime now) => f.Kind switch
    {
        PageKind.Live => Join(
            f.Interval is { } every ? $"every {Interval(every)}" : null,
            f.AsOf is { } asOf ? $"As of {Time(asOf)}" : null),
        PageKind.Snapshot => f.Taken is { } taken ? $"Taken {Time(taken)} · {Age(now - taken)}" : string.Empty,
        PageKind.History => Join(
            f.Window,
            f.CompareWith is { } baseline ? $"compared with {baseline}" : null,
            f.DataTo is { } to ? $"Data to {DataTo(to, now)}" : null),
        _ => string.Empty,
    };

    /// <summary>A snapshot read <see cref="StaleAfter"/> ago or more.</summary>
    public static bool IsStale(PageFreshness f, DateTime now) =>
        f.Kind == PageKind.Snapshot && f.Taken is { } taken && now - taken >= StaleAfter;

    /// <summary>
    /// What a screen reader says for the badge and its detail, in words rather than colour:
    /// "Live, updates every 5 seconds, as of 14:02:36", "Snapshot, taken at 14:01:10, 20 minutes ago, more than 15 minutes old".
    /// </summary>
    public static string AutomationName(PageFreshness f, DateTime now) => f.Kind switch
    {
        PageKind.Live => Spoken(
            f.Paused ? "Live, paused" : "Live",
            f.Interval is { } every ? $"updates every {IntervalWords(every)}" : null,
            f.AsOf is { } asOf ? $"as of {Time(asOf)}" : null),
        PageKind.Snapshot => Spoken(
            "Snapshot",
            f.Taken is { } taken ? $"taken at {Time(taken)}, {AgeWords(now - taken)}" : "not read yet",
            IsStale(f, now) ? $"more than {StaleAfter.TotalMinutes:0} minutes old" : null),
        PageKind.History => Spoken(
            "History",
            f.Window,
            f.CompareWith is { } baseline ? $"compared with {baseline}" : null,
            f.DataTo is { } to ? $"data to {DataTo(to, now)}" : null),
        _ => string.Empty,
    };

    /// <summary>"5 s", "1 min", "1 min 30 s", "2 h".</summary>
    public static string Interval(TimeSpan every) =>
        every.TotalSeconds < 60 ? $"{every.TotalSeconds:0} s"
        : every.TotalMinutes < 60 ? (every.Seconds == 0 ? $"{(int)every.TotalMinutes} min" : $"{(int)every.TotalMinutes} min {every.Seconds} s")
        : every.Minutes == 0 ? $"{(int)every.TotalHours} h" : $"{(int)every.TotalHours} h {every.Minutes} min";

    /// <summary>"just now", "2 min ago", "1 h 5 min ago", "3 d ago".</summary>
    public static string Age(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
        : age < TimeSpan.FromDays(1) ? (age.Minutes == 0 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalHours} h {age.Minutes} min ago")
        : $"{(int)age.TotalDays} d ago";

    private static string AgeWords(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? Plural((int)age.TotalMinutes, "minute") + " ago"
        : age < TimeSpan.FromDays(1) ? Plural((int)age.TotalHours, "hour") + " ago"
        : Plural((int)age.TotalDays, "day") + " ago";

    private static string IntervalWords(TimeSpan every) =>
        every.TotalSeconds < 60 ? Plural((int)every.TotalSeconds, "second")
        : every.TotalMinutes < 60 && every.Seconds == 0 ? Plural((int)every.TotalMinutes, "minute")
        : Plural((int)every.TotalSeconds, "second");

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    // "11:55" today, "2 Oct 11:55" before.
    private static string DataTo(DateTime to, DateTime now) =>
        to.ToString(to.Date == now.Date ? "HH:mm" : "d MMM HH:mm", CultureInfo.CurrentCulture);

    private static string Time(DateTime t) => t.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    // The parts there are, "a · b", and read out "a, b".
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    private static string Spoken(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
