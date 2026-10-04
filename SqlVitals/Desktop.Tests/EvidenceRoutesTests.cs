using System.Globalization;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>Every alert and health check leads to its evidence (#145): page, filter and time window.</summary>
public sealed class EvidenceRoutesTests
{
    private static readonly Guid Server = Guid.NewGuid();

    // Today on this PC's clock, so the banner shows times without a date.
    private static readonly DateTime Start = DateTime.Today.AddHours(13).AddMinutes(58).ToUniversalTime();
    private static readonly DateTime End   = Start.AddMinutes(7);

    private static Alert AlertOf(HealthIndicator indicator, DateTime? ended = null, string detail = "",
                                 HealthLevel severity = HealthLevel.Critical) =>
        new(Guid.NewGuid(), Server, indicator, severity, Start, ended, 97, 90, detail,
            ended is null ? null : AlertEndReason.Recovered);

    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ── Every indicator has one ──────────────────────────────────────────

    [Fact]
    public void EveryHealthIndicator_HasAnEvidenceRoute_ToPagesInTheSidebar()
    {
        // A new indicator fails here until it is given a page, a filter and a history page.
        var pages = NavMenu.AllPages.Select(p => p.Tag).ToHashSet();

        Assert.All(Enum.GetValues<HealthIndicator>(), indicator =>
        {
            var evidence = EvidenceRoutes.For(indicator);
            Assert.Contains(evidence.PageTag, pages);
            if (evidence.HistoryTag is { } history)
            {
                Assert.Contains(history, pages);
                Assert.True(EvidenceRoutes.ShowsPast(history), $"{history} can't show {indicator}'s past");
            }
        });
    }

    [Fact]
    public void EveryAlertKind_HasAnEvidenceRoute_ThatItsPageCanApply()
    {
        // Alerts are raised per indicator: each one's page, when it filters, takes the filter.
        Assert.All(Enum.GetValues<HealthIndicator>(), indicator =>
        {
            var route = EvidenceRoutes.ForAlert(AlertOf(indicator));
            Assert.Equal(Server, route.ConnectionId);
            Assert.NotNull(route.Focus);
            var type = PageCatalog.Types[route.Tag];
            if (!route.Focus!.Filter.IsNone)
                Assert.True(typeof(IFocusPage).IsAssignableFrom(type), $"{type.Name} can't show {indicator}'s filter");
        });
    }

    [Theory]
    [InlineData(HealthIndicator.Blocking,            "Blocking chain",  "Processes",       FocusKind.BlockingChain, "LiveMetrics")]
    [InlineData(HealthIndicator.Cpu,                 "Top CPU queries", "ResourceQueries", FocusKind.TopCpu,        "LiveMetrics")]
    [InlineData(HealthIndicator.PageLifeExpectancy,  "Memory",          "Memory",          FocusKind.None,          "LiveMetrics")]
    [InlineData(HealthIndicator.MemoryGrantsPending, "Memory grants",   "Memory",          FocusKind.PendingGrants, "LiveMetrics")]
    [InlineData(HealthIndicator.LogSpace,            "Log space",       "DbStorage",       FocusKind.Database,      null)]
    [InlineData(HealthIndicator.TempDbSpace,         "TempDB",          "TempDb",          FocusKind.None,          null)]
    public void TheRoutes_AreTheStorys(HealthIndicator indicator, string text, string page, FocusKind filter, string? history)
    {
        var evidence = EvidenceRoutes.For(indicator);

        Assert.Equal((text, page, filter, history), (evidence.Text, evidence.PageTag, evidence.Filter, evidence.HistoryTag));
    }

    // ── An alert's route ─────────────────────────────────────────────────

    [Fact]
    public void AnActiveAlert_OpensItsPage_FromFifteenMinutesBeforeItStarted_ToNow()
    {
        var route = EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Cpu));

        Assert.Equal("ResourceQueries", route.Tag);
        Assert.Equal(new FocusFilter(FocusKind.TopCpu), route.Focus!.Filter);
        Assert.Equal(Start.AddMinutes(-15), route.Focus.FromUtc);
        Assert.Null(route.Focus.ToUtc);
        Assert.Equal(new FocusOrigin(FocusSource.Alert, "CPU · critical", StartedUtc: Start), route.Focus.Origin);
    }

    [Fact]
    public void APastAlert_HasItsWindow_FifteenMinutesEitherSide()
    {
        var route = EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Cpu, ended: End));

        Assert.Equal(Start.AddMinutes(-15), route.Focus!.FromUtc);
        Assert.Equal(End.AddMinutes(15), route.Focus.ToUtc);
        Assert.True(route.Focus.Origin.IsPast);
    }

    [Fact]
    public void ABlockingAlert_FiltersToTheHeadBlockersChain_WhileItLasts()
    {
        Assert.Equal(new FocusFilter(FocusKind.BlockingChain), EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking)).Focus!.Filter);

        // Its chain has gone: the page shows the sessions now, unfiltered, and says so.
        Assert.True(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking, ended: End)).Focus!.Filter.IsNone);
        Assert.True(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.MemoryGrantsPending, ended: End)).Focus!.Filter.IsNone);
    }

    [Fact]
    public void ALogSpaceAlert_FiltersToItsDatabase_EvenOnceEnded()
    {
        var active = EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.LogSpace, detail: "Log 93% full (Sales (EU))"));
        var past   = EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.LogSpace, ended: End, detail: "Log 93% full (Sales (EU))"));

        Assert.Equal(new FocusFilter(FocusKind.Database, Name: "Sales (EU)"), active.Focus!.Filter);
        Assert.Equal(active.Focus.Filter, past.Focus!.Filter);
        // Without a database named, every database.
        Assert.True(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.LogSpace, detail: "Log 93% full")).Focus!.Filter.IsNone);
    }

    // ── The history page, for a past alert on a page that shows now ─────

    [Fact]
    public void APastAlert_OnALivePage_OffersLiveChartsForItsWindow()
    {
        var route   = EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking, ended: End));
        var history = EvidenceRoutes.History(route)!;

        Assert.Equal(("LiveMetrics", Server), (history.Tag, history.ConnectionId));
        Assert.Equal((Start.AddMinutes(-15), End.AddMinutes(15)), (history.Focus!.FromUtc, history.Focus.ToUtc));
        Assert.True(history.Focus.Filter.IsNone);
        Assert.Null(history.Focus.HistoryTag);
        Assert.Equal(route.Focus!.Origin, history.Focus.Origin);
    }

    [Fact]
    public void NoHistoryPage_ForAnActiveAlert_OrOneSqlVitalsKeepsNoHistoryOf()
    {
        Assert.Null(EvidenceRoutes.History(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Cpu))));
        Assert.Null(EvidenceRoutes.History(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.TempDbSpace, ended: End))));
        Assert.Null(EvidenceRoutes.History(new Route("Processes", Server)));
    }

    // ── A health check, and links between pages ─────────────────────────

    [Fact]
    public void AHealthCheck_OpensItsPageFiltered_AsTheServerIsNow()
    {
        var route = EvidenceRoutes.ForCheck(HealthIndicator.LogSpace, Server, "Log 81% full (Sales)");

        Assert.Equal(("DbStorage", Server), (route.Tag, route.ConnectionId));
        Assert.Equal(new FocusFilter(FocusKind.Database, Name: "Sales"), route.Focus!.Filter);
        Assert.Equal(new FocusOrigin(FocusSource.HealthCheck, "Log 81% full (Sales)"), route.Focus.Origin);
        Assert.Null(route.Focus.FromUtc);
    }

    [Fact]
    public void ALink_CarriesItsFilter_AndTheBannerNamesThePageItCameFrom()
    {
        var route = EvidenceRoutes.Link("ActiveWaits", Server, new FocusFilter(FocusKind.Session, 87), "Processes");

        Assert.Equal(("ActiveWaits", Server, 87L), (route.Tag, route.ConnectionId, route.Focus!.Filter.Id));
        Assert.Equal(new FocusOrigin(FocusSource.Page, "session 87", "Processes"), route.Focus.Origin);
    }

    // ── Navigation ───────────────────────────────────────────────────────

    [Fact]
    public void ShowAll_KeepsThePlace_AndBackStillReturnsWhereTheClickCameFrom()
    {
        var history  = new NavigationHistory();
        var overview = Workspace.Overview(Server);
        var evidence = EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Cpu));
        history.Visit(overview);
        history.Visit(evidence);

        history.ReplaceCurrent(EvidenceRoutes.Unfocused(evidence));

        Assert.Equal(new Route("ResourceQueries", Server), history.Current);
        Assert.Equal(overview, history.PeekBack(_ => true));
    }

    [Fact]
    public void TheSamePage_OpenedAsEvidence_ReplacesTheEntry_SoBackDoesNotStopTwice()
    {
        var history = new NavigationHistory();
        history.Visit(Workspace.Overview(Server));
        history.Visit(new Route("Processes", Server));

        history.Visit(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking)));

        Assert.Equal(Workspace.Overview(Server), history.PeekBack(_ => true));
    }

    [Fact]
    public void AnotherServer_OpensThePageUnfiltered()
    {
        var other = Guid.NewGuid();

        var route = Workspace.OnServer(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking)), other);

        Assert.Equal(new Route("Processes", other), route);
    }

    [Fact]
    public void TheQuickSwitchersRecents_KeepThePage_NotTheAlert()
    {
        var recent = new RecentRoutes();

        recent.Add(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking)));

        Assert.Equal(new Route("Processes", Server), Assert.Single(recent.Items));
    }

    // ── The banner ───────────────────────────────────────────────────────

    private static FocusBannerText Banner(Route route, string? note = null, bool showsWindow = false, string? historyName = "Live charts") =>
        Invariant(() => FocusBanner.For(route.Focus!, note, showsWindow, historyName, DateTime.Today));

    [Fact]
    public void TheBanner_NamesTheAlertAndItsTime_AndWhatThePageShows()
    {
        var banner = Banner(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Blocking)), "showing the blocking chain of session 87 only");

        Assert.Equal("Opened from alert", banner.Title);
        Assert.Equal("Blocking · critical · since 13:58 — showing the blocking chain of session 87 only", banner.What);
        Assert.Null(banner.Past);
        Assert.Null(banner.HistoryLink);
    }

    [Fact]
    public void APastAlert_OnALivePage_SaysSo_AndOffersTheHistoryPage()
    {
        var banner = Banner(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Cpu, ended: End, severity: HealthLevel.Warning)));

        Assert.Equal("CPU · warning · 13:58–14:05", banner.What);
        Assert.Equal("It ended at 14:05; this page shows the server now.", banner.Past);
        Assert.Equal("Open Live charts for 13:43–14:20", banner.HistoryLink);
    }

    [Fact]
    public void APastAlert_WithoutHistory_SaysThereIsNone()
    {
        var banner = Banner(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.TempDbSpace, ended: End)));

        Assert.Equal("It ended at 14:05; this page shows the server now. SqlVitals keeps no history of this check.", banner.Past);
        Assert.Null(banner.HistoryLink);
    }

    [Fact]
    public void APastAlert_OnAPageThatShowsItsWindow_NeedsNoWarning()
    {
        var history = EvidenceRoutes.History(EvidenceRoutes.ForAlert(AlertOf(HealthIndicator.Cpu, ended: End)))!;

        var banner = Banner(history, "showing 13:43–14:20 from the monitoring history", showsWindow: true);

        Assert.Equal("CPU · critical · 13:58–14:05 — showing 13:43–14:20 from the monitoring history", banner.What);
        Assert.Null(banner.Past);
    }

    [Fact]
    public void TheBanner_ForACheck_OrALink()
    {
        var check = Banner(EvidenceRoutes.ForCheck(HealthIndicator.Blocking, Server, "Blocking 3.5 min (3 blocked)"));
        var link  = Banner(EvidenceRoutes.Link("ActiveWaits", Server, new FocusFilter(FocusKind.Session, 87), "Processes"));

        Assert.Equal(("Opened from health check", "Blocking 3.5 min (3 blocked)"), (check.Title, check.What));
        Assert.Equal(("Opened from Processes", "session 87"), (link.Title, link.What));
    }
}
