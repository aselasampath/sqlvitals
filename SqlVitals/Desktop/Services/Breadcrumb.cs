namespace SqlVitals.Desktop.Services;

/// <summary>One part of the top bar's breadcrumb (#140).</summary>
/// <param name="Target">Where clicking it goes; null for the page on screen, and for the server's part.</param>
/// <param name="ServerId">Set on a server's part, which shows its health dot and score and opens a
/// menu of the saved servers (#141).</param>
/// <param name="CanDrop">A part the bar may leave out when it is short of room.</param>
public sealed record Crumb(string Text, Route? Target, Guid? ServerId = null, bool CanDrop = false);

/// <summary>
/// The top bar's breadcrumb (#140): <c>All servers › PROD-SQL01 › Activity now › Processes</c>.
/// Every part but the last opens its page, but the server's, which switches server (#141). Pure:
/// MainWindow passes the connections' names.
/// </summary>
public static class Breadcrumb
{
    public const string AllServers = "All servers";

    /// <param name="serverName">A saved connection's name; null for one that has gone.</param>
    public static IReadOnlyList<Crumb> For(Route route, Func<Guid, string?> serverName)
    {
        var home = new Crumb(AllServers, new Route("Home"), CanDrop: true);
        var page = NavMenu.AllPages.FirstOrDefault(p => p.Tag == route.Tag)?.Label ?? route.Tag;

        Crumb? Server(Guid? id) =>
            id is { } value && serverName(value) is { } name ? new Crumb(name, null, value) : null;

        return route.Tag switch
        {
            "Home"     => [home with { Target = null, CanDrop = false }],
            "Settings" => [new Crumb(page, null)],
            // About every server, or filtered to one from a notification.
            "Alerts"   => Parts(home, Server(route.AlertsConnectionId), new Crumb(page, null)),
            _          => Parts(home, Server(Workspace.ServerOf(route)), Group(route), new Crumb(page, null)),
        };
    }

    // The page's group, opening its first page on the same server; none for a page outside the
    // groups, or for a page named as its group (the Overview), which would only say it twice.
    private static Crumb? Group(Route route)
    {
        if (NavMenu.GroupOf(route.Tag) is not { } group || group.Pages.Any(p => p.Tag == route.Tag && p.Label == group.Name))
            return null;
        var first  = group.Pages[0].Tag;
        var server = Workspace.ServerOf(route);
        Route? target = first == route.Tag ? null
                      : first == "Overview" ? (server is { } id ? Workspace.Overview(id) : null)
                      : new Route(first, ConnectionId: server);
        return new Crumb(group.Name, target, CanDrop: true);
    }

    /// <summary>
    /// A route in a few words, for Back and Forward's tooltips and the quick switcher's recent routes
    /// (#144): "Processes · PROD-SQL01", "Overview · PROD-SQL01", "Alerts" for a page about every server.
    /// </summary>
    /// <param name="serverName">A saved connection's name; null for one that has gone.</param>
    public static string Describe(Route route, Func<Guid, string?> serverName)
    {
        string? Name(Guid? id) => id is { } value ? serverName(value) : null;

        if (route.Tag == "Overview")
            return Name(route.ServerId) is { } name ? $"Overview · {name}" : "Overview";

        var label = NavMenu.AllPages.FirstOrDefault(p => p.Tag == route.Tag)?.Label ?? route.Tag;
        return Name(route.ConnectionId ?? route.AlertsConnectionId) is { } server ? $"{label} · {server}" : label;
    }

    private static Crumb[] Parts(params Crumb?[] parts) => parts.OfType<Crumb>().ToArray();
}
