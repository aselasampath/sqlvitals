using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The Ctrl+K quick switcher's search (#144): matching by name and by tooltip words without regard
/// to case, ranking (exact, prefix, a word's start, anywhere, tooltip; recent routes first on a tie),
/// the groups, and the recent routes shown before anything is typed.
/// </summary>
public sealed class QuickSwitcherTests
{
    private static readonly SwitcherServer Prod01    = new(Guid.NewGuid(), "PROD-SQL01", "prod-sql01.corp.local");
    private static readonly SwitcherServer Prod02    = new(Guid.NewGuid(), "PROD-SQL02", "prod-sql02.corp.local");
    private static readonly SwitcherServer Reporting = new(Guid.NewGuid(), "REPORTING", "rpt.corp.local");
    private static readonly SwitcherServer[] Servers = [Prod01, Prod02, Reporting];

    // On PROD-SQL01's Overview, its workspace open.
    private static SwitcherScope OnProd01(params Route[] recent) =>
        new(Workspace.Overview(Prod01.Id), Prod01.Id, Servers, recent);

    private static IReadOnlyList<SwitcherSection> Search(string query, SwitcherScope? scope = null) =>
        QuickSwitcher.Search(query, scope ?? OnProd01());

    private static SwitcherSection Section(IReadOnlyList<SwitcherSection> sections, SwitcherGroup group) =>
        Assert.Single(sections, s => s.Group == group);

    private static SwitcherItem First(IReadOnlyList<SwitcherSection> sections) => sections[0].Items[0];

    // ── Matching ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("tempdb", "TempDB", QuickSwitcher.Exact, 0, 6)]
    [InlineData("TEMP", "TempDB", QuickSwitcher.Prefix, 0, 4)]
    [InlineData("stats", "Stale Stats", QuickSwitcher.WordPrefix, 6, 5)]
    [InlineData("conv", "Implicit Conv.", QuickSwitcher.WordPrefix, 9, 4)]
    [InlineData("db", "TempDB", QuickSwitcher.Contains, 4, 2)]
    public void AName_MatchesWhole_AtItsStart_AtAWordsStart_OrAnywhere_WithoutRegardToCase(string query, string name, int score, int start, int length) =>
        Assert.Equal((score, start, length), QuickSwitcher.Match(query, name, words: null));

    [Fact]
    public void TheWordsOfATooltip_Match_WhenEveryWordTypedStartsOneOfThem()
    {
        const string tip = "Who is connected and what each session is running, with blocking chains drawn as trees";
        Assert.Equal(QuickSwitcher.Words, QuickSwitcher.Match("blocking", "Processes", tip).Score);
        Assert.Equal(QuickSwitcher.Words, QuickSwitcher.Match("BLOCK chain", "Processes", tip).Score);
        // Inside a word isn't a word: "lock" doesn't find blocking.
        Assert.Equal(0, QuickSwitcher.Match("lock", "Processes", tip).Score);
        Assert.Equal(0, QuickSwitcher.Match("blocking deadlock", "Processes", tip).Score);
        Assert.Equal(0, QuickSwitcher.Match("x", "Processes", null).Score);
    }

    [Fact]
    public void WhatIsTyped_IsTrimmed_WithEachRunOfSpacesOne()
    {
        Assert.Equal("query store", QuickSwitcher.Normalise("  query \t  store "));
        var first = First(Search("  query   STORE "));
        Assert.Equal("Query Store", first.Text);
        Assert.Equal((0, 11), (first.MatchStart, first.MatchLength));
    }

    [Fact]
    public void Blocking_FindsProcesses_ByItsTooltip()
    {
        var results = Search("blocking");
        var on = Section(results, SwitcherGroup.OnServer);
        Assert.Equal("On PROD-SQL01", on.Title);
        Assert.Equal("Processes", on.Items[0].Text);
        Assert.Equal(new Route("Processes", ConnectionId: Prod01.Id), on.Items[0].Route);
        Assert.Equal("Activity now", on.Items[0].Detail);
        // Found by the tooltip, so nothing in the name is in bold.
        Assert.Equal(-1, on.Items[0].MatchStart);
        Assert.Same(on, results[0]);
    }

    // ── Ranking ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnExactName_ComesBeforeAPrefix_BeforeAWordsStart_BeforeAnywhere_BeforeATooltip()
    {
        var on = Section(Search("temp"), SwitcherGroup.OnServer);
        Assert.Equal("TempDB", on.Items[0].Text);
        Assert.Equal((0, 4), (on.Items[0].MatchStart, on.Items[0].MatchLength));

        // "waits": Active Waits and Top Waits by a word's start, in the menu's order, before Wait Trend by its group's name.
        var waits = Section(Search("waits"), SwitcherGroup.OnServer).Items.Select(i => i.Text).ToList();
        Assert.Equal(["Active Waits", "Top Waits"], waits.Take(2));
        Assert.Contains("Wait Trend", waits);
        Assert.True(waits.IndexOf("Wait Trend") > 1);

        // The whole name beats its start: "Overview" is Overview, then Live charts, in the Overview group.
        var overview = Section(Search("overview"), SwitcherGroup.OnServer).Items;
        Assert.Equal("Overview", overview[0].Text);
        Assert.Equal(Workspace.Overview(Prod01.Id), overview[0].Route);
    }

    [Fact]
    public void OnATie_TheRouteOpenedMostRecently_ComesFirst_ThenTheMenusOrder()
    {
        // "waits" starts a word of both: Active Waits before Top Waits in the menu…
        Assert.Equal("Active Waits", Section(Search("waits"), SwitcherGroup.OnServer).Items[0].Text);

        // …unless Top Waits was opened more recently.
        var recent = OnProd01(new Route("TopWaits", ConnectionId: Prod01.Id), new Route("ActiveWaits", ConnectionId: Prod01.Id));
        Assert.Equal("Top Waits", Section(Search("waits", recent), SwitcherGroup.OnServer).Items[0].Text);

        // A better match still wins over a recent one.
        var recentTop = OnProd01(new Route("TopWaits", ConnectionId: Prod01.Id));
        Assert.Equal("Active Waits", First(Search("active", recentTop)).Text);
    }

    [Fact]
    public void TheGroupWithTheBestMatch_ComesFirst_SoItsFirstResultIsTheOneSelected()
    {
        // A server's exact name beats any page's tooltip.
        var results = Search("reporting");
        Assert.Equal(SwitcherGroup.OtherServers, results[0].Group);
        Assert.Equal("Overview on REPORTING", First(results).Text);

        // "Health thresholds" is a setting's exact name.
        Assert.Equal(SwitcherGroup.Settings, Search("health thresholds")[0].Group);
    }

    // ── The groups ───────────────────────────────────────────────────────────

    [Fact]
    public void SamePageOnAnotherServer_ListsTheBestPageFound_OnEachOtherServer_WithTheirHealth()
    {
        var other = Section(Search("temp"), SwitcherGroup.OtherServers);
        Assert.Equal("Same page on another server", other.Title);
        Assert.Equal(["TempDB on PROD-SQL02", "TempDB on REPORTING"], other.Items.Select(i => i.Text));
        Assert.Equal(new Route("TempDb", ConnectionId: Prod02.Id), other.Items[0].Route);
        Assert.Equal(Prod02.Id, other.Items[0].Server);
        Assert.Equal((0, 4), (other.Items[0].MatchStart, other.Items[0].MatchLength));
    }

    [Fact]
    public void AServersName_KeepsThePageOnScreen_OnThatServer()
    {
        var scope = new SwitcherScope(new Route("Processes", ConnectionId: Prod01.Id), Prod01.Id, Servers, []);
        var results = QuickSwitcher.Search("prod-sql02", scope);

        var first = First(results);
        Assert.Equal("Processes on PROD-SQL02", first.Text);
        Assert.Equal(new Route("Processes", ConnectionId: Prod02.Id), first.Route);
        // "PROD-SQL02" in bold, after "Processes on ".
        Assert.Equal(("Processes on ".Length, 10), (first.MatchStart, first.MatchLength));

        // And the server itself, under Servers: its Overview.
        var server = Section(results, SwitcherGroup.Servers).Items.Single();
        Assert.Equal(Workspace.Overview(Prod02.Id), server.Route);
        Assert.Equal("prod-sql02.corp.local", server.Detail);
    }

    [Fact]
    public void Servers_AreFoundByName_OrByTheirServersName()
    {
        var byHost = Section(Search("rpt"), SwitcherGroup.Servers).Items.Single();
        Assert.Equal("REPORTING", byHost.Text);

        // "prod" starts both production servers, and the open one is listed too.
        Assert.Equal(["PROD-SQL01", "PROD-SQL02"], Section(Search("prod"), SwitcherGroup.Servers).Items.Select(i => i.Text));
    }

    [Fact]
    public void FromAPageAboutEveryServer_TheActiveServersPagesAreListed_AndNoSamePage()
    {
        var scope = new SwitcherScope(new Route("Home"), Prod02.Id, Servers, []);
        var results = QuickSwitcher.Search("temp", scope);
        Assert.Equal("On PROD-SQL02", Section(results, SwitcherGroup.OnServer).Title);
        Assert.DoesNotContain(results, s => s.Group == SwitcherGroup.OtherServers);

        // Without any server, only what isn't a server's.
        var none = QuickSwitcher.Search("a", new SwitcherScope(new Route("Settings"), null, [], []));
        Assert.DoesNotContain(none, s => s.Group is SwitcherGroup.OnServer or SwitcherGroup.OtherServers or SwitcherGroup.Servers);
    }

    [Fact]
    public void Pages_AreTheOnesAboutEveryServer()
    {
        var pages = Section(Search("alerts"), SwitcherGroup.Pages);
        Assert.Equal(new Route("Alerts"), pages.Items[0].Route);

        Assert.Equal(new Route("Home"), Section(Search("all servers"), SwitcherGroup.Pages).Items[0].Route);
        // Export reads the open server, as from the sidebar.
        Assert.Equal(new Route("Export", ConnectionId: Prod01.Id), Section(Search("export"), SwitcherGroup.Pages).Items[0].Route);
    }

    [Fact]
    public void Settings_AreFoundByCategory_TheOpenServersTabs_AndEachSettingsName()
    {
        var thresholds = Section(Search("thresholds"), SwitcherGroup.Settings).Items;
        // For every connection, and the open server's own.
        Assert.Contains(thresholds, i => i.Route == new Route("Settings", Settings: new SettingsLocation(SettingsCategory.Thresholds)) && i.Detail == "Settings");
        Assert.Contains(thresholds, i => i.Route == new Route("Settings", Settings: SettingsLocation.Connection(Prod01.Id, ConnectionTab.Thresholds))
                                      && i.Detail == "PROD-SQL01's connection");

        var timeout = Section(Search("command timeout"), SwitcherGroup.Settings).Items[0];
        Assert.Equal("Settings › Monitoring & history", timeout.Detail);
        Assert.Equal(new SettingsLocation(SettingsCategory.Monitoring), timeout.Route.Settings);

        // A setting on a page opens the page, on the open server.
        var rpo = Section(Search("backup rpo"), SwitcherGroup.Settings).Items[0];
        Assert.Equal(new Route("Backups", ConnectionId: Prod01.Id), rpo.Route);
        Assert.Equal("On Backups", rpo.Detail);

        // A connection's setting opens its tab.
        var encrypt = Section(Search("encrypt"), SwitcherGroup.Settings).Items[0];
        Assert.Equal("PROD-SQL01 › Connection", encrypt.Detail);
        Assert.Equal(SettingsLocation.Connection(Prod01.Id, ConnectionTab.Connection), encrypt.Route.Settings);
    }

    [Fact]
    public void APageTheServerLacks_IsListedWithItsReason_AndCantBeOpened()
    {
        const string reason = "Not available on Azure SQL Database, which has no SQL Server Agent.";
        var scope = OnProd01() with { Unavailable = (tag, server) => tag == "AgentJobs" && server == Prod01.Id ? reason : null };

        var results = QuickSwitcher.Search("agent", scope);
        var jobs = Section(results, SwitcherGroup.OnServer).Items.Single();
        Assert.False(jobs.CanOpen);
        Assert.Equal(reason, jobs.Unavailable);
        // On another server it can be: its edition isn't known until it is open.
        Assert.All(Section(results, SwitcherGroup.OtherServers).Items, i => Assert.True(i.CanOpen));
    }

    [Fact]
    public void Nothing_Matches_NoGroups()
    {
        Assert.Empty(Search("zzzzqq"));
    }

    // ── Recent routes ────────────────────────────────────────────────────────

    [Fact]
    public void NothingTyped_ShowsTheEightMostRecentRoutes_ButThePageOnScreen()
    {
        var tags = new[] { "Processes", "TopWaits", "QueryStore", "TempDb", "Memory", "FileIo", "Backups", "AgentJobs", "IndexHealth", "StaleStats" };
        var recent = new[] { Workspace.Overview(Prod01.Id) }
            .Concat(tags.Select(t => new Route(t, ConnectionId: Prod01.Id)))
            .ToArray();

        var section = Assert.Single(Search("", OnProd01(recent)));
        Assert.Equal(SwitcherGroup.Recent, section.Group);
        Assert.Equal("Recent", section.Title);
        Assert.Equal(QuickSwitcher.RecentShown, section.Items.Count);
        // Most recent first; the Overview on screen is left out.
        Assert.Equal("Processes · PROD-SQL01", section.Items[0].Text);
        Assert.Equal("Activity now", section.Items[0].Detail);
        Assert.Equal(Prod01.Id, section.Items[0].Server);
        Assert.Equal("AgentJobs", section.Items[^1].Route.Tag);
    }

    [Fact]
    public void NothingTyped_AndNothingOpenedYet_ShowsNothing()
    {
        Assert.Empty(Search("   ", OnProd01(Workspace.Overview(Prod01.Id))));
    }

    [Fact]
    public void RecentRoutes_KeepEachPlaceOnce_MostRecentFirst_UpToTheirCapacity()
    {
        var recent = new RecentRoutes();
        recent.Add(new Route("Processes", ConnectionId: Prod01.Id));
        recent.Add(new Route("Settings", Settings: new SettingsLocation(SettingsCategory.Monitoring)));
        recent.Add(new Route("Processes", ConnectionId: Prod01.Id));
        // Settings on another category is the same place.
        recent.Add(new Route("Settings", Settings: new SettingsLocation(SettingsCategory.Appearance)));

        Assert.Equal(2, recent.Items.Count);
        Assert.Equal(SettingsCategory.Appearance, recent.Items[0].Settings!.Category);
        Assert.Equal("Processes", recent.Items[1].Tag);

        for (var i = 0; i < RecentRoutes.Capacity + 5; i++)
            recent.Add(new Route("Processes", ConnectionId: Guid.NewGuid()));
        Assert.Equal(RecentRoutes.Capacity, recent.Items.Count);
    }

    [Fact]
    public void ARoute_IsDescribed_ByItsPageAndServer()
    {
        static string? Name(Guid id) => id == Prod01.Id ? "PROD-SQL01" : null;
        Assert.Equal("Processes · PROD-SQL01", Breadcrumb.Describe(new Route("Processes", ConnectionId: Prod01.Id), Name));
        Assert.Equal("Overview · PROD-SQL01", Breadcrumb.Describe(Workspace.Overview(Prod01.Id), Name));
        Assert.Equal("Alerts · PROD-SQL01", Breadcrumb.Describe(new Route("Alerts", AlertsConnectionId: Prod01.Id), Name));
        Assert.Equal("All servers", Breadcrumb.Describe(new Route("Home"), Name));
        // A server that has gone: just the page.
        Assert.Equal("Processes", Breadcrumb.Describe(new Route("Processes", ConnectionId: Guid.NewGuid()), Name));
    }

    // Every page can be found by a word of what it shows, not just its name (#144).
    [Fact]
    public void EveryServerPage_HasATooltipToBeFoundBy()
    {
        Assert.All(NavMenu.Groups.SelectMany(g => g.Pages), p => Assert.False(string.IsNullOrWhiteSpace(p.ToolTip), p.Tag));
    }
}
