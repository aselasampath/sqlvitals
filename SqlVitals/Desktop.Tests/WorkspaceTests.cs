using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The server workspace (#141): which server a page is inside, and switching server from the
/// breadcrumb's menu, which keeps the page or falls back to the server's Overview.
/// </summary>
public sealed class WorkspaceTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Fact]
    public void APageIsInsideTheServerItQueries_OrTheOneItSummarises()
    {
        Assert.Equal(A, Workspace.ServerOf(new Route("Processes", ConnectionId: A)));
        Assert.Equal(A, Workspace.ServerOf(new Route("Export", ConnectionId: A)));
        Assert.Equal(A, Workspace.ServerOf(new Route("Overview", ServerId: A)));

        // Pages about every server have no workspace: the sidebar says how to open one.
        Assert.Null(Workspace.ServerOf(new Route("Home")));
        Assert.Null(Workspace.ServerOf(new Route("Settings")));
        Assert.Null(Workspace.ServerOf(new Route("Alerts")));
        Assert.Null(Workspace.ServerOf(new Route("Alerts", AlertsConnectionId: A)));
    }

    [Fact]
    public void SwitchingServer_KeepsThePage()
    {
        Assert.Equal(new Route("Processes", ConnectionId: B), Workspace.OnServer(new Route("Processes", ConnectionId: A), B));
        Assert.Equal(new Route("Overview", ServerId: B), Workspace.OnServer(new Route("Overview", ServerId: A), B));
        Assert.Equal(new Route("Alerts", AlertsConnectionId: B), Workspace.OnServer(new Route("Alerts", AlertsConnectionId: A), B));
        // From a page about every server, the server's Overview.
        Assert.Equal(Workspace.Overview(B), Workspace.OnServer(new Route("Home"), B));
    }

    [Fact]
    public void APageTheServerLacks_FallsBackToItsOverview()
    {
        static bool AzureLacks(string tag) => tag is "Backups" or "AgentJobs" or "TempDb" or "SecurityAudit";

        var backups = Workspace.OnServer(new Route("Backups", ConnectionId: A), B);
        Assert.Equal(new Route("Overview", ServerId: B), Workspace.Available(backups, AzureLacks, B));

        var processes = new Route("Processes", ConnectionId: B);
        Assert.Same(processes, Workspace.Available(processes, AzureLacks, B));

        // A connection from appsettings.json only has no saved id, and no Overview: Live charts.
        Assert.Equal(new Route("LiveMetrics"), Workspace.Available(new Route("Backups"), AzureLacks, null));
    }

    [Fact]
    public void TheOverview_IsAPageAboutTheServerByItsId() =>
        Assert.Equal(new Route("Overview", ServerId: A), Workspace.Overview(A));
}
