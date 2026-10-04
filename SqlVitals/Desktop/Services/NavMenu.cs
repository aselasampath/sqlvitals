namespace SqlVitals.Desktop.Services;

/// <summary>A page in the sidebar: the tag <c>MainWindow.NavigateTo</c> opens it by, and its button.</summary>
/// <param name="Icon">A Segoe Fluent Icons glyph (<see cref="NavMenu.IconFont"/>).</param>
/// <param name="ToolTip">Null for a page whose name says enough.</param>
public sealed record NavPage(string Tag, string Icon, string Label, string? ToolTip = null);

/// <summary>A group of a server's pages in the sidebar (#124, #141).</summary>
/// <param name="Id">Stable: tests and links name groups by it; the name may change.</param>
/// <param name="Question">The header's tooltip: what the pages in the group answer.</param>
public sealed record NavGroup(string Id, string Name, string Icon, string Question, IReadOnlyList<NavPage> Pages);

/// <summary>
/// The sidebar (#141): a global part (every server at a glance, alerts, export), then the server
/// part, the open server's pages grouped by the question a DBA is asking, then Settings at the
/// foot. MainWindow builds the sidebar from this, so a page added here gets its button, and a page
/// that isn't here has none.
/// </summary>
public static class NavMenu
{
    /// <summary>
    /// The icons' font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10, which
    /// has the same glyphs at the same code points.
    /// </summary>
    public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>The pages about every server, or not about one: above the server part.</summary>
    public static readonly IReadOnlyList<NavPage> Global =
    [
        new("Home",   "\uE80F", "All servers", "Every saved connection as a tile with its health score, the ones that need attention first"),
        new("Alerts", "\uEA8F", "Alerts", AlertsToolTip),
        new("Export", "\uEDE1", "Export / AI", "A report of the open server, to save or to paste into an AI chat"),
    ];

    /// <summary>The open server's pages, in groups. One group is open at a time.</summary>
    public static readonly IReadOnlyList<NavGroup> Groups =
    [
        // One Overview per server (#143): is it OK, and if not, why? Live charts has the full-size charts.
        new("Overview", "Overview", "\uE80A", "Is the server healthy, and if not, why?",
        [
            new("Overview",    "\uE95E", "Overview",
                "The server's active alerts, what is costing its health score points, its headline numbers and charts, " +
                "and what happened since you last looked"),
            new("LiveMetrics", "\uEC4A", "Live charts",
                "Full-size live charts: waits, CPU, throughput, physical I/O, memory and the process map"),
        ]),
        new("Activity", "Activity now", "\uE9D9", "Who is connected, what is running, and who is blocking whom?",
        [
            new("Processes",      "\uE716", "Processes"),
            new("ActiveWaits",    "\uE945", "Active Waits"),
            new("Deadlocks",      "\uE72E", "Deadlocks",
                "Recent deadlocks from the system_health session: victim, participants, objects and statements"),
            new("AppConnections", "\uE703", "App Connections"),
        ]),
        new("Waits", "Waits", "\uE916", "What is the server waiting on, and how has that changed over time?",
        [
            new("TopWaits",  "\uE8CB", "Top Waits"),
            new("WaitTrend", "\uE9D2", "Wait Trend"),
        ]),
        new("Queries", "Queries", "\uE9F5", "Which queries cost the most, which got slower, and why?",
        [
            new("ResourceQueries",  "\uECAD", "Resource Queries"),
            new("QueryStore",       "\uE8F1", "Query Store"),
            new("QueryRegressions", "\uE74B", "Query Regressions"),
            new("PlanCacheHealth",  "\uE81E", "Plan Cache Health"),
            new("ImplicitConv",     "\uE7BA", "Implicit Conv."),
            new("SpTrace",          "\uE7B3", "SP Trace"),
        ]),
        new("Resources", "Resources", "\uEB05", "Is memory, TempDB, disk or storage the bottleneck?",
        [
            new("Memory",    "\uE964", "Memory Grants"),
            new("TempDb",    "\uEE94", "TempDB"),
            new("FileIo",    "\uEDA2", "File I/O",
                "Read and write latency per database file, measured over the last few seconds; files slower than your threshold highlighted"),
            new("DbStorage", "\uE8B7", "DB Storage"),
            new("Perfmon",   "\uE9F9", "Perfmon"),
        ]),
        new("Maintenance", "Maintenance", "\uE90F", "Are indexes, statistics, backups and jobs looked after?",
        [
            new("IndexHealth", "\uE8FD", "Index Health"),
            new("StaleStats",  "\uE81C", "Stale Stats"),
            new("Backups",     "\uE74E", "Backups",
                "Each database's last full, differential and log backup from msdb, with a warning when one is older than your RPO"),
            new("AgentJobs",   "\uE823", "Agent Jobs",
                "SQL Agent jobs from msdb: last run, duration against the average, next run; failed and long-running jobs first"),
        ]),
        new("Configuration", "Configuration", "\uE9E9", "Is the server set up correctly and securely?",
        [
            new("ConfigChecks",  "\uE9D5", "Config Checks",
                "MAXDOP, cost threshold, max server memory, TempDB files, auto-shrink, page verify and compatibility level against best practice"),
            new("SecurityAudit", "\uE8D7", "Security Audit",
                "Who is sysadmin, whether sa is enabled, databases marked TRUSTWORTHY and orphaned users, each with the risk explained"),
        ]),
    ];

    /// <summary>The pages at the foot of the sidebar.</summary>
    public static readonly IReadOnlyList<NavPage> Footer =
    [
        new("Settings", "\uE713", "Settings"),
    ];

    /// <summary>The Alerts button's tooltip while no alert is active.</summary>
    public const string AlertsToolTip = "Health thresholds breached, now and in the past, for every monitored connection";

    /// <summary>Every page in the sidebar, in the order shown.</summary>
    public static IEnumerable<NavPage> AllPages => Global.Concat(Groups.SelectMany(g => g.Pages)).Concat(Footer);

    /// <summary>The group a page is in; null for a global or footer page, or an unknown tag.</summary>
    public static NavGroup? GroupOf(string tag) =>
        Groups.FirstOrDefault(g => g.Pages.Any(p => p.Tag == tag));

    /// <summary>A page of the open server's, in the server part.</summary>
    public static bool IsServerPage(string tag) => GroupOf(tag) is not null;
}
