namespace SqlVitals.Desktop.Services;

/// <summary>A page in the sidebar: the tag <c>MainWindow.NavigateTo</c> opens it by, and its button.</summary>
/// <param name="ToolTip">Null for a page whose name says enough.</param>
public sealed record NavPage(string Tag, string Icon, string Label, string? ToolTip = null)
{
    /// <summary>The button's text, e.g. "🔗  Processes".</summary>
    public string Content => $"{Icon}  {Label}";
}

/// <summary>A collapsible section of the sidebar (#124).</summary>
/// <param name="Id">Saved in the settings file, so it must never change; the name may.</param>
/// <param name="Question">The header's tooltip: what the pages in the group answer.</param>
public sealed record NavGroup(string Id, string Name, string Question, IReadOnlyList<NavPage> Pages);

/// <summary>
/// The sidebar (#124): its pages, grouped by the question a DBA is asking, in the order they're
/// shown. MainWindow builds the sidebar from this, so a page added here gets its button, and a
/// page that isn't here has none.
/// </summary>
public static class NavMenu
{
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        new("Overview", "Overview", "Is the server healthy, and has anything gone wrong?",
        [
            new("LiveMetrics", "💓", "Live Metrics"),
            new("Alerts",      "🔔", "Alerts", AlertsToolTip),
        ]),
        new("Activity", "Activity now", "Who is connected, what is running, and who is blocking whom?",
        [
            new("Processes",      "🔗", "Processes"),
            new("ActiveWaits",    "⚡", "Active Waits"),
            new("Deadlocks",      "💥", "Deadlocks",
                "Recent deadlocks from the system_health session: victim, participants, objects and statements"),
            new("AppConnections", "🔌", "App Connections"),
        ]),
        new("Waits", "Waits", "What is the server waiting on, and how has that changed over time?",
        [
            new("TopWaits",  "📊", "Top Waits"),
            new("WaitTrend", "📈", "Wait Trend"),
        ]),
        new("Queries", "Queries", "Which queries cost the most, which got slower, and why?",
        [
            new("ResourceQueries",  "🔥", "Resource Queries"),
            new("QueryStore",       "📋", "Query Store"),
            new("QueryRegressions", "🐢", "Query Regressions"),
            new("PlanCacheHealth",  "🔎", "Plan Cache Health"),
            new("ImplicitConv",     "⚠️", "Implicit Conv."),
            new("SpTrace",          "🛰️", "SP Trace"),
        ]),
        new("Resources", "Resources", "Is memory, TempDB, disk or storage the bottleneck?",
        [
            new("Memory",    "🧠", "Memory Grants"),
            new("TempDb",    "🗄️", "TempDB"),
            new("FileIo",    "💽", "File I/O",
                "Read and write latency per database file, measured over the last few seconds; files slower than your threshold highlighted"),
            new("DbStorage", "📦", "DB Storage"),
            new("Perfmon",   "⏱️", "Perfmon"),
        ]),
        new("Maintenance", "Maintenance", "Are indexes, statistics, backups and jobs looked after?",
        [
            new("IndexHealth", "🔍", "Index Health"),
            new("StaleStats",  "📉", "Stale Stats"),
            new("Backups",     "💾", "Backups",
                "Each database's last full, differential and log backup from msdb, with a warning when one is older than your RPO"),
            new("AgentJobs",   "🕒", "Agent Jobs",
                "SQL Agent jobs from msdb: last run, duration against the average, next run; failed and long-running jobs first"),
        ]),
        new("Configuration", "Configuration", "Is the server set up correctly and securely?",
        [
            new("ConfigChecks",  "✅", "Config Checks",
                "MAXDOP, cost threshold, max server memory, TempDB files, auto-shrink, page verify and compatibility level against best practice"),
            new("SecurityAudit", "🔒", "Security Audit",
                "Who is sysadmin, whether sa is enabled, databases marked TRUSTWORTHY and orphaned users, each with the risk explained"),
        ]),
    ];

    /// <summary>The pages below the groups, each after a separator, as before the groups existed.</summary>
    public static readonly IReadOnlyList<NavPage> Footer =
    [
        new("Export",   "📤", "Export / AI"),
        new("Settings", "⚙️", "Settings"),
    ];

    /// <summary>The Alerts button's tooltip while no alert is active.</summary>
    public const string AlertsToolTip = "Health thresholds breached, now and in the past, for every monitored connection";

    /// <summary>Every page in the sidebar, in the order shown.</summary>
    public static IEnumerable<NavPage> AllPages => Groups.SelectMany(g => g.Pages).Concat(Footer);

    /// <summary>The group a page is in; null for a footer page or an unknown tag.</summary>
    public static NavGroup? GroupOf(string tag) =>
        Groups.FirstOrDefault(g => g.Pages.Any(p => p.Tag == tag));

    /// <summary>
    /// Saved collapsed groups, cleaned up: known ids only (a group may have been renamed or
    /// removed since), each once, in sidebar order. The ids are matched without regard to case.
    /// </summary>
    public static IReadOnlyList<string> NormalizeCollapsed(IEnumerable<string>? ids)
    {
        var saved = new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase);
        return Groups.Where(g => saved.Contains(g.Id)).Select(g => g.Id).ToList();
    }
}
