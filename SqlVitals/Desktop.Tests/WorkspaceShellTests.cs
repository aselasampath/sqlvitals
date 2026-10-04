using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The window's layout with the sidebar pinned, collapsed or hidden (#142), in each theme: the shell
/// with the top bar and a page, laid out off screen at the default 1320 × 820 on an STA thread.
/// Each state is also rendered to a PNG under %TEMP%\SqlVitalsTests\sidebar-states, to look at.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class WorkspaceShellTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private const double WindowWidth  = 1320;
    private const double WindowHeight = 820;

    [Theory, MemberData(nameof(Themes))]
    public void Pinned_Is232Wide_WithLabelsAndTheOpenGroupsPages(string theme) => OnSta(theme, (shell, host, bar, page) =>
    {
        Assert.Equal(SidebarMode.Pinned, shell.Shown);
        Assert.Equal(232, shell.ColumnDefinitions[0].ActualWidth);
        Assert.Equal(1088, page.ActualWidth);
        Assert.True(shell.Menu.Button("Processes").ActualHeight > 0);
        Assert.Equal(Visibility.Collapsed, Rail(shell).Visibility);

        // The pin beside the name collapses it.
        Assert.Equal("Collapse the menu to icons", AutomationProperties.GetName(shell.PinButton));
        Assert.Equal("Collapse the menu", AutomationProperties.GetName(bar.BtnSidebar));
        Render(host, theme, "1-pinned");

        Click(shell.PinButton);
        Assert.Equal(SidebarMode.Collapsed, shell.Shown);
    });

    [Theory, MemberData(nameof(Themes))]
    public void Collapsed_Is56Wide_AnIconPerGlobalPageAndGroup_EachNamed_WithTheCountAndScore(string theme) => OnSta(theme, (shell, host, bar, page) =>
    {
        shell.Cycle();
        Layout(host);

        Assert.Equal(SidebarMode.Collapsed, shell.Shown);
        Assert.Equal(56, shell.ColumnDefinitions[0].ActualWidth);
        Assert.Equal(1264, page.ActualWidth);
        Assert.Equal(Visibility.Visible, Rail(shell).Visibility);

        // An icon per global page and per group, each with a tooltip and a spoken name.
        var icons = NavMenu.Global.Select(p => shell.Rail.Page(p.Tag))
                    .Concat(NavMenu.Groups.Select(g => shell.Rail.Group(g.Id)))
                    .Concat(NavMenu.Footer.Select(p => shell.Rail.Page(p.Tag)))
                    .Append(shell.Rail.PinButton).ToList();
        Assert.All(icons, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(i)));
            Assert.NotNull(i.ToolTip);
            Assert.True(i.ActualWidth <= 40 && i.ActualWidth > 0, $"{AutomationProperties.GetName(i)} is {i.ActualWidth} wide");
        });
        Assert.Equal("Activity now: Who is connected, what is running, and who is blocking whom?", shell.Rail.Group("Activity").ToolTip);

        // The alert count stays on Alerts' icon; the server's dot and score under the global pages.
        var badge = UiThread.Find<Border>(shell.Rail.Page("Alerts")).Single(b => b.Child is TextBlock { Text: "3" });
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal("Alerts (3 active)", AutomationProperties.GetName(shell.Rail.Page("Alerts")));
        Assert.Equal(Visibility.Visible, shell.Rail.ServerMark.Visibility);
        Assert.Equal("84", shell.Rail.ServerScore.Text);
        Assert.Equal("Server PROD-SQL01, health score 84: show what lowered it", AutomationProperties.GetName(shell.Rail.ServerMark));
        FrameworkElement? scoreAnchor = null;
        shell.ScoreRequested += anchor => scoreAnchor = anchor;
        Click(shell.Rail.ServerMark);
        Assert.Same(shell.Rail.ServerMark, scoreAnchor);

        // The page on screen's group is highlighted.
        Assert.Same(host.FindResource("NavRailButtonActive"), shell.Rail.Group("Activity").Style);
        Assert.Same(host.FindResource("NavRailButton"), shell.Rail.Group("Waits").Style);

        Assert.Equal("Hide the menu", AutomationProperties.GetName(bar.BtnSidebar));
        Render(host, theme, "2-collapsed");
    });

    [Theory, MemberData(nameof(Themes))]
    public void Collapsed_AGroupsFlyout_HasItsNameQuestionPagesAndPin_AndClosesWhenAPageIsPicked(string theme) => OnSta(theme, (shell, host, _, _) =>
    {
        var picked = new List<string>();
        shell.PageRequested += picked.Add;
        shell.Menu.Button("Backups").IsEnabled = false;   // as on Azure SQL Database
        shell.Menu.Button("Backups").ToolTip   = "Not available on Azure SQL Database";
        shell.Choose(SidebarMode.Collapsed);
        Layout(host);

        Click(shell.Rail.Group("Maintenance"));
        Layout(host);
        var flyout = shell.Flyout!;
        Assert.Equal("Maintenance", flyout.GroupId);
        var texts = UiThread.Find<TextBlock>(flyout).Select(t => t.Text).ToList();
        Assert.Contains("Maintenance", texts);
        Assert.Contains("Are indexes, statistics, backups and jobs looked after?", texts);
        Assert.Equal(["IndexHealth", "StaleStats", "Backups", "AgentJobs"], flyout.Pages.Select(p => (string)p.Tag));
        Assert.Equal("Pin the menu open", AutomationProperties.GetName(flyout.PinButton));
        // A page the server lacks is the same as in the pinned sidebar.
        var backups = flyout.Pages.Single(p => (string)p.Tag == "Backups");
        Assert.False(backups.IsEnabled);
        Assert.Equal("Not available on Azure SQL Database", backups.ToolTip);
        // Beside the collapsed sidebar, level with its icon, inside the window.
        Assert.Equal(62, Canvas.GetLeft(flyout));
        Assert.InRange(Canvas.GetTop(flyout), 8, WindowHeight - flyout.ActualHeight - 8);
        Render(host, theme, "3-flyout");

        // Clicking its icon again closes it.
        Click(shell.Rail.Group("Maintenance"));
        Assert.Null(shell.Flyout);

        Click(shell.Rail.Group("Queries"));
        Click(shell.Flyout!.Pages.Single(p => (string)p.Tag == "QueryStore"));
        Assert.Equal(["QueryStore"], picked);
        Assert.Null(shell.Flyout);
        Assert.Equal(SidebarMode.Collapsed, shell.Shown);
    });

    [Theory, MemberData(nameof(Themes))]
    public void Collapsed_AFlyout_OpensAfterThePointerRests_AndClosesOnEsc_AClickOutside_OrThePointerLeaving(string theme) => OnSta(theme, (shell, host, _, page) =>
    {
        shell.Choose(SidebarMode.Collapsed);
        Layout(host);
        var waits = shell.Rail.Group("Waits");

        // Resting on the icon opens it after the delay, and not before.
        Mouse(waits, UIElement.MouseEnterEvent);
        Assert.Null(shell.Flyout);
        Assert.Equal(TimeSpan.FromMilliseconds(300), WorkspaceShell.HoverDelay);
        shell.ElapseTimers();
        Assert.Equal("Waits", shell.Flyout?.GroupId);

        // Moving off it into the fly-out keeps it; leaving both closes it.
        Mouse(waits, UIElement.MouseLeaveEvent);
        Mouse(shell.Flyout!, UIElement.MouseEnterEvent);
        shell.ElapseTimers();
        Assert.NotNull(shell.Flyout);
        Mouse(shell.Flyout!, UIElement.MouseLeaveEvent);
        shell.ElapseTimers();
        Assert.Null(shell.Flyout);

        // Resting on a group then moving off before the delay opens nothing.
        Mouse(waits, UIElement.MouseEnterEvent);
        Mouse(waits, UIElement.MouseLeaveEvent);
        shell.ElapseTimers();
        Assert.Null(shell.Flyout);

        // Esc closes it.
        Click(waits);
        PressKey(page, Key.Escape, preview: true);
        Assert.Null(shell.Flyout);

        // A click outside closes it, and still reaches the page.
        Click(waits);
        var reached = false;
        page.PreviewMouseDown += (_, _) => reached = true;
        Press(page);
        Assert.Null(shell.Flyout);
        Assert.True(reached);

        // A click in it keeps it when the pointer leaves.
        Mouse(waits, UIElement.MouseEnterEvent);
        shell.ElapseTimers();
        Press(shell.Flyout!.Pages[0]);
        Mouse(shell.Flyout!, UIElement.MouseLeaveEvent);
        shell.ElapseTimers();
        Assert.NotNull(shell.Flyout);
    });

    [Theory, MemberData(nameof(Themes))]
    public void Collapsed_TheKeyboard_OpensAFlyoutWithEnterOrRight_ArrowsMove_AndEscGoesBackToTheIcon(string theme) => OnSta(theme, (shell, host, _, _) =>
    {
        shell.Choose(SidebarMode.Collapsed);
        Layout(host);
        var resources = shell.Rail.Group("Resources");

        Assert.True(PressKey(resources, Key.Right));
        var flyout = shell.Flyout!;
        Assert.Equal("Resources", flyout.GroupId);
        Assert.Same(flyout.Pages[0], shell.FocusMovedTo);

        // ↑ and ↓ go round its pages and the pin; Tab stays in it.
        var body = (Panel)flyout.Child;
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(body));
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(body));

        // Esc, or ←, closes it and goes back to the group's icon.
        PressKey(flyout.Pages[1], Key.Left, preview: true);
        Assert.Null(shell.Flyout);
        Assert.Same(resources, shell.FocusMovedTo);

        // The page on screen is where the keyboard goes in, if it is in the group.
        shell.Highlight("TempDb");
        PressKey(resources, Key.Right);
        Assert.Equal("TempDb", (string)((Button)shell.FocusMovedTo!).Tag);
        PressKey(shell.Flyout!.Pages[0], Key.Escape, preview: true);
        Assert.Same(resources, shell.FocusMovedTo);

        // In the rail, ↑ and ↓ go from icon to icon.
        var rail = (Panel)resources.Parent;
        Assert.Equal(KeyboardNavigationMode.Contained, KeyboardNavigation.GetDirectionalNavigation(rail));
    });

    [Theory, MemberData(nameof(Themes))]
    public void Collapsed_PinTheMenuOpen_PinsIt_AndIsSaved(string theme) => OnSta(theme, (shell, host, _, _) =>
    {
        var chosen = new List<SidebarMode>();
        shell.ModeChosen += chosen.Add;
        shell.Choose(SidebarMode.Collapsed);
        Click(shell.Rail.Group("Waits"));
        Click(shell.Flyout!.PinButton);

        Assert.Equal(SidebarMode.Pinned, shell.Shown);
        Assert.Null(shell.Flyout);
        Assert.Equal([SidebarMode.Collapsed, SidebarMode.Pinned], chosen);

        shell.Choose(SidebarMode.Collapsed);
        Click(shell.Rail.PinButton);
        Assert.Equal(SidebarMode.Pinned, shell.Shown);
    });

    [Theory, MemberData(nameof(Themes))]
    public void Hidden_GivesThePageTheWholeWidth_AndThe_BarsButtonSlidesItInOverThePage(string theme) => OnSta(theme, (shell, host, bar, page) =>
    {
        shell.Choose(SidebarMode.Hidden);
        bar.ShowSidebar(shell.Shown, shell.IsSlidOver, alerts: 3);
        Layout(host);

        Assert.Equal(0, shell.ColumnDefinitions[0].ActualWidth);
        Assert.Equal(WindowWidth, page.ActualWidth);
        Assert.Equal(Visibility.Collapsed, Rail(shell).Visibility);
        Assert.Equal(Visibility.Collapsed, Pinned(shell).Visibility);
        // The alert count is on ☰ now; the server's score is in the breadcrumb.
        Assert.Equal(Visibility.Visible, bar.SidebarBadge.Visibility);
        Render(host, theme, "4-hidden");

        shell.ToggleFromBar();
        Layout(host);
        Assert.True(shell.IsSlidOver);
        Assert.Equal(SidebarMode.Hidden, shell.Shown);
        // Over the page, which keeps its width.
        Assert.Equal(WindowWidth, page.ActualWidth);
        Assert.Equal(232, Pinned(shell).ActualWidth);
        Assert.True(Panel.GetZIndex(Pinned(shell)) > Panel.GetZIndex(page));
        Assert.Equal("Pin the menu open", AutomationProperties.GetName(shell.PinButton));
        Render(host, theme, "5-slid-over");

        // The keyboard goes in and Tab stays in it until it closes.
        Assert.Same(shell.Menu.Button("Home"), shell.FocusMovedTo);
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(Pinned(shell)));

        // Picking a page closes it.
        var picked = new List<string>();
        shell.PageRequested += picked.Add;
        Click(shell.Menu.Button("Alerts"));
        Assert.Equal(["Alerts"], picked);
        Assert.False(shell.IsSlidOver);
        Assert.Equal(Visibility.Collapsed, Pinned(shell).Visibility);
        Assert.Equal(KeyboardNavigationMode.Continue, KeyboardNavigation.GetTabNavigation(Pinned(shell)));
    });

    [Theory, MemberData(nameof(Themes))]
    public void Hidden_TheSlidInMenu_ClosesOnEscOrAClickOutside_AndGivesTheFocusBackToTheBarsButton(string theme) => OnSta(theme, (shell, host, bar, page) =>
    {
        shell.Choose(SidebarMode.Hidden);
        Layout(host);

        shell.ToggleFromBar();
        PressKey(shell.Menu.Button("Home"), Key.Escape, preview: true);
        Assert.False(shell.IsSlidOver);
        Assert.Same(bar.BtnSidebar, shell.FocusMovedTo);

        shell.ToggleFromBar();
        Press(page);
        Assert.False(shell.IsSlidOver);

        // A click in it doesn't close it.
        shell.ToggleFromBar();
        Press(shell.Menu.Button("Export"));
        Assert.True(shell.IsSlidOver);

        // Its pin pins it, and that is what is saved.
        var chosen = new List<SidebarMode>();
        shell.ModeChosen += chosen.Add;
        Click(shell.PinButton);
        Assert.Equal(SidebarMode.Pinned, shell.Shown);
        Assert.False(shell.IsSlidOver);
        Assert.Equal([SidebarMode.Pinned], chosen);
        Assert.True(double.IsNaN(Pinned(shell).Width));
        Assert.Equal(1, Grid.GetColumnSpan(Pinned(shell)));
    });

    [Theory, MemberData(nameof(Themes))]
    public void Hidden_PointingAtTheLeftEdge_SlidesItIn_AndItGoesWhenThePointerLeaves(string theme) => OnSta(theme, (shell, host, _, _) =>
    {
        shell.Choose(SidebarMode.Hidden);
        Layout(host);
        var edge = Edge(shell);
        Assert.Equal(Visibility.Visible, edge.Visibility);
        Assert.Equal(6, edge.ActualWidth);

        Mouse(edge, UIElement.MouseEnterEvent);
        Assert.False(shell.IsSlidOver);
        shell.ElapseTimers();
        Assert.True(shell.IsSlidOver);
        // The keyboard stays where it was: the pointer opened it.
        Assert.Null(shell.FocusMovedTo);

        Mouse(Pinned(shell), UIElement.MouseEnterEvent);
        Mouse(Pinned(shell), UIElement.MouseLeaveEvent);
        shell.ElapseTimers();
        Assert.False(shell.IsSlidOver);
    });

    [Theory, MemberData(nameof(Themes))]
    public void CtrlB_AndTheBarsButton_GoPinnedCollapsedHidden_AndCtrlBPinsAgain(string theme) => OnSta(theme, (shell, host, _, _) =>
    {
        var chosen = new List<SidebarMode>();
        shell.ModeChosen += chosen.Add;

        shell.Cycle();
        shell.ToggleFromBar();
        Assert.Equal(SidebarMode.Hidden, shell.Shown);
        // In Hidden, ☰ shows it over the page; Ctrl+B pins it.
        shell.ToggleFromBar();
        Assert.True(shell.IsSlidOver);
        shell.Cycle();
        Assert.Equal(SidebarMode.Pinned, shell.Shown);
        Assert.False(shell.IsSlidOver);

        Assert.Equal([SidebarMode.Collapsed, SidebarMode.Hidden, SidebarMode.Pinned], chosen);
    });

    [Theory, MemberData(nameof(Themes))]
    public void ANarrowWindow_ShowsItCollapsed_WithoutSavingThat_AndWideningRestoresIt(string theme) => OnSta(theme, (shell, host, _, page) =>
    {
        var chosen = new List<SidebarMode>();
        shell.ModeChosen += chosen.Add;

        shell.SetWindowWidth(1200);
        Layout(host, 1200);
        Assert.Equal(SidebarMode.Collapsed, shell.Shown);
        Assert.Equal(SidebarMode.Pinned, shell.Saved);
        Assert.Equal(1144, page.ActualWidth);
        Render(host, theme, "6-narrow", 1200);

        shell.SetWindowWidth(1320);
        Layout(host);
        Assert.Equal(SidebarMode.Pinned, shell.Shown);
        Assert.Empty(chosen);

        // Restored at start-up in a narrow window: collapsed, and pinned again once wide.
        shell.Restore(SidebarMode.Pinned, 1000);
        Assert.Equal(SidebarMode.Collapsed, shell.Shown);
        shell.SetWindowWidth(1300);
        Assert.Equal(SidebarMode.Pinned, shell.Shown);
    });

    [Theory, MemberData(nameof(Themes))]
    public void ChangingState_ClosesAFlyout(string theme) => OnSta(theme, (shell, host, bar, _) =>
    {
        shell.Choose(SidebarMode.Collapsed);
        PressKey(shell.Rail.Group("Waits"), Key.Right);
        Assert.NotNull(shell.Flyout);
        shell.Cycle();
        Assert.Null(shell.Flyout);
        Assert.Equal(SidebarMode.Hidden, shell.Shown);
    });

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Border Pinned(WorkspaceShell shell) => (Border)shell.Children[0];
    private static Border Rail(WorkspaceShell shell)   => (Border)shell.Children[1];
    private static Border Edge(WorkspaceShell shell)   => (Border)shell.Children[2];

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Mouse(UIElement target, RoutedEvent routedEvent) =>
        target.RaiseEvent(new MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });

    // A mouse button pressed on an element, as the window would route it: tunnelling down to it.
    private static void Press(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });

    // True when the key press was handled.
    private static bool PressKey(UIElement target, Key key, bool preview = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key)
        {
            RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent,
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static void Layout(FrameworkElement host, double width = WindowWidth)
    {
        host.Measure(new Size(width, WindowHeight));
        host.Arrange(new Rect(0, 0, width, WindowHeight));
        host.UpdateLayout();
    }

    private static void Render(FrameworkElement host, string theme, string state, double width = WindowWidth)
    {
        Layout(host, width);
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "sidebar-states");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)width, (int)WindowHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{state}-{theme}.png"));
        encoder.Save(file);
    }

    // PROD-SQL01, monitored with a score of 84 and 3 active alerts, on its Processes page.
    private static ConnectionSelectorItem Server()
    {
        var item = new ConnectionSelectorItem(new ConnectionSettings { Server = "prod", Name = "PROD-SQL01", Database = "SalesDb" });
        void Set(string name, object value) => typeof(ConnectionSelectorItem).GetProperty(name)!.SetValue(item, value);
        Set(nameof(ConnectionSelectorItem.Score), new HealthScore(84, HealthLevel.Warning, [], DateTime.Now));
        Set(nameof(ConnectionSelectorItem.HealthFill), HealthBrushes.For(HealthLevel.Warning));
        Set(nameof(ConnectionSelectorItem.HealthStroke), HealthBrushes.For(HealthLevel.Warning));
        Set(nameof(ConnectionSelectorItem.HealthText), "Monitoring · Warning");
        Set(nameof(ConnectionSelectorItem.IsMonitoring), true);
        return item;
    }

    // The shell with the top bar and a page standing in for a grid, as MainWindow lays them out.
    private static void OnSta(string theme, Action<WorkspaceShell, FrameworkElement, TopBar, FrameworkElement> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                var shell = new WorkspaceShell { Animate = false };
                var bar   = new TopBar();
                var page  = new Border { Background = (Brush)resources["BgDeep"], Child = PageStandIn(resources) };
                var content = new DockPanel();
                DockPanel.SetDock(bar, Dock.Top);
                content.Children.Add(bar);
                content.Children.Add(page);
                Grid.SetColumn(content, 1);
                shell.Children.Add(content);
                var host = new Border { Resources = resources, Child = shell, Background = (Brush)resources["BgDeep"] };

                var server = Server();
                var route  = new Route("Processes", ConnectionId: server.Settings.Id);
                bar.ShowBreadcrumb(Breadcrumb.For(route, _ => "PROD-SQL01"), _ => server, [server]);
                bar.ShowPage(null, new PageFreshness(PageKind.Live, TimeSpan.FromSeconds(10), false, AsOf: DateTime.Now), canPause: true, database: null);
                shell.ReturnFocusTo = bar.BtnSidebar;
                shell.ModeShown += () => bar.ShowSidebar(shell.Shown, shell.IsSlidOver, 3);
                shell.Restore(SidebarMode.Pinned, WindowWidth);
                shell.ShowServer(server);
                shell.Highlight("Processes");
                shell.SetCount("Alerts", 3, "3 active alerts");
                shell.VersionText.Text = "v0.58.0";
                Layout(host);

                test(shell, host, bar, page);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    // Rows of text, so the renders show how much of the page each state leaves.
    private static FrameworkElement PageStandIn(ResourceDictionary resources)
    {
        var rows = new StackPanel { Margin = new Thickness(20) };
        rows.Children.Add(new TextBlock { Text = "Processes", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (Brush)resources["TextPrimary"], Margin = new Thickness(0, 0, 0, 12) });
        for (var i = 0; i < 18; i++)
            rows.Children.Add(new Border
            {
                Height = 28, Margin = new Thickness(0, 0, 0, 2), Background = (Brush)resources[i % 2 == 0 ? "BgCard" : "BgDeep"],
                Child = new TextBlock
                {
                    Text = $"  {51 + i}   sa   SalesDb   running   SELECT o.OrderId, o.CustomerId, l.ProductId, l.Quantity FROM dbo.Orders o JOIN dbo.OrderLines l ON …",
                    Foreground = (Brush)resources["TextMuted"], VerticalAlignment = VerticalAlignment.Center,
                },
            });
        return rows;
    }

    // A key event needs a source; nothing is on screen.
    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
