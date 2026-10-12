using System.IO;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The sidebar's menu (#124, #141): the global pages, the server's groups and what's in them, and
/// the page remembered for the next start.
/// </summary>
public sealed class NavMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Create() => new(_dir);

    // ── The menu ─────────────────────────────────────────────────────────

    [Fact]
    public void TheGlobalPart_TheServersGroups_AndSettingsAtTheFoot()
    {
        Assert.Equal(["All servers", "Alerts", "Export / AI"], NavMenu.Global.Select(p => p.Label));
        Assert.Equal(["Settings"], NavMenu.Footer.Select(p => p.Tag));

        var layout = NavMenu.Groups.Select(g => $"{g.Name}: {string.Join(", ", g.Pages.Select(p => p.Label))}");
        Assert.Equal(
        [
            "Overview: Overview, Live charts",
            "Activity now: Processes, Deadlocks, App Connections",
            "Waits: Waits",
            "Queries: Resource Queries, Query Store, Query Regressions, Plan Cache Health, SP Trace",
            "Resources: Memory, TempDB, File I/O, DB Storage, Perfmon",
            "Maintenance: Index Health, Stale Stats, Backups, Agent Jobs",
            "Configuration: Config Checks, Security Audit",
        ], layout);

        Assert.Equal("Home", NavMenu.AllPages.First().Tag);
        Assert.Equal("Overview", NavMenu.Groups[0].Pages[0].Tag);   // the server's Overview first (#143)
        Assert.All(NavMenu.Global.Concat(NavMenu.Footer), p => Assert.False(NavMenu.IsServerPage(p.Tag)));
        Assert.All(NavMenu.Groups.SelectMany(g => g.Pages), p => Assert.True(NavMenu.IsServerPage(p.Tag)));
    }

    [Fact]
    public void EveryPage_IsInTheSidebarOnce()
    {
        var tags = NavMenu.AllPages.Select(p => p.Tag).ToList();

        Assert.Equal(tags.Count, tags.Distinct().Count());
        // The pages MainWindow.NavigateTo opens; a new page must be added to NavMenu to get a button.
        Assert.Equal(
            new[]
            {
                "Home", "Overview", "LiveMetrics", "Alerts", "Waits", "Processes", "Deadlocks", "AgentJobs",
                "Backups", "FileIo", "ConfigChecks", "SecurityAudit", "TempDb", "Memory", "QueryStore",
                "QueryRegressions", "IndexHealth", "ResourceQueries", "PlanCacheHealth", "StaleStats",
                "DbStorage", "AppConnections", "Perfmon", "SpTrace", "Export", "Settings",
            }.Order(), tags.Order());
    }

    // ── Pages merged into others (#178) ──────────────────────────────────

    [Theory]
    [InlineData("ActiveWaits", "Processes")]
    [InlineData("TopWaits", "Waits")]
    [InlineData("WaitTrend", "Waits")]
    [InlineData("ImplicitConv", "PlanCacheHealth")]
    [InlineData("Memory", "Memory")]
    [InlineData("Processes", "Processes")]
    public void AMergedPagesTag_OpensThePageItIsPartOfNow(string old, string now)
    {
        Assert.Equal(now, NavMenu.Current(old));
        Assert.Contains(now, NavMenu.AllPages.Select(p => p.Tag));
        Assert.True(PageCatalog.Types.ContainsKey(now));
    }

    [Fact]
    public void AMergedPagesRoute_KeepsItsServer_AndOpensOnThePartItWas()
    {
        var server = Guid.NewGuid();
        var alert  = new PageFocus(new FocusFilter(FocusKind.BlockingChain), new FocusOrigin(FocusSource.Alert, "Blocking"));

        Assert.Equal(new Route("Waits", server), NavMenu.Current(new Route("WaitTrend", server)));
        Assert.Equal(new Route("Waits", server), NavMenu.Current(new Route("TopWaits", server)));
        Assert.Equal(new Route("PlanCacheHealth", server, View: NavMenu.ImplicitConversionsView), NavMenu.Current(new Route("ImplicitConv", server)));
        // Only a session's filter meant something on Active Waits.
        Assert.Equal(new Route("Processes", server, View: "Waiting"), NavMenu.Current(new Route("ActiveWaits", server, Focus: alert)));
        // A page that wasn't merged is as it was.
        var processes = new Route("Processes", server, Focus: alert);
        Assert.Same(processes, NavMenu.Current(processes));
        Assert.All(NavMenu.Merged.Keys, tag => Assert.DoesNotContain(tag, NavMenu.AllPages.Select(p => p.Tag)));
    }

    [Theory]
    [InlineData("ActiveWaits", "Processes")]
    [InlineData("TopWaits", "Waits")]
    [InlineData("WaitTrend", "Waits")]
    [InlineData("ImplicitConv", "PlanCacheHealth")]
    public void ALastPageSavedBeforeTheMerge_OpensThePageItIsPartOfNow(string old, string now)
    {
        Create().SetLastPage("Processes", Guid.NewGuid());
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["LastPage"] = old;
        File.WriteAllText(file, json.ToJsonString());

        Assert.Equal(now, Create().Load().LastPage);
    }

    [Fact]
    public void Icons_AreFluentGlyphs_UniquePerPage_AndEveryGroupHasPagesAndAQuestion()
    {
        Assert.Equal(NavMenu.Groups.Count, NavMenu.Groups.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(NavMenu.Groups, g =>
        {
            Assert.NotEmpty(g.Pages);
            Assert.EndsWith("?", g.Question);
        });

        var icons = NavMenu.AllPages.Select(p => p.Icon).Concat(NavMenu.Groups.Select(g => g.Icon)).ToList();
        Assert.Equal(icons.Count, icons.Distinct().Count());
        // One character each from the Private Use Area, where the icon fonts keep their glyphs: no emoji.
        Assert.All(icons, icon => Assert.True(icon.Length == 1 && icon[0] is >= '\uE000' and <= '\uF8FF', $"U+{(int)icon[0]:X4}"));
        Assert.StartsWith("Segoe Fluent Icons", NavMenu.IconFont);
        Assert.EndsWith("Segoe MDL2 Assets", NavMenu.IconFont);   // Windows 10
    }

    [Theory]
    [InlineData("Processes", "Activity")]
    [InlineData("Overview", "Overview")]
    [InlineData("LiveMetrics", "Overview")]
    [InlineData("TempDb", "Resources")]
    [InlineData("SecurityAudit", "Configuration")]
    [InlineData("Alerts", null)]
    [InlineData("Settings", null)]
    [InlineData("NoSuchPage", null)]
    public void GroupOf_FindsThePagesGroup(string tag, string? expected) =>
        Assert.Equal(expected, NavMenu.GroupOf(tag)?.Id);

    // ── The page reopened at start-up ─────────────────────────────────────

    [Fact]
    public void ANewInstallation_HasNoLastPage()
    {
        var store = Create().Load();
        Assert.Null(store.LastPage);
        Assert.Null(store.LastServerId);
    }

    [Fact]
    public void TheLastPageAndServer_SurviveSaveAndLoad()
    {
        var server = Guid.NewGuid();
        Create().SetLastPage("Processes", server);
        Assert.Equal(("Processes", server), (Create().Load().LastPage, Create().Load().LastServerId));

        Create().SetLastPage("Home", null);
        Assert.Equal(("Home", null), (Create().Load().LastPage, Create().Load().LastServerId));
    }

    [Fact]
    public void APageThatNoLongerExists_IsForgottenOnLoad()
    {
        Create().SetLastPage("Processes", Guid.NewGuid());
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["LastPage"] = "Tuning";
        // A file from before 0.57.0 has the collapsed groups, which are now ignored.
        json["NavCollapsedGroups"] = new JsonArray("Waits");
        File.WriteAllText(file, json.ToJsonString());

        Assert.Null(Create().Load().LastPage);
    }

    [Fact]
    public void AServersSummary_SavedBeforeTheOverview_OpensItsOverview()
    {
        var server = Guid.NewGuid();
        Create().SetLastPage("Processes", server);
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["LastPage"] = "ServerDetail";   // before 0.59.0 (#143)
        File.WriteAllText(file, json.ToJsonString());

        Assert.Equal(("Overview", server), (Create().Load().LastPage, Create().Load().LastServerId));
    }

    [Fact]
    public void TheLastPage_IsNotInTheActivityLog_AndIsOnlyWrittenWhenItChanges()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;
        var server = Guid.NewGuid();

        service.SetLastPage("Processes", server);
        var file    = Path.Combine(_dir, "settings.dat");
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written.AddMinutes(-1));
        service.SetLastPage("Processes", server);

        Assert.Empty(raised);
        Assert.Equal(written.AddMinutes(-1), File.GetLastWriteTimeUtc(file));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
