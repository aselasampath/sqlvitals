namespace SqlVitals.Desktop.Services;

/// <summary>One part of the top bar's breadcrumb (#140).</summary>
/// <param name="Target">Where clicking it goes; null for the page on screen.</param>
/// <param name="ServerId">Set on a server's part, which shows its health dot and score.</param>
/// <param name="CanDrop">A part the bar may leave out when it is short of room.</param>
public sealed record Crumb(string Text, Route? Target, Guid? ServerId = null, bool CanDrop = false);

/// <summary>
/// The top bar's breadcrumb (#140): <c>All servers › PROD-SQL01 › Activity now › Processes</c>.
/// Every part but the last opens its page. Pure: MainWindow passes the connections' names.
/// </summary>
public static class Breadcrumb
{
    public const string AllServers = "All servers";

    /// <param name="serverName">A saved connection's name; null for one that has gone.</param>
    public static IReadOnlyList<Crumb> For(Route route, Func<Guid, string?> serverName)
    {
        var home = new Crumb(AllServers, new Route("Home"), CanDrop: true);
        var page = NavMenu.AllPages.FirstOrDefault(p => p.Tag == route.Tag)?.Label ?? route.Tag;

        Crumb? Server(Guid? id, bool current = false) =>
            id is { } value && serverName(value) is { } name
                ? new Crumb(name, current ? null : new Route("ServerDetail", ServerId: value), value)
                : null;

        return route.Tag switch
        {
            "Home"         => [home with { Target = null, CanDrop = false }],
            "ServerDetail" => Parts(home, Server(route.ServerId, current: true) ?? new Crumb("Server", null)),
            "Settings"     => [new Crumb(page, null)],
            // About every server, or filtered to one from a notification.
            "Alerts"       => Parts(home, Server(route.AlertsConnectionId), new Crumb(page, null)),
            _              => Parts(home, Server(route.ConnectionId), Group(route), new Crumb(page, null)),
        };
    }

    // The page's group, opening its first page; none for a page outside the groups.
    private static Crumb? Group(Route route)
    {
        if (NavMenu.GroupOf(route.Tag) is not { } group)
            return null;
        var first = group.Pages[0].Tag;
        return new Crumb(group.Name, first == route.Tag ? null : route with { Tag = first }, CanDrop: true);
    }

    private static Crumb[] Parts(params Crumb?[] parts) => parts.OfType<Crumb>().ToArray();
}
