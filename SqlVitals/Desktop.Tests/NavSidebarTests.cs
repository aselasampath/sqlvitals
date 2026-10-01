using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The sidebar's groups (#124) as WPF builds them, in each theme: laid out off screen on an STA
/// thread, reading back the template parts rather than trusting the XAML.
/// </summary>
public sealed class NavSidebarTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    // Segoe MDL2 Assets' ChevronDown and ChevronRight.
    private const string ChevronDown  = "";
    private const string ChevronRight = "";

    [Theory, MemberData(nameof(Themes))]
    public void EveryPage_HasAButton_InItsGroup_InOrder(string theme) => OnSta(theme, [], (sidebar, panel, _) =>
    {
        var groups = panel.Children.OfType<Expander>().ToList();
        Assert.Equal(NavMenu.Groups.Select(g => g.Name.ToUpperInvariant()), groups.Select(g => (string)g.Header));

        foreach (var (group, expander) in NavMenu.Groups.Zip(groups))
            Assert.Equal(group.Pages.Select(p => p.Tag),
                         ((Panel)expander.Content).Children.OfType<Button>().Select(b => (string)b.Tag));

        // Home above the groups (#130); Export / AI and Settings below them, each after a separator, as before.
        Assert.Equal("Home", (string)((Button)panel.Children[0]).Tag);
        Assert.Equal(["Separator", "Export", "Separator", "Settings"],
                     panel.Children.Cast<UIElement>().Skip(NavMenu.Header.Count + groups.Count)
                          .Select(e => e is Button b ? (string)b.Tag : e.GetType().Name));
        Assert.All(NavMenu.AllPages, p => Assert.Equal(p.Label, AutomationProperties.GetName(sidebar.Button(p.Tag))));
    });

    [Theory, MemberData(nameof(Themes))]
    public void AnExpandedGroup_ShowsItsPages_AndAChevronPointingDown(string theme) => OnSta(theme, [], (sidebar, _, _) =>
    {
        var group = sidebar.Group("Waits");

        Assert.True(group.IsExpanded);
        Assert.Equal(Visibility.Visible, Part<ContentPresenter>(group, "Body").Visibility);
        Assert.Equal(ChevronDown, Part<TextBlock>(group, "Chevron").Text);
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(group, "CurrentMark").Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void ASavedCollapsedGroup_StartsCollapsed_WithoutReportingAChange(string theme) =>
        OnSta(theme, ["Waits"], (sidebar, _, toggled) =>
        {
            var group = sidebar.Group("Waits");

            Assert.False(group.IsExpanded);
            Assert.Equal(Visibility.Collapsed, Part<ContentPresenter>(group, "Body").Visibility);
            Assert.Equal(ChevronRight, Part<TextBlock>(group, "Chevron").Text);
            Assert.Empty(toggled);
        });

    [Theory, MemberData(nameof(Themes))]
    public void ClickingTheHeader_TogglesTheGroup_AndReportsIt(string theme) => OnSta(theme, [], (sidebar, _, toggled) =>
    {
        var header = Part<ToggleButton>(sidebar.Group("Queries"), "HeaderToggle");
        var click  = (IToggleProvider)new ToggleButtonAutomationPeer(header);

        click.Toggle();
        click.Toggle();

        Assert.Equal([("Queries", true), ("Queries", false)], toggled);
    });

    [Theory, MemberData(nameof(Themes))]
    public void EnterAndSpace_OnTheFocusedGroup_ToggleIt(string theme) => OnSta(theme, [], (sidebar, _, toggled) =>
    {
        var group = sidebar.Group("Resources");

        Assert.True(group.Focusable);
        Assert.False(Part<ToggleButton>(group, "HeaderToggle").Focusable);

        Assert.True(PressKey(group, Key.Enter));
        Assert.True(PressKey(group, Key.Space));
        Assert.False(PressKey(group, Key.Down));
        // A key pressed on one of its pages is the page's, and bubbles up without toggling the group.
        PressKey(sidebar.Button("TempDb"), Key.Enter);

        Assert.Equal([("Resources", true), ("Resources", false)], toggled);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheGroup_IsAnnouncedAsExpandable_WithItsState(string theme) => OnSta(theme, ["Maintenance"], (sidebar, _, _) =>
    {
        var group = sidebar.Group("Maintenance");
        var peer  = UIElementAutomationPeer.CreatePeerForElement(group);
        var state = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);

        Assert.Equal("Maintenance", peer.GetName());
        Assert.Equal(ExpandCollapseState.Collapsed, state.ExpandCollapseState);
        group.IsExpanded = true;
        Assert.Equal(ExpandCollapseState.Expanded, state.ExpandCollapseState);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheQuestion_IsTheHeadersTooltip_NotThePages(string theme) => OnSta(theme, [], (sidebar, _, _) =>
    {
        var group = sidebar.Group("Activity");

        Assert.Equal("Who is connected, what is running, and who is blocking whom?",
                     Part<ToggleButton>(group, "HeaderToggle").ToolTip);
        Assert.Null(group.ToolTip);
        Assert.Null(sidebar.Button("Processes").ToolTip);
    });

    [Theory, MemberData(nameof(Themes))]
    public void OpeningAPage_HighlightsIt_AndExpandsItsGroup(string theme) =>
        OnSta(theme, ["Resources", "Queries"], (sidebar, panel, toggled) =>
        {
            sidebar.Highlight("TempDb");

            Assert.Same(panel.FindResource("NavButtonActive"), sidebar.Button("TempDb").Style);
            Assert.Same(panel.FindResource("NavButton"), sidebar.Button("LiveMetrics").Style);
            Assert.True(sidebar.Group("Resources").IsExpanded);
            Assert.False(sidebar.Group("Queries").IsExpanded);
            Assert.Equal([("Resources", false)], toggled);
            Assert.True(NavGroupState.GetHoldsCurrentPage(sidebar.Group("Resources")));
            Assert.False(NavGroupState.GetHoldsCurrentPage(sidebar.Group("Overview")));

            // A footer page is in no group.
            sidebar.Highlight("Settings");
            Assert.DoesNotContain(NavMenu.Groups, g => NavGroupState.GetHoldsCurrentPage(sidebar.Group(g.Id)));
        });

    [Theory, MemberData(nameof(Themes))]
    public void ACollapsedGroupHoldingTheCurrentPage_SaysSo(string theme) => OnSta(theme, [], (sidebar, panel, _) =>
    {
        var accent = theme == "DarkTheme" ? "AccentLight" : "Accent";
        sidebar.Highlight("Processes");
        var group = sidebar.Group("Activity");

        group.IsExpanded = false;
        panel.UpdateLayout();

        Assert.Equal(Visibility.Visible, Part<TextBlock>(group, "CurrentMark").Visibility);
        Assert.Equal(Color(panel, accent), ((SolidColorBrush)Part<TextBlock>(group, "HeaderText").Foreground).Color);

        // Any other collapsed group looks as usual.
        var other = sidebar.Group("Waits");
        other.IsExpanded = false;
        panel.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(other, "CurrentMark").Visibility);
        Assert.Equal(Color(panel, "TextMuted"), ((SolidColorBrush)Part<TextBlock>(other, "HeaderText").Foreground).Color);
    });

    [Theory, MemberData(nameof(Themes))]
    public void AGroupWhosePagesAreAllDisabled_StaysVisible(string theme) => OnSta(theme, [], (sidebar, panel, _) =>
    {
        sidebar.Button("ConfigChecks").IsEnabled  = false;
        sidebar.Button("SecurityAudit").IsEnabled = false;
        panel.UpdateLayout();

        var group = sidebar.Group("Configuration");
        // Not IsVisible, which is false for anything not in a window.
        Assert.Equal(Visibility.Visible, group.Visibility);
        Assert.True(group.ActualHeight > sidebar.Button("SecurityAudit").ActualHeight);
    });

    // ── Helpers ──────────────────────────────────────────────────────────

    private static T Part<T>(Expander expander, string name) where T : FrameworkElement =>
        (T)expander.Template.FindName(name, expander);

    private static Color Color(FrameworkElement element, string key) =>
        ((SolidColorBrush)element.FindResource(key)).Color;

    // True when the key press was handled.
    private static bool PressKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    // The sidebar in a 190 px wide panel, with the theme's resources, laid out off screen.
    private static void OnSta(string theme, string[] collapsed, Action<NavSidebar, StackPanel, List<(string, bool)>> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                var panel   = new StackPanel { Resources = resources };
                var sidebar = new NavSidebar(panel, collapsed);
                var toggled = new List<(string, bool)>();
                sidebar.GroupToggled += (id, isCollapsed) => toggled.Add((id, isCollapsed));

                panel.Measure(new Size(190, 4000));
                panel.Arrange(new Rect(panel.DesiredSize));
                panel.UpdateLayout();

                test(sidebar, panel, toggled);
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
