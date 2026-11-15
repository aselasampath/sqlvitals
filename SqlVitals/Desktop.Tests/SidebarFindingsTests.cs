using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The flat sidebar's logic (#254): which screen a check past a threshold marks and how, the filter
/// box's matching, and the Recent row's screens.
/// </summary>
public sealed class SidebarFindingsTests
{
    // ── The finding dots ──────────────────────────────────────────────────

    [Fact]
    public void ACheckPastAThreshold_MarksTheScreenThatExplainsIt()
    {
        var findings = SidebarFindings.From(Score(
            (HealthIndicator.Blocking, HealthLevel.Warning, 240),
            (HealthIndicator.BackupsPastRpo, HealthLevel.Critical, 2),
            (HealthIndicator.Cpu, HealthLevel.Healthy, 10)));

        Assert.Equal(["Backups", "Processes"], findings.Keys.Order());

        var processes = findings["Processes"];
        Assert.Equal(HealthLevel.Warning, processes.Level);
        Assert.Equal("blocking", processes.ShortName);
        Assert.Equal("blocking, warning", processes.Spoken);
        Assert.Equal([$"Warning: {Phrase(HealthIndicator.Blocking, HealthLevel.Warning, 240)}"], processes.Lines);

        Assert.Equal(HealthLevel.Critical, findings["Backups"].Level);
        Assert.Equal("RPO missed", findings["Backups"].ShortName);
        Assert.Equal("RPO missed, critical", findings["Backups"].Spoken);
    }

    [Fact]
    public void TwoChecksOnOneScreen_TheWorseNamesIt_AndTheLinesListBoth()
    {
        // Both explained by Processes: the critical one first, whatever order the checks come in.
        var findings = SidebarFindings.From(Score(
            (HealthIndicator.Blocking, HealthLevel.Warning, 240),
            (HealthIndicator.OpenTransaction, HealthLevel.Critical, 45)));

        var processes = Assert.Single(findings).Value;
        Assert.Equal(HealthLevel.Critical, processes.Level);
        Assert.Equal("open transaction", processes.ShortName);
        Assert.Equal(2, processes.Lines.Count);
        Assert.StartsWith("Critical: ", processes.Lines[0]);
        Assert.StartsWith("Warning: ", processes.Lines[1]);
    }

    [Fact]
    public void NoScore_AHealthyOne_OrAnUnreachableServer_MarkNothing()
    {
        Assert.Empty(SidebarFindings.From(null));
        Assert.Empty(SidebarFindings.From(Score((HealthIndicator.Cpu, HealthLevel.Healthy, 10))));
        Assert.Empty(SidebarFindings.From(Score((HealthIndicator.Cpu, HealthLevel.Unknown, 0))));
        Assert.Empty(SidebarFindings.From(HealthScore.Unavailable("Login failed", DateTime.Now)));
    }

    [Fact]
    public void EveryCheck_HasAShortName_AndAScreenInTheSidebar()
    {
        foreach (var indicator in Enum.GetValues<HealthIndicator>())
        {
            var name = SidebarFindings.ShortName(indicator);
            Assert.InRange(name.Length, 1, 16);
            Assert.True(NavMenu.IsServerPage(EvidenceRoutes.For(indicator).PageTag), $"{indicator} marks no server screen");
        }
    }

    [Fact]
    public void TheWorstFinding_IsTheMostSevere()
    {
        var warning  = new PageFinding(HealthLevel.Warning, "CPU", ["Warning: CPU at 82 %"]);
        var critical = new PageFinding(HealthLevel.Critical, "blocking", ["Critical: blocking for 9 min"]);

        Assert.Same(critical, SidebarFindings.Worst([warning, null, critical]));
        Assert.Same(warning, SidebarFindings.Worst([null, warning]));
        Assert.Null(SidebarFindings.Worst([null, null]));
    }

    // ── The filter box ────────────────────────────────────────────────────

    [Theory]
    [InlineData("",          "TempDb",   true)]    // nothing typed keeps every screen
    [InlineData("   ",       "TempDb",   true)]
    [InlineData("temp",      "TempDb",   true)]    // the name's start
    [InlineData("stats",     "StaleStats", true)]  // a word's start
    [InlineData("db",        "TempDb",   true)]    // anywhere in the name
    [InlineData("blocking",  "Processes", true)]   // a word of its tooltip
    [InlineData("rpo",       "Backups",  true)]
    [InlineData("tempdb",    "Backups",  false)]
    [InlineData("zzz",       "Processes", false)]
    public void TheFilter_MatchesAScreensNameOrTooltip_AsTheQuickSwitcherDoes(string query, string tag, bool kept) =>
        Assert.Equal(kept, SidebarFindings.Matches(query, NavMenu.AllPages.Single(p => p.Tag == tag)));

    [Fact]
    public void Enter_OpensTheBestMatch_NotTheFirstInTheSidebar()
    {
        var pages = NavMenu.Global.Concat(NavMenu.Groups.SelectMany(g => g.Pages)).ToList();

        // Deadlocks comes first and only matches by its tooltip's words; Stale Stats by a word of its name.
        Assert.Equal("StaleStats", SidebarFindings.Best("stat", pages)!.Tag);
        Assert.Equal("Memory", SidebarFindings.Best("memory", pages)!.Tag);     // the whole name, before tooltips
        Assert.Equal("TempDb", SidebarFindings.Best("temp", pages)!.Tag);
        Assert.Equal("Processes", SidebarFindings.Best("blocking", pages)!.Tag);
        Assert.Null(SidebarFindings.Best("zzz", pages));
        Assert.Null(SidebarFindings.Best("  ", pages));
    }

    [Fact]
    public void Same_ComparesTheMarks_NotTheDictionaries()
    {
        var a = SidebarFindings.From(Score((HealthIndicator.Blocking, HealthLevel.Warning, 240)));
        var b = SidebarFindings.From(Score((HealthIndicator.Blocking, HealthLevel.Warning, 240)));
        Assert.NotSame(a, b);
        Assert.True(SidebarFindings.Same(a, b));
        Assert.False(SidebarFindings.Same(a, SidebarFindings.From(Score((HealthIndicator.Blocking, HealthLevel.Critical, 240)))));
        Assert.False(SidebarFindings.Same(a, SidebarFindings.None));
        Assert.True(SidebarFindings.Same(SidebarFindings.None, new Dictionary<string, PageFinding>()));
    }

    // ── The Recent row ────────────────────────────────────────────────────

    [Fact]
    public void Recent_IsThisServersLastThreeScreens_OtherThanTheOneOnShow()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var recent = new RecentRoutes();
        foreach (var route in new[]
                 {
                     new Route("Backups", ConnectionId: a),
                     new Route("Waits", ConnectionId: a),
                     new Route("Memory", ConnectionId: b),     // another server's
                     new Route("Home"),                         // about every server
                     new Route("Alerts"),
                     Workspace.Overview(a),
                     new Route("TopWaits", ConnectionId: a),    // merged into Waits (#178): Waits again
                     new Route("IndexHealth", ConnectionId: a),
                     new Route("Processes", ConnectionId: a),   // on show
                 })
            recent.Add(route);

        Assert.Equal(["IndexHealth", "Waits", "Overview"], SidebarFindings.Recent(recent.Items, a, current: "Processes"));
        Assert.Equal(["Memory"], SidebarFindings.Recent(recent.Items, b, current: "Processes"));
        Assert.Empty(SidebarFindings.Recent(recent.Items, null, current: "Home"));
        Assert.Empty(SidebarFindings.Recent([], a, current: "Processes"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HealthReading Reading(HealthIndicator indicator, HealthLevel level, double value) =>
        new(indicator, level, value, new HealthThreshold(1, 2), Reason: null, Measured: level != HealthLevel.Unknown);

    private static string Phrase(HealthIndicator indicator, HealthLevel level, double value) =>
        HealthRules.Phrase(Reading(indicator, level, value));

    private static HealthScore Score(params (HealthIndicator Indicator, HealthLevel Level, double Value)[] checks) =>
        HealthScore.From(checks.Select(c => Reading(c.Indicator, c.Level, c.Value)).ToList(), DateTime.Now);
}
