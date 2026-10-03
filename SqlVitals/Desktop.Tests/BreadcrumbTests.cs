using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>The top bar's breadcrumb (#140): what each part says and where it goes.</summary>
public sealed class BreadcrumbTests
{
    private static readonly Guid Prod = Guid.NewGuid();
    private static readonly Guid Gone = Guid.NewGuid();

    private static string? Name(Guid id) => id == Prod ? "PROD-SQL01" : null;

    private static IReadOnlyList<Crumb> For(Route route) => Breadcrumb.For(route, Name);

    [Fact]
    public void AServersPage_ShowsAllServers_TheServer_ItsGroup_AndThePage()
    {
        var crumbs = For(new Route("Deadlocks", ConnectionId: Prod));

        Assert.Equal(["All servers", "PROD-SQL01", "Activity now", "Deadlocks"], crumbs.Select(c => c.Text));
        Assert.Equal(new Route("Home"), crumbs[0].Target);
        // The server part opens the server menu (#141), not a page.
        Assert.Null(crumbs[1].Target);
        Assert.Equal(Prod, crumbs[1].ServerId);
        // The group opens its first page, on the same server.
        Assert.Equal(new Route("Processes", ConnectionId: Prod), crumbs[2].Target);
        Assert.Null(crumbs[3].Target);

        // Short of room, All servers and the group go; the server and the page stay.
        Assert.Equal([true, false, true, false], crumbs.Select(c => c.CanDrop));
    }

    [Fact]
    public void TheFirstPageOfAGroup_DoesNotLinkTheGroupToItself()
    {
        var crumbs = For(new Route("Processes", ConnectionId: Prod));
        Assert.Equal(["All servers", "PROD-SQL01", "Activity now", "Processes"], crumbs.Select(c => c.Text));
        Assert.Null(crumbs[2].Target);
    }

    [Fact]
    public void Home_IsAllServers()
    {
        var crumb = Assert.Single(For(new Route("Home")));
        Assert.Equal("All servers", crumb.Text);
        Assert.Null(crumb.Target);
        Assert.False(crumb.CanDrop);
    }

    [Fact]
    public void AServersSummary_IsItsOverview()
    {
        var crumbs = For(new Route("ServerDetail", ServerId: Prod));
        Assert.Equal(["All servers", "PROD-SQL01", "Overview", "Summary"], crumbs.Select(c => c.Text));
        Assert.Equal(Prod, crumbs[1].ServerId);
        Assert.Null(crumbs[2].Target);   // Summary is the group's first page

        // From Live Metrics, Overview opens the server's Summary.
        var live = For(new Route("LiveMetrics", ConnectionId: Prod));
        Assert.Equal(["All servers", "PROD-SQL01", "Overview", "Live Metrics"], live.Select(c => c.Text));
        Assert.Equal(new Route("ServerDetail", ServerId: Prod), live[2].Target);
    }

    [Fact]
    public void Alerts_AreAboutEveryServer_OrTheOneTheyAreFilteredTo()
    {
        Assert.Equal(["All servers", "Alerts"], For(new Route("Alerts")).Select(c => c.Text));
        Assert.Equal(["All servers", "PROD-SQL01", "Alerts"], For(new Route("Alerts", AlertsConnectionId: Prod)).Select(c => c.Text));
    }

    [Fact]
    public void Settings_AndAPageOutsideTheGroups_HaveNoGroup()
    {
        Assert.Equal(["Settings"], For(new Route("Settings")).Select(c => c.Text));
        Assert.Equal(["All servers", "PROD-SQL01", "Export / AI"], For(new Route("Export", ConnectionId: Prod)).Select(c => c.Text));
    }

    [Fact]
    public void AServerThatHasGone_OrAConnectionFromAppSettings_IsLeftOut()
    {
        Assert.Equal(["All servers", "Activity now", "Processes"], For(new Route("Processes", ConnectionId: Gone)).Select(c => c.Text));
        Assert.Equal(["All servers", "Activity now", "Processes"], For(new Route("Processes")).Select(c => c.Text));
        Assert.Equal(["All servers", "Overview", "Summary"], For(new Route("ServerDetail", ServerId: Gone)).Select(c => c.Text));
    }

    [Fact]
    public void EveryPageInTheMenu_EndsWithItsLabel_HomeBeingAllServers() =>
        Assert.All(NavMenu.AllPages.Where(p => p.Tag != "Home"), page =>
            Assert.Equal(page.Label, For(new Route(page.Tag, ConnectionId: Route.IsGlobal(page.Tag) ? null : Prod))[^1].Text));
}
