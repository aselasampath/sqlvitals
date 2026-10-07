using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The sidebar (#124, #141) as WPF builds it, in each theme: laid out off screen on an STA thread,
/// reading back the template parts rather than trusting the XAML. Its states are also rendered to
/// PNGs under %TEMP%\SqlVitalsTests\sidebar, to look at.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class NavSidebarTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    // Segoe MDL2 Assets' ChevronDown and ChevronRight.
    private const string ChevronDown  = "\uE70D";
    private const string ChevronRight = "\uE76C";

    [Theory, MemberData(nameof(Themes))]
    public void TheGlobalPart_ThenTheServerPart_ThenSettingsAtTheFoot(string theme) => OnSta(theme, (sidebar, panel, footer) =>
    {
        var children = panel.Children.Cast<UIElement>().ToList();
        Assert.Equal(["Home", "Alerts", "Export"], children.Take(3).Select(c => (string)((Button)c).Tag));
        Assert.Same(sidebar.ServerHeader, children[3]);
        Assert.Same(sidebar.NoServer, children[4]);

        var groups = children.Skip(5).Cast<Expander>().ToList();
        Assert.Equal(NavMenu.Groups.Select(g => g.Name), groups.Select(g => (string)g.Header));
        foreach (var (group, expander) in NavMenu.Groups.Zip(groups))
            Assert.Equal(group.Pages.Select(p => p.Tag),
                         ((Panel)expander.Content).Children.OfType<Button>().Select(b => (string)b.Tag));

        Assert.Equal(["Settings"], footer.Children.OfType<Button>().Select(b => (string)b.Tag));
        Assert.All(NavMenu.AllPages, p => Assert.Equal(p.Label, AutomationProperties.GetName(sidebar.Button(p.Tag))));
        Assert.Equal("All servers", AutomationProperties.GetName(sidebar.Button("Home")));
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheGlobalPages_HaveFluentIcons_AGroupsPagesAreIndentedUnderIt(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        panel.UpdateLayout();
        var icon = UiThread.Find<TextBlock>(sidebar.Button("Alerts")).First(t => t.FontFamily.Source == NavMenu.IconFont);
        Assert.Equal("\uEA8F", icon.Text);
        Assert.Equal(NavMenu.IconFont, icon.FontFamily.Source);
        Assert.Equal("\uE80A", NavGroupState.GetIcon(sidebar.Group("Overview")));
        Assert.Equal("\uE80A", Part<TextBlock>(sidebar.Group("Overview"), "HeaderIcon").Text);

        // No emoji left anywhere.
        Assert.DoesNotContain(UiThread.Find<TextBlock>(sidebar.Button("Settings")), t => t.Text.Any(char.IsSurrogate));
        Assert.Equal(42, sidebar.Button("TempDb").Padding.Left);
    });

    [Theory, MemberData(nameof(Themes))]
    public void WithNoServerOpen_TheServerPartSaysHowToOpenOne(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        var requested = new List<string>();
        sidebar.PageRequested += requested.Add;
        sidebar.ShowServer(null);
        sidebar.Highlight("Home");
        panel.UpdateLayout();

        Assert.Equal(Visibility.Collapsed, sidebar.ServerHeader.Visibility);
        Assert.Equal(Visibility.Visible, sidebar.NoServer.Visibility);
        Assert.Equal("Open a server from All servers", string.Concat(sidebar.NoServer.Inlines.Select(i => i switch
        {
            Run run        => run.Text,
            Hyperlink link => string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text)),
            _              => "",
        })));
        Assert.All(NavMenu.Groups, g => Assert.Equal(Visibility.Collapsed, sidebar.Group(g.Id).Visibility));

        // "All servers" in it is a link to Home.
        var link = sidebar.NoServer.Inlines.OfType<Hyperlink>().Single();
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(["Home"], requested);

        Render(panel, theme, "no-server");
    });

    [Theory, MemberData(nameof(Themes))]
    public void WithAServerOpen_ItsHeaderAndItsGroupsShow(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        var toggled = 0;
        sidebar.MonitorToggleRequested += () => toggled++;
        sidebar.ShowServer(Server("PROD-SQL01"));
        sidebar.Highlight("Processes");
        panel.UpdateLayout();

        Assert.Equal(Visibility.Visible, sidebar.ServerHeader.Visibility);
        Assert.Equal(Visibility.Collapsed, sidebar.NoServer.Visibility);
        Assert.Equal("PROD-SQL01", sidebar.ServerName.Text);
        Assert.Equal("prod · SalesDb", sidebar.ServerLine.Text);
        Assert.Equal("Server PROD-SQL01", AutomationProperties.GetName(sidebar.ServerHeader));
        Assert.All(NavMenu.Groups, g => Assert.Equal(Visibility.Visible, sidebar.Group(g.Id).Visibility));

        // Not monitored: no score yet, and the button starts it.
        Assert.Equal(Visibility.Collapsed, sidebar.ScoreButton.Visibility);
        Assert.Equal("Start monitoring PROD-SQL01", AutomationProperties.GetName(sidebar.MonitorButton));
        Assert.Equal("\uE768", sidebar.MonitorButton.Content);
        sidebar.MonitorButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, toggled);

        Render(panel, theme, "server");
    });

    [Theory, MemberData(nameof(Themes))]
    public void OneGroupIsOpen_TheCurrentPages_AndOpeningAnotherClosesIt(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        sidebar.Highlight("TempDb");
        panel.UpdateLayout();

        Assert.Same(panel.FindResource("NavButtonActive"), sidebar.Button("TempDb").Style);
        Assert.Same(panel.FindResource("NavButton"), sidebar.Button("LiveMetrics").Style);
        Assert.Equal(["Resources"], OpenGroups(sidebar));
        Assert.Equal(ChevronDown, Part<TextBlock>(sidebar.Group("Resources"), "Chevron").Text);

        // Clicking another group's header opens it and closes the current page's, which then says it
        // holds the page on screen.
        ((IToggleProvider)new ToggleButtonAutomationPeer(Part<ToggleButton>(sidebar.Group("Queries"), "HeaderToggle"))).Toggle();
        panel.UpdateLayout();
        Assert.Equal(["Queries"], OpenGroups(sidebar));
        var resources = sidebar.Group("Resources");
        Assert.Equal(ChevronRight, Part<TextBlock>(resources, "Chevron").Text);
        Assert.Equal(Visibility.Visible, Part<TextBlock>(resources, "CurrentMark").Visibility);
        var accent = theme == "DarkTheme" ? "AccentLight" : "Accent";
        Assert.Equal(Color(panel, accent), ((SolidColorBrush)Part<TextBlock>(resources, "HeaderText").Foreground).Color);

        // Opening a page in another group moves the open group with it.
        sidebar.Highlight("Backups");
        Assert.Equal(["Maintenance"], OpenGroups(sidebar));

        // A global page leaves the groups as they are.
        sidebar.Highlight("Alerts");
        Assert.Same(panel.FindResource("NavButtonActive"), sidebar.Button("Alerts").Style);
        Assert.DoesNotContain(NavMenu.Groups, g => NavGroupState.GetHoldsCurrentPage(sidebar.Group(g.Id)));
    });

    [Theory, MemberData(nameof(Themes))]
    public void EnterAndSpace_OnTheFocusedGroup_ToggleIt(string theme) => OnSta(theme, (sidebar, _, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        var group = sidebar.Group("Resources");

        Assert.True(group.Focusable);
        Assert.False(Part<ToggleButton>(group, "HeaderToggle").Focusable);

        Assert.True(PressKey(group, Key.Enter));
        Assert.True(group.IsExpanded);
        Assert.True(PressKey(group, Key.Space));
        Assert.False(group.IsExpanded);
        Assert.False(PressKey(group, Key.Down));
        // A key pressed on one of its pages is the page's, and bubbles up without toggling the group.
        PressKey(sidebar.Button("TempDb"), Key.Enter);
        Assert.False(group.IsExpanded);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheGroup_IsAnnouncedAsExpandable_WithItsQuestionAsTheHeadersTooltip(string theme) => OnSta(theme, (sidebar, _, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        var group = sidebar.Group("Activity");
        var peer  = UIElementAutomationPeer.CreatePeerForElement(group);
        var state = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);

        Assert.Equal("Activity now", peer.GetName());
        Assert.Equal(ExpandCollapseState.Collapsed, state.ExpandCollapseState);
        group.IsExpanded = true;
        Assert.Equal(ExpandCollapseState.Expanded, state.ExpandCollapseState);

        Assert.Equal("Who is connected, what is running, and who is blocking whom?",
                     Part<ToggleButton>(group, "HeaderToggle").ToolTip);
        Assert.Null(group.ToolTip);
        // A page's own tooltip says what it shows; the quick switcher finds it by those words (#144).
        Assert.Equal(NavMenu.AllPages.Single(p => p.Tag == "Processes").ToolTip, sidebar.Button("Processes").ToolTip);
    });

    [Theory, MemberData(nameof(Themes))]
    public void APageTheServerLacks_IsDisabled_AndSaysSoInText(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("Azure-DB"));
        sidebar.Highlight("IndexHealth");
        var backups = sidebar.Button("Backups");
        backups.IsEnabled = false;
        panel.UpdateLayout();

        var notAvailable = (FrameworkElement)backups.Template.FindName("NotAvailable", backups);
        Assert.Equal(Visibility.Visible, notAvailable.Visibility);
        Assert.Equal(Color(panel, "TextDisabled"), ((SolidColorBrush)backups.Foreground).Color);
        // The group stays: the server has its other pages.
        Assert.True(sidebar.Group("Maintenance").ActualHeight > backups.ActualHeight);

        Render(panel, theme, "disabled");
    });

    [Theory, MemberData(nameof(Themes))]
    public void Alerts_ShowTheActiveCount(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.SetCount("Alerts", 2, "2 active alerts");
        panel.UpdateLayout();

        var badge = UiThread.Find<Border>(sidebar.Button("Alerts")).Single(b => b.Child is TextBlock { Text: "2" });
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal("Alerts (2 active)", AutomationProperties.GetName(sidebar.Button("Alerts")));
        Assert.Equal("2 active alerts", sidebar.Button("Alerts").ToolTip);

        sidebar.SetCount("Alerts", 0, NavMenu.AlertsToolTip);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        Assert.Equal("Alerts", AutomationProperties.GetName(sidebar.Button("Alerts")));
    });

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ConnectionSelectorItem Server(string name) =>
        new(new ConnectionSettings { Server = "prod", Name = name, Database = "SalesDb" });

    private static IEnumerable<string> OpenGroups(NavSidebar sidebar) =>
        NavMenu.Groups.Where(g => sidebar.Group(g.Id).IsExpanded).Select(g => g.Id);

    private static T Part<T>(Expander expander, string name) where T : FrameworkElement
    {
        expander.ApplyTemplate();
        return (T)expander.Template.FindName(name, expander);
    }

    private static Color Color(FrameworkElement element, string key) =>
        ((SolidColorBrush)element.FindResource(key)).Color;

    // True when the key press was handled.
    private static bool PressKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static void Render(FrameworkElement panel, string theme, string state)
    {
        var host = (FrameworkElement)panel.Parent;
        host.Measure(new Size(190, 900));
        host.Arrange(new Rect(0, 0, 190, 900));
        host.UpdateLayout();
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "sidebar");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap(190, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{state}-{theme}.png"));
        encoder.Save(file);
    }

    // The sidebar in a 190 px wide column, with the theme's resources, laid out off screen.
    private static void OnSta(string theme, Action<NavSidebar, StackPanel, StackPanel> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                var panel  = new StackPanel();
                var footer = new StackPanel();
                DockPanel.SetDock(footer, Dock.Bottom);
                var host = new DockPanel { Resources = resources, Background = (Brush)resources["BgSidebar"], LastChildFill = false };
                host.Children.Add(footer);
                host.Children.Add(panel);
                DockPanel.SetDock(panel, Dock.Top);
                var sidebar = new NavSidebar(panel, footer);

                host.Measure(new Size(190, 900));
                host.Arrange(new Rect(0, 0, 190, 900));
                host.UpdateLayout();

                test(sidebar, panel, footer);
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

    // A key event needs a source; nothing is on screen.
    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
