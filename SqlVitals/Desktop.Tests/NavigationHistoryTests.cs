using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>Back and Forward through the pages visited (#139), without WPF.</summary>
public sealed class NavigationHistoryTests
{
    private static readonly Guid ServerA = Guid.NewGuid();
    private static readonly Guid ServerB = Guid.NewGuid();

    private static Route Page(string tag, Guid? connection = null) => new(tag, connection ?? ServerA);
    private static bool Any(Route _) => true;

    [Fact]
    public void Back_ThenForward_ReturnsToEachPage()
    {
        var history = new NavigationHistory();
        history.Visit(Page("TopWaits"));
        history.Visit(Page("Processes"));

        var back = history.PeekBack(Any);
        Assert.Equal(Page("TopWaits"), back);
        history.CommitBack(back!, back!);
        Assert.Equal(Page("TopWaits"), history.Current);

        var forward = history.PeekForward(Any);
        Assert.Equal(Page("Processes"), forward);
        history.CommitForward(forward!, forward!);
        Assert.Equal(Page("Processes"), history.Current);
        Assert.Null(history.PeekForward(Any));
    }

    [Fact]
    public void Nothing_ToGoBackTo_BeforeASecondPage()
    {
        var history = new NavigationHistory();
        Assert.Null(history.PeekBack(Any));
        history.Visit(Page("TopWaits"));
        Assert.Null(history.PeekBack(Any));
        Assert.Null(history.PeekForward(Any));
    }

    [Fact]
    public void AVisit_AfterGoingBack_DropsForward_AsInABrowser()
    {
        var history = new NavigationHistory();
        history.Visit(Page("TopWaits"));
        history.Visit(Page("Processes"));
        var back = history.PeekBack(Any)!;
        history.CommitBack(back, back);

        history.Visit(Page("Backups"));

        Assert.Null(history.PeekForward(Any));
        Assert.Equal(Page("TopWaits"), history.PeekBack(Any));
    }

    [Fact]
    public void TheSamePlace_ReplacesTheCurrentEntry()
    {
        // Settings on another category, or Alerts opened again from a notification.
        var history = new NavigationHistory();
        history.Visit(Page("TopWaits"));
        history.Visit(new Route("Settings"));
        history.Visit(new Route("Settings", Settings: new SettingsLocation(SettingsCategory.Appearance)));

        Assert.Equal(SettingsCategory.Appearance, history.Current!.Settings!.Category);
        Assert.Equal(Page("TopWaits"), history.PeekBack(Any));
        var back = history.PeekBack(Any)!;
        history.CommitBack(back, back);
        Assert.Null(history.PeekBack(Any));
    }

    [Fact]
    public void TheSamePage_OnAnotherServer_IsANewEntry()
    {
        var history = new NavigationHistory();
        history.Visit(Page("Processes", ServerA));
        history.Visit(Page("Processes", ServerB));

        Assert.Equal(Page("Processes", ServerA), history.PeekBack(Any));
    }

    [Fact]
    public void Back_SkipsRoutes_NoLongerAvailable_AndDropsThem()
    {
        // ServerB was removed since its pages were visited.
        var history = new NavigationHistory();
        history.Visit(Page("TopWaits", ServerA));
        history.Visit(Page("Processes", ServerB));
        history.Visit(Page("Backups", ServerB));
        history.Visit(Page("Deadlocks", ServerA));
        bool stillThere(Route r) => r.ConnectionId != ServerB;

        var back = history.PeekBack(stillThere);
        Assert.Equal(Page("TopWaits", ServerA), back);
        history.CommitBack(back!, back!);

        // The removed server's pages are gone from Back, even if it came back.
        Assert.Null(history.PeekBack(Any));
        Assert.Equal(Page("Deadlocks", ServerA), history.PeekForward(Any));
    }

    [Fact]
    public void AMove_ThatLedSomewhereElse_RecordsWhereItLed()
    {
        // Back to a server whose password isn't saved opens Settings to ask for it.
        var history = new NavigationHistory();
        history.Visit(Page("Processes", ServerB));
        history.Visit(Page("TopWaits", ServerA));

        var back = history.PeekBack(Any)!;
        history.CommitBack(back, Page("LiveMetrics", ServerB));

        Assert.Equal(Page("LiveMetrics", ServerB), history.Current);
        Assert.Equal(Page("TopWaits", ServerA), history.PeekForward(Any));
    }

    [Fact]
    public void ACommit_AfterTheHistoryChanged_IsAVisit()
    {
        var history = new NavigationHistory();
        history.Visit(Page("TopWaits"));
        history.Visit(Page("Processes"));

        history.CommitBack(Page("Backups"), Page("Backups"));

        Assert.Equal(Page("Backups"), history.Current);
        Assert.Equal(Page("Processes"), history.PeekBack(Any));
    }

    [Fact]
    public void History_KeepsTheLatestRoutes_UpToItsCapacity()
    {
        var history = new NavigationHistory();
        for (var i = 0; i <= NavigationHistory.Capacity + 10; i++)
            history.Visit(Page($"Page{i}"));

        var reachable = 0;
        while (history.PeekBack(Any) is { } back)
        {
            history.CommitBack(back, back);
            reachable++;
        }

        Assert.Equal(NavigationHistory.Capacity, reachable);
        Assert.Equal(Page("Page10"), history.Current);
    }

    [Fact]
    public void RouteStates_KeepTheMostRecentlySaved_UpToTheirCapacity()
    {
        var states = new RouteStates<string>();
        for (var i = 0; i < RouteStates<string>.Capacity + 5; i++)
            states.Save(Page($"Page{i}"), $"state {i}");

        // Saving again makes a route the most recent.
        states.Save(Page("Page5"), "state 5 again");
        states.Save(Page("Extra"), "extra");

        Assert.Equal(RouteStates<string>.Capacity, states.Count);
        Assert.Null(states.Get(Page("Page0")));
        Assert.Equal("state 5 again", states.Get(Page("Page5")));
        Assert.Equal("extra", states.Get(Page("Extra")));
    }

    [Fact]
    public void RouteStates_AreKeptPerRoute_IncludingWhatThePageWasOpenedWith()
    {
        // Alerts opened from a notification has its own grid filter, not the unfiltered page's.
        var states = new RouteStates<string>();
        states.Save(new Route("Alerts"), "all");
        states.Save(new Route("Alerts", AlertsConnectionId: ServerA), "one server");

        Assert.Equal("all", states.Get(new Route("Alerts")));
        Assert.Equal("one server", states.Get(new Route("Alerts", AlertsConnectionId: ServerA)));
        Assert.Null(states.Get(new Route("Alerts", AlertsConnectionId: ServerB)));
    }
}
