using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Services;

/// <summary>What a page shows of its rows when opened as evidence (#145).</summary>
public enum FocusKind
{
    /// <summary>Every row: the page itself is the evidence.</summary>
    None,

    /// <summary>Processes: one blocking chain, by its head blocker's session (or the chain waiting longest).</summary>
    BlockingChain,

    /// <summary>Resource Queries: the By CPU tab, with the queries run since the window's start.</summary>
    TopCpu,

    /// <summary>DB Storage: the files of one database.</summary>
    Database,

    /// <summary>Memory Grants: the grants still waiting for memory.</summary>
    PendingGrants,

    /// <summary>Active Waits: one session, and the sessions it blocks.</summary>
    Session,

    /// <summary>Query Store: one query.</summary>
    Query,
}

/// <summary>A page's filter (#145).</summary>
/// <param name="Id">The head blocker's or the session's id, or the query id; null for the chain waiting longest.</param>
/// <param name="Name">The database's name.</param>
public sealed record FocusFilter(FocusKind Kind, long? Id = null, string? Name = null)
{
    public static FocusFilter None { get; } = new(FocusKind.None);

    public bool IsNone => Kind == FocusKind.None;
}

/// <summary>Where a focused page was opened from.</summary>
public enum FocusSource { Alert, HealthCheck, Page }

/// <summary>
/// What a focused page was opened from, for its "Opened from alert" banner (#145): the alert with
/// its times, the health check, or the page whose link it was.
/// </summary>
/// <param name="What">"CPU · critical", "Blocking 3.5 min (3 blocked)", "session 87".</param>
/// <param name="Page">The page whose link it was, e.g. "Processes"; null for an alert or check.</param>
/// <param name="EndedUtc">When the alert ended; null while it is active, and for a check or link.</param>
public sealed record FocusOrigin(FocusSource Source, string What, string? Page = null,
                                 DateTime? StartedUtc = null, DateTime? EndedUtc = null)
{
    /// <summary>An alert that has ended: the page shows the past only if it can read the history.</summary>
    public bool IsPast => EndedUtc is not null;
}

/// <summary>
/// What a page is opened to show (#145): a filter on its rows, a time window, and where it was
/// opened from. Part of its <see cref="Route"/>, so Back and Forward open it the same way.
/// </summary>
/// <param name="FromUtc">The window's start; null when there is none.</param>
/// <param name="ToUtc">The window's end; null for "now", while the alert is active.</param>
/// <param name="HistoryTag">The page that reads the window from the monitoring history, offered
/// by a page that only shows the server now. Null when SqlVitals keeps no history of it.</param>
public sealed record PageFocus(FocusFilter Filter, FocusOrigin Origin, DateTime? FromUtc = null, DateTime? ToUtc = null,
                               string? HistoryTag = null);

/// <summary>Where a health check is looked into: the page, how it is filtered, and the page with its history.</summary>
/// <param name="Text">The link's words: "Top CPU queries", "Blocking chain".</param>
/// <param name="PageTag">The page's tag in <see cref="NavMenu"/>.</param>
/// <param name="HistoryTag">The page that charts the check's past, or null when SqlVitals keeps none.</param>
/// <param name="NowOnly">The filter describes the server now (a blocking chain, a grant waiting), so it
/// can't apply to an alert that has ended.</param>
public sealed record Evidence(string Text, string PageTag, FocusKind Filter, string? HistoryTag, bool NowOnly = false);

/// <summary>
/// Every alert's and health check's evidence route (#145): the page that shows why, already
/// filtered to the problem and set to the alert's time. Pure; MainWindow does the navigating.
/// </summary>
public static class EvidenceRoutes
{
    /// <summary>How far either side of a past alert its window reaches.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(15);

    /// <summary>The pages that read a time window from the monitoring history.</summary>
    public static bool ShowsPast(string tag) => tag is "LiveMetrics";

    /// <summary>Where each health check, and so each alert, is looked into. Throws for an indicator without one.</summary>
    public static Evidence For(HealthIndicator indicator) => indicator switch
    {
        // Live charts keep CPU, lock waits, page life expectancy and grants pending in the history;
        // the blocked sessions themselves, log and TempDB space aren't kept.
        HealthIndicator.Cpu                 => new("Top CPU queries", "ResourceQueries", FocusKind.TopCpu, "LiveMetrics"),
        HealthIndicator.Blocking            => new("Blocking chain", "Processes", FocusKind.BlockingChain, "LiveMetrics", NowOnly: true),
        HealthIndicator.PageLifeExpectancy  => new("Memory", "Memory", FocusKind.None, "LiveMetrics"),
        HealthIndicator.MemoryGrantsPending => new("Memory grants", "Memory", FocusKind.PendingGrants, "LiveMetrics", NowOnly: true),
        HealthIndicator.LogSpace            => new("Log space", "DbStorage", FocusKind.Database, null),
        HealthIndicator.TempDbSpace         => new("TempDB", "TempDb", FocusKind.None, null),
        _ => throw new ArgumentOutOfRangeException(nameof(indicator), indicator, "No page explains this check."),
    };

    /// <summary>
    /// An alert's evidence: its page on its server, filtered to the problem, with the alert's window
    /// (start − 15 min to end + 15 min, or to now while it is active). An alert that has ended keeps
    /// only a filter that still means something (the database, not a blocking chain long gone).
    /// </summary>
    public static Route ForAlert(Alert alert)
    {
        var evidence = For(alert.Indicator);
        var filter   = alert.EndedUtc is not null && evidence.NowOnly ? FocusFilter.None : Filter(evidence.Filter, alert.Detail);
        var origin   = new FocusOrigin(FocusSource.Alert,
                                       $"{HealthThresholds.Info(alert.Indicator).Name} · {AlertText.Severity(alert.Severity).ToLowerInvariant()}",
                                       StartedUtc: alert.StartedUtc, EndedUtc: alert.EndedUtc);
        var focus = new PageFocus(filter, origin, alert.StartedUtc - Margin, alert.EndedUtc + Margin, evidence.HistoryTag);
        return new Route(evidence.PageTag, ConnectionId: alert.ConnectionId, Focus: focus);
    }

    /// <summary>
    /// A health check's evidence, as the server stands now: its page on the server, filtered as an
    /// active alert's would be.
    /// </summary>
    /// <param name="detail">What the check said, e.g. "Log 93% full (Sales)": names the database.</param>
    public static Route ForCheck(HealthIndicator indicator, Guid server, string? detail)
    {
        var evidence = For(indicator);
        var what     = string.IsNullOrWhiteSpace(detail) ? HealthThresholds.Info(indicator).Name : detail;
        var focus    = new PageFocus(Filter(evidence.Filter, detail), new FocusOrigin(FocusSource.HealthCheck, what));
        return new Route(evidence.PageTag, ConnectionId: server, Focus: focus);
    }

    /// <summary>A link from one page to another that carries its filter: a session, a query.</summary>
    /// <param name="fromPage">The page the link is on, e.g. "Processes".</param>
    public static Route Link(string tag, Guid? server, FocusFilter filter, string fromPage) =>
        new(tag, ConnectionId: server, Focus: new PageFocus(filter, new FocusOrigin(FocusSource.Page, Describe(filter), fromPage)));

    /// <summary>
    /// For a page that only shows the server now, opened for an alert that has ended: the page that
    /// charts the alert's window from the history. Null when there is none, or nothing to offer.
    /// </summary>
    public static Route? History(Route route) =>
        route.Focus is { Origin.IsPast: true, HistoryTag: { } tag, FromUtc: not null } focus && !ShowsPast(route.Tag)
            ? new Route(tag, route.ConnectionId, Focus: focus with { Filter = FocusFilter.None, HistoryTag = null })
            : null;

    /// <summary>The route without its focus: the page as the sidebar opens it.</summary>
    public static Route Unfocused(Route route) => route.Focus is null ? route : route with { Focus = null };

    private static FocusFilter Filter(FocusKind kind, string? detail) => kind switch
    {
        // "Log 93% full (Sales)": the fullest log's database. Without one, every database.
        FocusKind.Database => AlertText.LogDatabase(detail) is { } name ? new FocusFilter(kind, Name: name) : FocusFilter.None,
        // The head isn't known until the page reads the sessions: the chain waiting longest.
        _ => new FocusFilter(kind),
    };

    private static string Describe(FocusFilter filter) => filter.Kind switch
    {
        FocusKind.Session  => $"session {filter.Id}",
        FocusKind.Query    => $"query {filter.Id}",
        FocusKind.Database => filter.Name ?? "a database",
        _                  => filter.Kind.ToString(),
    };
}

/// <summary>The words of a focused page's banner (#145).</summary>
/// <param name="Title">"Opened from alert", "Opened from health check", "Opened from Processes".</param>
/// <param name="What">"CPU · critical · 13:58–14:05", with what the page is showing: "— showing … only".</param>
/// <param name="Past">For a page that shows the server now, opened for an alert that has ended: says so.</param>
/// <param name="HistoryLink">"Open Live charts for 13:43–14:20", when the history page can show it.</param>
public sealed record FocusBannerText(string Title, string What, string? Past, string? HistoryLink);

public static class FocusBanner
{
    /// <param name="note">What the page shows of its rows now, e.g. "showing the blocking chain of session 87 only".</param>
    /// <param name="pageShowsWindow">The page shows the focus's window itself (a history page).</param>
    /// <param name="historyName">The history page's name, e.g. "Live charts".</param>
    /// <param name="today">This PC's date, so a time on another day shows its date.</param>
    public static FocusBannerText For(PageFocus focus, string? note, bool pageShowsWindow, string? historyName, DateTime today)
    {
        var origin = focus.Origin;
        var title = origin.Source switch
        {
            FocusSource.Alert       => "Opened from alert",
            FocusSource.HealthCheck => "Opened from health check",
            _                       => $"Opened from {origin.Page}",
        };

        var what = origin.Source == FocusSource.Alert && origin.StartedUtc is { } started
            ? origin.EndedUtc is { } ended
                ? $"{origin.What} · {Clock(started, today)}–{Clock(ended, today)}"
                : $"{origin.What} · since {Clock(started, today)}"
            : origin.What;
        if (!string.IsNullOrWhiteSpace(note))
            what += $" — {note}";

        string? past = null, link = null;
        if (origin is { IsPast: true, EndedUtc: { } end } && !pageShowsWindow)
        {
            past = $"It ended at {Clock(end, today)}; this page shows the server now.";
            if (focus is { HistoryTag: not null, FromUtc: { } from } && historyName is not null)
                link = $"Open {historyName} for {Clock(from, today)}–{Clock(focus.ToUtc ?? end, today)}";
            else
                past += " SqlVitals keeps no history of this check.";
        }
        return new FocusBannerText(title, what, past, link);
    }

    // "13:58" today, "30 Sep 13:58" on another day, on this PC's clock.
    private static string Clock(DateTime utc, DateTime today)
    {
        var local = utc.ToLocalTime();
        return local.Date == today.Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}
