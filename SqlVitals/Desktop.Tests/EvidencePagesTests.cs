using System.IO;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Pages opened as evidence (#145): the route's filter and window applied on the page, and
/// cleared by Show all.
/// </summary>
public sealed class EvidencePagesTests : IDisposable
{
    private static readonly Guid Server = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static PageFocus Focus(FocusFilter filter, DateTime? fromUtc = null) =>
        new(filter, new FocusOrigin(FocusSource.Alert, "Blocking · critical", StartedUtc: DateTime.UtcNow), fromUtc);

    // ── Processes: the head blocker's chain ──────────────────────────────

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0) =>
        new(spid, blockedBy, blockedBy != 0 ? "suspended" : "sleeping", "login", "host", "app",
            blockedBy != 0 ? "LCK_M_X" : null, waitMs, null, null, "db", 0, 0, 0,
            DateTime.MinValue, DateTime.MinValue, 0, null, null, "");

    // 87 blocks 112 and 115 (115 for a minute); 40 blocks 41 briefly; 5 and 6 block nothing.
    private static readonly ProcessNode[] Sessions =
    [
        Node(87), Node(112, blockedBy: 87, waitMs: 4_000), Node(115, blockedBy: 87, waitMs: 60_000),
        Node(40), Node(41, blockedBy: 40, waitMs: 1_000),
        Node(5), Node(6),
    ];

    [Fact]
    public void Processes_ShowsTheChainWaitingLongest_UntilShowAll() => UiThread.Run(() =>
    {
        var page = new ProcessesPage(new FakeRepository
        {
            GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>(Sessions),
        }, new ConnectionSettingsService(_dir));
        var focusPage = (IFocusPage)page;

        focusPage.ShowFocus(Focus(new FocusFilter(FocusKind.BlockingChain)));
        Assert.Null(focusPage.FocusNote);   // nothing read yet
        UiThread.Wait(page.RefreshAsync());

        Assert.Equal("showing the blocking chain of session 87 only", focusPage.FocusNote);
        Assert.Equal("Show all 7 sessions", focusPage.ShowAllText);
        Assert.Equal([87, 112, 115], Shown(page));

        focusPage.ClearFocus();

        Assert.Null(focusPage.FocusNote);
        Assert.Null(focusPage.ShowAllText);
        Assert.Equal([5, 6, 40, 41, 87, 112, 115], Shown(page));
    });

    [Fact]
    public void Processes_WithNothingBlocked_SaysSo() => UiThread.Run(() =>
    {
        var page = new ProcessesPage(new FakeRepository
        {
            GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>([Node(5), Node(6)]),
        }, new ConnectionSettingsService(_dir));

        ((IFocusPage)page).ShowFocus(Focus(new FocusFilter(FocusKind.BlockingChain)));
        UiThread.Wait(page.RefreshAsync());

        Assert.Equal("nothing is blocked now", ((IFocusPage)page).FocusNote);
        Assert.Empty(Shown(page));
    });

    // The sessions drawn on the map.
    private static List<int> Shown(ProcessesPage page) =>
        UiThread.Find<System.Windows.Shapes.Ellipse>(page.ProcessMap.ProcessCanvas)
                .Select(e => ((ProcessNode)e.Tag).SessionId).Order().ToList();

    // ── Resource Queries: by CPU, run since the window's start ───────────

    private static ResourceIntensiveQuery Query(string text, DateTime lastRun, long cpu) =>
        new(text, "db", null, 1, 0, 0, cpu, cpu, 0, 0, 0, 0, lastRun, lastRun, lastRun);

    [Fact]
    public void ResourceQueries_ShowsTheQueriesByCpu_RunSinceTheWindowStarted() => UiThread.Run(() =>
    {
        // The server's clock is this PC's.
        var now  = DateTime.Now;
        var page = new ResourceQueriesPage(new FakeRepository
        {
            GetResourceIntensiveQueriesAsync = _ => Task.FromResult<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
                    ([], [Query("old", now.AddHours(-2), 900), Query("during", now.AddMinutes(-5), 500)], [])),
        }, TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
        var focusPage = (IFocusPage)page;
        var since     = DateTime.UtcNow.AddMinutes(-20);

        focusPage.ShowFocus(Focus(new FocusFilter(FocusKind.TopCpu), since));
        UiThread.Wait(page.RefreshAsync());

        Assert.Same(page.TabCpu, page.Tabs.SelectedItem);
        Assert.Equal(["during"], page.CpuGrid.Items.Cast<ResourceIntensiveQuery>().Select(q => q.QueryText));
        Assert.Equal($"showing the 1 of the top 2 queries by CPU last run since {since.ToLocalTime():HH:mm}", focusPage.FocusNote);
        Assert.Equal("Show all 2 queries", focusPage.ShowAllText);

        focusPage.ClearFocus();

        Assert.Equal(["old", "during"], page.CpuGrid.Items.Cast<ResourceIntensiveQuery>().Select(q => q.QueryText));
        Assert.Null(focusPage.ShowAllText);
    });

    // ── Active Waits: a session from Processes ───────────────────────────

    private static ActiveWait Wait(int session, int blockedBy = 0) =>
        new(session, blockedBy, "LCK_M_X", 1_000, 1, null, null, "suspended", "SELECT", 0, 0, 0, 0, 0, 0, 1,
            "login", "host", "app", "db", "Lock", blockedBy != 0 ? 1 : 0, null, DateTime.Now, "Warning");

    [Fact]
    public void ActiveWaits_ShowsTheSession_AndTheSessionsItBlocks() => UiThread.Run(() =>
    {
        var page = new ActiveWaitsPage(new FakeRepository
        {
            GetActiveWaitsAsync = () => Task.FromResult<IEnumerable<ActiveWait>>([Wait(87), Wait(112, blockedBy: 87), Wait(200)]),
        });
        var focusPage = (IFocusPage)page;

        focusPage.ShowFocus(new PageFocus(new FocusFilter(FocusKind.Session, 87), new FocusOrigin(FocusSource.Page, "session 87", "Processes")));
        UiThread.Wait(page.RefreshAsync());

        Assert.Equal([87, 112], page.ActiveGrid.Items.Cast<ActiveWait>().Select(w => w.SessionId));
        Assert.Equal("showing session 87 and the sessions it blocks only", focusPage.FocusNote);
        Assert.Equal("Show all 3 waiting tasks", focusPage.ShowAllText);

        focusPage.ClearFocus();

        Assert.Equal(3, page.ActiveGrid.Items.Count);
    });

    // ── The banner, and Home's Investigate ───────────────────────────────

    [Fact]
    public void TheBanner_ShowsShowAll_OnlyWhenThePageHasAFilter() => UiThread.Run(() =>
    {
        var banner = new FocusBannerView();
        var text   = new FocusBannerText("Opened from alert", "CPU · critical · since 13:58", null, null);

        banner.Show(text, "Show all 20 queries");
        Assert.Equal("Opened from alert CPU · critical · since 13:58 [Show all 20 queries]", banner.Text);

        banner.Show(text, showAll: null);
        Assert.Equal("Opened from alert CPU · critical · since 13:58", banner.Text);

        banner.Show(text with { Past = "It ended at 14:05; this page shows the server now.", HistoryLink = "Open Live charts for 13:43–14:20" }, null);
        Assert.EndsWith("| It ended at 14:05; this page shows the server now. [Open Live charts for 13:43–14:20]", banner.Text);

        banner.Show(null, null);
        Assert.Equal(Visibility.Collapsed, banner.Visibility);
    });

    [Fact]
    public void AHomeTile_OffersInvestigate_OnlyWithAnActiveAlert()
    {
        var tile  = ServerTile.From(new TileInput(Server, "PROD", "prod-sql", "Sales", false, true, null), DateTime.Now);
        var alert = new Alert(Guid.NewGuid(), Server, HealthIndicator.Blocking, HealthLevel.Critical, DateTime.UtcNow, null, 240, 120, "");

        var home = new HomeTile(tile, null);
        Assert.Equal(Visibility.Collapsed, home.InvestigateVisibility);

        home.Update(tile, null, alert);
        Assert.Equal(Visibility.Visible, home.InvestigateVisibility);
        Assert.Equal("Investigate the Blocking alert on PROD", home.InvestigateName);
        Assert.StartsWith("Blocking · critical: open Blocking chain", home.InvestigateToolTip);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
