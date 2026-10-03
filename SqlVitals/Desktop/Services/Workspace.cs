namespace SqlVitals.Desktop.Services;

/// <summary>
/// The server workspace (#141): which server a route is inside, and the same place on another
/// server. Pure; MainWindow does the navigating.
/// </summary>
public static class Workspace
{
    /// <summary>
    /// The server whose pages the sidebar shows for a route: a server page's connection, or the
    /// server a summary is about. Null on a page about every server (Home, Alerts, Settings).
    /// </summary>
    public static Guid? ServerOf(Route route) =>
        route.Tag == "ServerDetail" ? route.ServerId
        : Route.IsGlobal(route.Tag) ? null
        : route.ConnectionId;

    /// <summary>
    /// The same page on another server, as picked from the breadcrumb's server menu: a server's
    /// page, its summary, or Alerts filtered to it. From a page about every server, the server's
    /// summary.
    /// </summary>
    public static Route OnServer(Route route, Guid server) => route.Tag switch
    {
        "ServerDetail"                => route with { ServerId = server },
        "Alerts"                      => route with { AlertsConnectionId = server },
        var tag when Route.IsGlobal(tag) => Overview(server),
        _                             => route with { ConnectionId = server },
    };

    /// <summary>
    /// The route itself, or the server's Overview when the server doesn't have the page (Azure SQL
    /// Database has no Agent, backups in msdb, …). Without a saved connection to show, Live Metrics.
    /// </summary>
    public static Route Available(Route route, Func<string, bool> isDisabled, Guid? server) =>
        !isDisabled(route.Tag) ? route
        : server is { } id ? Overview(id)
        : new Route("LiveMetrics");

    /// <summary>A server's Overview: its summary until #143 merges it with Live Metrics.</summary>
    public static Route Overview(Guid server) => new("ServerDetail", ServerId: server);
}
