using System.IO;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Tests;

/// <summary>The sidebar's groups (#124): what's in them, and which ones the user collapsed.</summary>
public sealed class NavMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Create() => new(_dir);

    // ── The menu ─────────────────────────────────────────────────────────

    [Fact]
    public void TheGroups_HoldThePagesInTheStorysOrder()
    {
        var layout = NavMenu.Groups.Select(g => $"{g.Name}: {string.Join(", ", g.Pages.Select(p => p.Label))}");

        Assert.Equal(
        [
            "Overview: Live Metrics, Alerts",
            "Activity now: Processes, Active Waits, Deadlocks, App Connections",
            "Waits: Top Waits, Wait Trend",
            "Queries: Resource Queries, Query Store, Query Regressions, Plan Cache Health, Implicit Conv., SP Trace",
            "Resources: Memory Grants, TempDB, File I/O, DB Storage, Perfmon",
            "Maintenance: Index Health, Stale Stats, Backups, Agent Jobs",
            "Configuration: Config Checks, Security Audit",
        ], layout);
        Assert.Equal(["Export", "Settings"], NavMenu.Footer.Select(p => p.Tag));
        Assert.Equal(["Home"], NavMenu.Header.Select(p => p.Tag));   // above the groups (#130)
        Assert.Equal("Home", NavMenu.AllPages.First().Tag);
        Assert.Null(NavMenu.GroupOf("Home"));
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
                "Home", "LiveMetrics", "Alerts", "TopWaits", "ActiveWaits", "Processes", "Deadlocks", "AgentJobs", "Backups",
                "FileIo", "ConfigChecks", "SecurityAudit", "WaitTrend", "TempDb", "Memory", "QueryStore",
                "QueryRegressions", "IndexHealth", "ResourceQueries", "ImplicitConv", "PlanCacheHealth", "StaleStats",
                "DbStorage", "AppConnections", "Perfmon", "SpTrace", "Export", "Settings",
            }.Order(), tags.Order());
    }

    [Fact]
    public void GroupIdsAndIcons_AreUnique_AndEveryGroupHasPagesAndAQuestion()
    {
        Assert.Equal(NavMenu.Groups.Count, NavMenu.Groups.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(NavMenu.Groups, g =>
        {
            Assert.NotEmpty(g.Pages);
            Assert.EndsWith("?", g.Question);
        });

        var icons = NavMenu.AllPages.Select(p => p.Icon).ToList();
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }

    [Theory]
    [InlineData("Processes", "Activity")]
    [InlineData("TempDb", "Resources")]
    [InlineData("SecurityAudit", "Configuration")]
    [InlineData("Settings", null)]
    [InlineData("NoSuchPage", null)]
    public void GroupOf_FindsThePagesGroup(string tag, string? expected) =>
        Assert.Equal(expected, NavMenu.GroupOf(tag)?.Id);

    [Fact]
    public void NormalizeCollapsed_KeepsKnownGroupsOnce_InSidebarOrder()
    {
        Assert.Equal(["Waits", "Maintenance"], NavMenu.NormalizeCollapsed(["maintenance", "Gone", "Waits", "WAITS"]));
        Assert.Empty(NavMenu.NormalizeCollapsed(null));
    }

    // ── Saved state ──────────────────────────────────────────────────────

    [Fact]
    public void ANewInstallation_HasEveryGroupExpanded() =>
        Assert.Empty(Create().Load().NavCollapsedGroups);

    [Fact]
    public void CollapsedGroups_SurviveSaveAndLoad()
    {
        var service = Create();
        service.SetNavGroupCollapsed("Maintenance", collapsed: true);
        service.SetNavGroupCollapsed("Waits", collapsed: true);
        service.SetNavGroupCollapsed("Waits", collapsed: true);
        Assert.Equal(["Waits", "Maintenance"], Create().Load().NavCollapsedGroups);

        service.SetNavGroupCollapsed("Maintenance", collapsed: false);
        Assert.Equal(["Waits"], Create().Load().NavCollapsedGroups);
    }

    [Fact]
    public void AFileFromBeforeTheGroups_HasEveryGroupExpanded()
    {
        Create().SetNavGroupCollapsed("Waits", collapsed: true);
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.Remove("NavCollapsedGroups");
        File.WriteAllText(file, json.ToJsonString());

        Assert.Empty(Create().Load().NavCollapsedGroups);
    }

    [Fact]
    public void AGroupThatNoLongerExists_IsDroppedOnLoad()
    {
        Create().SetNavGroupCollapsed("Waits", collapsed: true);
        var file = Path.Combine(_dir, "settings.dat");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["NavCollapsedGroups"] = new JsonArray("Waits", "Tuning");
        File.WriteAllText(file, json.ToJsonString());

        Assert.Equal(["Waits"], Create().Load().NavCollapsedGroups);
    }

    [Fact]
    public void CollapsingAndExpanding_IsNotInTheActivityLog()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        service.SetNavGroupCollapsed("Maintenance", collapsed: true);
        service.SetNavGroupCollapsed("Maintenance", collapsed: false);

        Assert.Empty(raised);
        Assert.Empty(Create().Load().NavCollapsedGroups);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
