using System.IO;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
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
        var page = Wave2Pages.Processes(new FakeRepository
        {
            GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>(Sessions),
        }, new ConnectionSettingsService(_dir));
        var focusPage = (IFocusPage)page;

        focusPage.ShowFocus(Focus(new FocusFilter(FocusKind.BlockingChain)));
        Assert.Null(focusPage.FocusNote);   // nothing read yet
        UiThread.Wait(page.ViewModel.RefreshAsync());

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
        var page = Wave2Pages.Processes(new FakeRepository
        {
            GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>([Node(5), Node(6)]),
        }, new ConnectionSettingsService(_dir));

        ((IFocusPage)page).ShowFocus(Focus(new FocusFilter(FocusKind.BlockingChain)));
        UiThread.Wait(page.ViewModel.RefreshAsync());

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
        var page = Wave2Pages.ResourceQueries(new FakeRepository
        {
            GetResourceIntensiveQueriesAsync = _ => Task.FromResult<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
                    ([], [Query("old", now.AddHours(-2), 900), Query("during", now.AddMinutes(-5), 500)], [])),
        }, TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
        var focusPage = (IFocusPage)page;
        var since     = DateTime.UtcNow.AddMinutes(-20);

        focusPage.ShowFocus(Focus(new FocusFilter(FocusKind.TopCpu), since));
        UiThread.Wait(page.ViewModel.RefreshAsync());

        Assert.Same(page.TabCpu, page.Tabs.SelectedItem);
        Assert.Equal(["during"], page.CpuGrid.Items.Cast<ResourceIntensiveQuery>().Select(q => q.QueryText));
        Assert.Equal($"showing the 1 of the top 2 queries by CPU last run since {since.ToLocalTime():HH:mm}", focusPage.FocusNote);
        Assert.Equal("Show all 2 queries", focusPage.ShowAllText);

        focusPage.ClearFocus();

        Assert.Equal(["old", "during"], page.CpuGrid.Items.Cast<ResourceIntensiveQuery>().Select(q => q.QueryText));
        Assert.Null(focusPage.ShowAllText);
    });

    // ── Processes' Waiting view (Active Waits until #178): a session's link ──

    private static ActiveWait Wait(int session, int blockedBy = 0) =>
        new(session, blockedBy, "LCK_M_X", 1_000, 1, null, null, "suspended", "SELECT", 0, 0, 0, 0, 0, 0, 1,
            "login", "host", "app", "db", "Lock", blockedBy != 0 ? 1 : 0, null, DateTime.Now, "Warning");

    private ProcessesPage Waiting(Func<int>? counted = null, string? view = null) => Wave2Pages.Processes(new FakeRepository
    {
        GetProcessesAsync   = () => Task.FromResult<IEnumerable<ProcessNode>>(Sessions),
        GetActiveWaitsAsync = () =>
        {
            counted?.Invoke();
            return Task.FromResult<IEnumerable<ActiveWait>>([Wait(87), Wait(112, blockedBy: 87), Wait(200)]);
        },
    }, new ConnectionSettingsService(_dir), view: view);

    [Fact]
    public void Processes_ASessionsLink_OpensTheWaitingView_OnTheSession_AndTheSessionsItBlocks() => UiThread.Run(() =>
    {
        var page = Waiting();
        var focusPage = (IFocusPage)page;

        focusPage.ShowFocus(new PageFocus(new FocusFilter(FocusKind.Session, 87), new FocusOrigin(FocusSource.Page, "session 87", "Processes")));
        Assert.Equal(ProcessesView.Waiting, page.View);
        UiThread.Wait(page.ViewModel.RefreshAsync());

        Assert.Equal([87, 112], page.WaitingGrid.Items.Cast<ActiveWait>().Select(w => w.SessionId));
        Assert.Equal("showing session 87 and the sessions it blocks only", focusPage.FocusNote);
        Assert.Equal("Show all 3 waiting tasks", focusPage.ShowAllText);

        focusPage.ClearFocus();

        Assert.Equal(3, page.WaitingGrid.Items.Count);
        Assert.Null(focusPage.ShowAllText);
        // A link doesn't change the view Processes opens on.
        Assert.Equal(ProcessesView.Map, new ConnectionSettingsService(_dir).Load().ProcessesView);
    });

    [Fact]
    public void Processes_TheWaitingView_ReadsTheWaits_AndJudgesThem_TheMapNeither() => UiThread.Run(() =>
    {
        var reads = 0;
        var page  = Waiting(() => ++reads, view: nameof(ProcessesView.Waiting));
        Assert.Equal(ProcessesView.Waiting, page.View);
        Assert.Equal(Visibility.Visible, page.WaitingPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, page.ProcessMap.Visibility);

        UiThread.Wait(page.ViewModel.RefreshAsync());
        Assert.Equal(1, reads);
        Assert.StartsWith("Session 112 is blocked by session 87", page.ViewModel.Verdict!.Headline);

        // The map: the sessions' verdict, from what was read, and no more waits read.
        page.BtnMap.IsChecked = true;
        Assert.Equal((ProcessesView.Map, Visibility.Collapsed, Visibility.Visible), (page.View, page.WaitingPanel.Visibility, page.ProcessMap.Visibility));
        Assert.StartsWith("2 head blockers are blocking", page.ViewModel.Verdict!.Headline);
        UiThread.Wait(page.ViewModel.RefreshAsync());
        Assert.Equal(1, reads);
    });

    [Fact]
    public void Processes_ABlockingChain_IsShownOnTheMap_WhenWaitingWasLastChosen() => UiThread.Run(() =>
    {
        new ConnectionSettingsService(_dir).SetProcessesView(ProcessesView.Waiting, groupByLogin: true);
        var page = Waiting();
        Assert.Equal(ProcessesView.Waiting, page.View);

        ((IFocusPage)page).ShowFocus(Focus(new FocusFilter(FocusKind.BlockingChain)));
        UiThread.Wait(page.ViewModel.RefreshAsync());

        Assert.Equal((ProcessesView.Map, Visibility.Visible), (page.View, page.ProcessMap.Visibility));
        Assert.Equal("showing the blocking chain of session 87 only", ((IFocusPage)page).FocusNote);
        // The view Processes opens on is still the one chosen.
        Assert.Equal(ProcessesView.Waiting, new ConnectionSettingsService(_dir).Load().ProcessesView);
    });

    [Fact]
    public void Processes_ASessionsFilter_IsOnlyDescribedOnTheWaitingView_AndShowAllClearsIt() => UiThread.Run(() =>
    {
        var page = Waiting();
        var focusPage = (IFocusPage)page;
        focusPage.ShowFocus(new PageFocus(new FocusFilter(FocusKind.Session, 87), new FocusOrigin(FocusSource.Page, "session 87", "Processes")));
        UiThread.Wait(page.ViewModel.RefreshAsync());

        // The map shows every session: the banner says nothing of session 87, but Show all still clears it.
        page.BtnMap.IsChecked = true;
        Assert.Null(focusPage.FocusNote);
        Assert.Equal("Show all", focusPage.ShowAllText);

        focusPage.ClearFocus();
        Assert.Null(focusPage.ShowAllText);
        page.BtnWaiting.IsChecked = true;
        UiThread.Wait(page.ViewModel.RefreshAsync());
        Assert.Equal(3, page.WaitingGrid.Items.Count);
    });

    [Fact]
    public void Processes_ShowTheChainOnTheMap_KeepsTheViewProcessesOpensOn() => UiThread.Run(() =>
    {
        new ConnectionSettingsService(_dir).SetProcessesView(ProcessesView.Grid, groupByLogin: true);
        var page = Waiting(view: nameof(ProcessesView.Waiting));

        TemplateRolloutTests.Hosted(page, page.ViewModel.RefreshAsync, null, () =>
        {
            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);   // Show the chain on the map
            Assert.Equal(ProcessesView.Map, page.View);
            Assert.Equal(87, page.ProcessMap.SelectedSession);
        });

        Assert.Equal(ProcessesView.Grid, new ConnectionSettingsService(_dir).Load().ProcessesView);
    });

    [Fact]
    public void Processes_AnOldActiveWaitsRoute_OpensTheWaitingView()
    {
        // #178: a route to the Active Waits page, saved or from Back, opens Processes on Waiting.
        var server = Guid.NewGuid();
        var focus  = new PageFocus(new FocusFilter(FocusKind.Session, 87), new FocusOrigin(FocusSource.Page, "session 87", "Processes"));

        Assert.Equal(new Route("Processes", server, View: "Waiting", Focus: focus), NavMenu.Current(new Route("ActiveWaits", server, Focus: focus)));
        Assert.Equal(new Route("Processes", server, View: "Waiting"), NavMenu.Current(new Route("ActiveWaits", server)));
    }

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
    public void AHomeTile_ShowsTheLastHourOfItsViewModelsClock()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var tile = ServerTile.From(new TileInput(Server, "PROD", "prod-sql", "Sales", false, true, null), DateTime.Now);

        var home = new HomeTile(tile, null, time: time);

        Assert.Equal(time.GetUtcNow().UtcDateTime, home.Trend.ToUtc);
        Assert.Equal(time.GetUtcNow().UtcDateTime - HomeTile.TrendSpan, home.Trend.FromUtc);
    }

    [Fact]
    public void AHomeTile_OffersInvestigate_OnlyWithAnActiveAlert()
    {
        var tile  = ServerTile.From(new TileInput(Server, "PROD", "prod-sql", "Sales", false, true, null), DateTime.Now);
        var alert = new Alert(Guid.NewGuid(), Server, HealthIndicator.Blocking, HealthLevel.Critical, DateTime.UtcNow, null, 240, 120, "");

        var home = new HomeTile(tile, null);
        Assert.False(home.ShowsInvestigate);

        home.Update(tile, null, alert);
        Assert.True(home.ShowsInvestigate);
        Assert.Equal("Investigate the Blocking alert on PROD", home.InvestigateName);
        Assert.StartsWith("Blocking · critical: open Blocking chain", home.InvestigateToolTip);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
