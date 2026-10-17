using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Shell;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The shell's parts on their own (#191): the sidebar's states (#142), the top bar's words and
/// commands (#140), and the quick switcher's search and selection (#144). No window, no control.
/// </summary>
public sealed class ShellPartsViewModelTests
{
    // ── The sidebar ──────────────────────────────────────────────────────

    [Fact]
    public void TheSidebar_CyclesPinnedCollapsedHidden_AndSavesEachChoice()
    {
        var sidebar = new SidebarViewModel();
        var chosen  = new List<SidebarMode>();
        sidebar.ModeChosen += chosen.Add;
        Assert.Equal(SidebarMode.Pinned, sidebar.Shown);

        sidebar.Cycle();
        Assert.Equal(SidebarMode.Collapsed, sidebar.Shown);
        sidebar.Cycle();
        Assert.Equal(SidebarMode.Hidden, sidebar.Shown);
        sidebar.Cycle();
        Assert.Equal(SidebarMode.Pinned, sidebar.Shown);
        Assert.Equal([SidebarMode.Collapsed, SidebarMode.Hidden, SidebarMode.Pinned], chosen);
    }

    [Fact]
    public void Hidden_TheMenuButtonSlidesItIn_AndOut_WithoutChangingTheState()
    {
        var sidebar = new SidebarViewModel();
        sidebar.Restore(SidebarMode.Hidden, windowWidth: 1320);
        var chosen = 0;
        sidebar.ModeChosen += _ => chosen++;

        sidebar.ToggleFromBar();
        Assert.True(sidebar.IsSlidOver);
        Assert.True(sidebar.SlideByKeyboard);
        sidebar.ToggleFromBar();
        Assert.False(sidebar.IsSlidOver);
        Assert.Equal(SidebarMode.Hidden, sidebar.Shown);
        Assert.Equal(0, chosen);

        // The pointer at the window's edge slides it in without the keyboard; choosing a state slides it out.
        sidebar.SlideIn(keyboard: false);
        Assert.False(sidebar.SlideByKeyboard);
        sidebar.Choose(SidebarMode.Pinned);
        Assert.False(sidebar.IsSlidOver);
        Assert.False(sidebar.SlideByKeyboard);
        Assert.Equal(SidebarMode.Pinned, sidebar.Shown);
    }

    [Fact]
    public void ANarrowWindow_ShowsAPinnedSidebarCollapsed_AndFocusModeHidesIt_WithoutChangingTheSavedState()
    {
        var sidebar = new SidebarViewModel();
        sidebar.Restore(SidebarMode.Pinned, windowWidth: 1100);
        Assert.Equal(SidebarMode.Collapsed, sidebar.Shown);
        Assert.Equal(SidebarMode.Pinned, sidebar.Saved);

        sidebar.SetWindowWidth(1400);
        Assert.Equal(SidebarMode.Pinned, sidebar.Shown);

        sidebar.InFocus = true;
        Assert.Equal(SidebarMode.Hidden, sidebar.Shown);
        // In focus mode Ctrl+B slides it over the page, and back.
        sidebar.Cycle();
        Assert.True(sidebar.IsSlidOver);
        sidebar.Cycle();
        Assert.False(sidebar.IsSlidOver);

        sidebar.InFocus = false;
        Assert.Equal(SidebarMode.Pinned, sidebar.Shown);
        Assert.Equal(SidebarMode.Pinned, sidebar.Saved);
    }

    [Fact]
    public void ThePageOnScreen_OpensItsGroup_AndTheAlertsSayHowMany()
    {
        var sidebar = new SidebarViewModel { Current = "TempDb" };

        Assert.Equal("Resources", sidebar.OpenGroup);
        sidebar.Current = "Alerts";
        Assert.Equal("Resources", sidebar.OpenGroup);   // a page about every server leaves it as it is

        Assert.Equal(NavMenu.AlertsToolTip, sidebar.AlertsToolTip);
        sidebar.AlertCount = 1;
        Assert.Equal("1 active alert", sidebar.AlertsToolTip);
        sidebar.AlertCount = 3;
        Assert.Equal("3 active alerts", sidebar.AlertsToolTip);
    }

    // ── The top bar ──────────────────────────────────────────────────────

    [Fact]
    public void BackAndForward_SayWhereTheyGo_AndCanOnlyBePressedWhenTheyGoSomewhere()
    {
        var bar = new TopBarViewModel();
        var moves = new List<string>();
        bar.BackRequested    += () => moves.Add("Back");
        bar.ForwardRequested += () => moves.Add("Forward");
        Assert.False(bar.BackCommand.CanExecute(null));
        Assert.Equal("Back (Alt+Left)", bar.BackToolTip);

        bar.ShowHistory("Processes · PROD-SQL01", null);
        Assert.True(bar.BackCommand.CanExecute(null));
        Assert.False(bar.ForwardCommand.CanExecute(null));
        Assert.Equal("Back to Processes · PROD-SQL01 (Alt+Left)", bar.BackToolTip);

        bar.BackCommand.Execute(null);
        Assert.Equal(["Back"], moves);
    }

    [Fact]
    public void TheMenuButton_SaysWhatItDoesNext_AndCarriesTheAlertsWhileTheSidebarIsHidden()
    {
        var bar = new TopBarViewModel();

        bar.ShowSidebar(SidebarMode.Pinned, slidOver: false, alerts: 2);
        Assert.Equal("Collapse the menu", bar.SidebarAutomationName);
        Assert.False(bar.SidebarCounts);

        bar.ShowSidebar(SidebarMode.Hidden, slidOver: false, alerts: 120);
        Assert.True(bar.SidebarCounts);
        Assert.Equal("99+", bar.SidebarBadge);
        Assert.Equal("Show the menu (120 active alerts)", bar.SidebarAutomationName);
        Assert.Equal("Show the menu over the page. Ctrl+B pins it open.\n120 active alerts", bar.SidebarToolTip);

        bar.ShowSidebar(SidebarMode.Hidden, slidOver: true, alerts: 0);
        Assert.Equal("Close the menu", bar.SidebarName);
        Assert.False(bar.SidebarCounts);
    }

    [Fact]
    public void ALivePage_OffersPause_APausedOneResumeAndRefresh_AndEveryOtherRefresh()
    {
        var bar = new TopBarViewModel();
        var pauses = new List<bool>();
        bar.PauseRequested += pauses.Add;

        bar.ShowPage(new PageFreshness(PageKind.Live, TimeSpan.FromSeconds(5)), canPause: true, database: null);
        Assert.True(bar.ShowsPause);
        Assert.False(bar.ShowsRefresh);
        bar.PauseCommand.Execute(null);

        bar.ShowContext(new PageFreshness(PageKind.Live, TimeSpan.FromSeconds(5), Paused: true), canPause: true, database: null);
        Assert.True(bar.ShowsRefresh);
        bar.PauseCommand.Execute(null);
        Assert.Equal([true, false], pauses);

        bar.ShowPage(new PageFreshness(PageKind.Snapshot), canPause: false, database: "Sales");
        Assert.False(bar.ShowsPause);
        Assert.True(bar.ShowsRefresh);
        Assert.Equal("Sales", bar.Database);
    }

    [Fact]
    public void ANewPage_ClearsTheMessage_AndRefreshFollowsCanRefresh()
    {
        var bar = new TopBarViewModel();
        bar.ShowMessage("Could not switch: login failed", isError: true);
        Assert.True(bar.MessageIsError);

        bar.ShowPage(null, canPause: false, database: null);
        Assert.Equal(string.Empty, bar.Message);
        Assert.False(bar.MessageIsError);

        Assert.False(bar.RefreshCommand.CanExecute(null));
        bar.CanRefresh = true;
        Assert.True(bar.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void TheServerMenu_SwitchesToAnotherServer_NotTheOpenOne()
    {
        var bar = new TopBarViewModel();
        var open = Guid.NewGuid();
        var other = Guid.NewGuid();
        var switched = new List<Guid>();
        bar.ServerRequested += switched.Add;

        bar.SwitchServer(open, current: open);
        bar.SwitchServer(other, current: open);

        Assert.Equal([other], switched);
    }

    // ── The quick switcher ───────────────────────────────────────────────

    private static readonly Route Waits = new("Waits");

    // Two pages that can be opened around one that can't, whatever is typed.
    private static IReadOnlyList<SwitcherSection> Search(string text) =>
        text.Length == 0
            ? []
            :
            [
                new SwitcherSection(SwitcherGroup.OnServer, "On PROD-SQL01",
                [
                    new SwitcherItem(SwitcherGroup.OnServer, "Processes", "Activity now", new Route("Processes"), ""),
                    new SwitcherItem(SwitcherGroup.OnServer, "Agent Jobs", "Maintenance", new Route("AgentJobs"), "", Unavailable: "No SQL Server Agent"),
                    new SwitcherItem(SwitcherGroup.OnServer, "Waits", "Waits", Waits, ""),
                ]),
            ];

    [Fact]
    public void TheSwitcher_SelectsTheFirstResult_AndTheArrowsGoRoundTheOnesThatCanBeOpened()
    {
        var switcher = new QuickSwitcherViewModel();
        var opened = 0;
        switcher.Opened += () => opened++;

        switcher.Open(Search, _ => null, keepsPage: true);
        Assert.True(switcher.IsOpen);
        Assert.Equal(1, opened);
        Assert.Empty(switcher.Sections);
        Assert.Null(switcher.Selected);
        Assert.StartsWith("The pages you open show here", switcher.EmptyText, StringComparison.Ordinal);

        switcher.Query = "a";
        Assert.Equal(["Processes", "Waits"], switcher.Openable.Select(i => i.Text));
        Assert.Equal("Processes", switcher.Selected!.Text);
        switcher.Move(1);
        Assert.Equal("Waits", switcher.Selected!.Text);
        switcher.Move(1);
        Assert.Equal("Processes", switcher.Selected!.Text);
        switcher.Move(-1);
        Assert.Equal("Waits", switcher.Selected!.Text);
    }

    [Fact]
    public void Picking_ClosesIt_AndOpensTheRoute_ButOneThatCantBeOpenedStaysOpen()
    {
        var switcher = new QuickSwitcherViewModel();
        var routes = new List<(Route Route, bool IntoPage)>();
        switcher.OpenRequested += (route, intoPage) => routes.Add((route, intoPage));
        switcher.Open(Search, _ => null, keepsPage: false);
        switcher.Query = "a";

        Assert.False(switcher.Pick(switcher.Sections[0].Items[1], intoPage: true));
        Assert.True(switcher.IsOpen);

        Assert.True(switcher.Pick(switcher.Openable[1], intoPage: true));
        Assert.False(switcher.IsOpen);
        Assert.Equal([(Waits, true)], routes);
    }

    [Fact]
    public void OpeningAgain_StartsEmpty()
    {
        var switcher = new QuickSwitcherViewModel();
        switcher.Open(Search, _ => null, keepsPage: false);
        switcher.Query = "wai";
        switcher.Close();

        switcher.Open(Search, _ => null, keepsPage: false);

        Assert.Equal(string.Empty, switcher.Query);
        Assert.Empty(switcher.Sections);
        Assert.Equal("No server, page or setting matches “x”.", Typed(switcher, "x"));
    }

    private static string Typed(QuickSwitcherViewModel switcher, string text)
    {
        switcher.Query = text;
        return switcher.EmptyText;
    }
}
