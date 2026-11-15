using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The pinned sidebar (#124, #141, #254) as WPF builds it, in each theme: laid out off screen on an
/// STA thread, reading back the elements rather than trusting the code. Its states are also
/// rendered to PNGs under %TEMP%\SqlVitalsTests\sidebar, to look at.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class NavSidebarTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static IEnumerable<string> ServerTags => NavMenu.Groups.SelectMany(g => g.Pages).Select(p => p.Tag);

    [Theory, MemberData(nameof(Themes))]
    public void TheGlobalPart_ThenTheServerPart_EveryScreenUnderItsCaption_ThenSettingsAtTheFoot(string theme) => OnSta(theme, (sidebar, panel, footer) =>
    {
        var children = panel.Children.Cast<UIElement>().ToList();
        Assert.Equal(["Home", "Alerts", "Export"], children.Take(3).Select(c => (string)((Button)c).Tag));
        Assert.Same(sidebar.ServerHeader, children[3]);
        Assert.Same(sidebar.NoServer, children[4]);
        Assert.Same(sidebar.RecentRow, children[5]);

        // Every group a caption, straight above its screens: no expander, nothing folds.
        var expected = NavMenu.Groups.SelectMany(g => new[] { $"caption:{g.Id}" }.Concat(g.Pages.Select(p => p.Tag)));
        var shown = children.Skip(6).Select(c => c switch
        {
            TextBlock caption => $"caption:{caption.Tag}",
            Button button     => (string)button.Tag,
            _                 => c.GetType().Name,
        });
        Assert.Equal(expected, shown);
        Assert.Empty(UiThread.Find<Expander>(panel));
        Assert.Empty(UiThread.Find<ToggleButton>(panel));

        Assert.Equal(["Settings"], footer.Children.OfType<Button>().Select(b => (string)b.Tag));
        Assert.All(NavMenu.AllPages, p => Assert.Equal(p.Label, AutomationProperties.GetName(sidebar.Button(p.Tag))));
        Assert.Equal("All servers", AutomationProperties.GetName(sidebar.Button("Home")));
    });

    [Theory, MemberData(nameof(Themes))]
    public void WithAServerOpen_EveryScreenIsShownAndEnabled_In24PxRows_Under22PxCaptions(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        Layout(panel);

        Assert.All(ServerTags, tag =>
        {
            var button = sidebar.Button(tag);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.IsEnabled, tag);
            Assert.Equal(NavSidebar.RowHeight, button.ActualHeight);
        });
        Assert.All(NavMenu.Groups, g =>
        {
            var caption = sidebar.Caption(g.Id);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.Equal(NavSidebar.CaptionHeight, caption.ActualHeight);
            // Small, upper case and muted, its question the tooltip.
            Assert.Equal(g.Name.ToUpperInvariant(), caption.Text);
            Assert.Equal(g.Question, caption.ToolTip);
            Assert.Equal(Color(panel, "TextMuted"), ((SolidColorBrush)caption.Foreground).Color);
        });
        Assert.Equal(NavSidebar.GlobalHeight, sidebar.Button("Alerts").ActualHeight);

        // The global pages keep their Fluent icons; no emoji anywhere.
        var icon = UiThread.Find<TextBlock>(sidebar.Button("Alerts")).First(t => t.FontFamily.Source == NavMenu.IconFont);
        Assert.Equal("\uEA8F", icon.Text);
        Assert.DoesNotContain(UiThread.Find<TextBlock>(panel), t => t.Text.Any(char.IsSurrogate));
    });

    [Theory, MemberData(nameof(Themes))]
    public void ACaption_IsAnnouncedAsTheHeadingOfItsScreens_AndIsNoStopForTheKeyboard(string theme) => OnSta(theme, (sidebar, _, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        var caption = sidebar.Caption("Activity");
        var peer    = UIElementAutomationPeer.CreatePeerForElement(caption);

        Assert.Equal("Activity now", peer.GetName());
        Assert.Equal(AutomationHeadingLevel.Level2, peer.GetHeadingLevel());
        Assert.Equal("Who is connected, what is running, and who is blocking whom?", peer.GetHelpText());
        Assert.False(caption.Focusable);
        Assert.All(ServerTags, tag => Assert.True(sidebar.Button(tag).Focusable));
        // A screen's own tooltip says what it shows; the filter and the quick switcher find it by those words.
        Assert.Equal(NavMenu.AllPages.Single(p => p.Tag == "Processes").ToolTip, sidebar.Button("Processes").ToolTip);
    });

    [Theory, MemberData(nameof(Themes))]
    public void WithNoServerOpen_TheServerPartSaysHowToOpenOne(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        var requested = new List<string>();
        sidebar.PageRequested += requested.Add;
        sidebar.ShowServer(null);
        sidebar.ShowRecent(["Waits"]);
        sidebar.Highlight("Home");
        Layout(panel);

        Assert.Equal(Visibility.Collapsed, sidebar.ServerHeader.Visibility);
        Assert.Equal(Visibility.Visible, sidebar.NoServer.Visibility);
        Assert.Equal(Visibility.Collapsed, sidebar.RecentRow.Visibility);
        Assert.Equal("Open a server from All servers", string.Concat(sidebar.NoServer.Inlines.Select(i => i switch
        {
            Run run        => run.Text,
            Hyperlink link => string.Concat(link.Inlines.OfType<Run>().Select(r => r.Text)),
            _              => "",
        })));
        Assert.All(NavMenu.Groups, g => Assert.Equal(Visibility.Collapsed, sidebar.Caption(g.Id).Visibility));
        Assert.All(ServerTags, t => Assert.Equal(Visibility.Collapsed, sidebar.Button(t).Visibility));

        // "All servers" in it is a link to Home.
        var link = sidebar.NoServer.Inlines.OfType<Hyperlink>().Single();
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(["Home"], requested);

        Render(panel, theme, "no-server");
    });

    [Theory, MemberData(nameof(Themes))]
    public void WithAServerOpen_ItsCard_AndItsNameOpensTheServerMenu(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        var toggled = 0;
        var menus   = 0;
        sidebar.MonitorToggleRequested += () => toggled++;
        sidebar.ServerMenuRequested    += () => menus++;
        sidebar.ShowServer(Server("PROD-SQL01"));
        sidebar.Highlight("Processes");
        Layout(panel);

        Assert.Equal(Visibility.Visible, sidebar.ServerHeader.Visibility);
        Assert.Equal(Visibility.Collapsed, sidebar.NoServer.Visibility);
        Assert.Equal("PROD-SQL01", sidebar.ServerName.Text);
        Assert.Equal("prod · SalesDb", sidebar.ServerLine.Text);
        Assert.Equal("Server PROD-SQL01", AutomationProperties.GetName(sidebar.ServerHeader));
        Assert.Equal("Server PROD-SQL01. Switch server", AutomationProperties.GetName(sidebar.ServerNameButton));
        Assert.Contains("Click to switch to another server.", (string)sidebar.ServerNameButton.ToolTip);
        Click(sidebar.ServerNameButton);
        Assert.Equal(1, menus);

        // Not monitored: no score yet, and the button starts it.
        Assert.Equal(Visibility.Collapsed, sidebar.ScoreButton.Visibility);
        Assert.Equal("Start monitoring PROD-SQL01", AutomationProperties.GetName(sidebar.MonitorButton));
        Assert.Equal("\uE768", sidebar.MonitorButton.Content);
        Click(sidebar.MonitorButton);
        Assert.Equal(1, toggled);

        Render(panel, theme, "server");
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheScreenOnShow_IsHighlighted_ItsCaptionIsNot(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        sidebar.Highlight("TempDb");
        Layout(panel);

        Assert.Same(panel.FindResource("NavButtonActive"), sidebar.Button("TempDb").Style);
        Assert.Same(panel.FindResource("NavButton"), sidebar.Button("LiveMetrics").Style);
        Assert.Equal(((SolidColorBrush)sidebar.Caption("Queries").Foreground).Color, ((SolidColorBrush)sidebar.Caption("Resources").Foreground).Color);
        Assert.Equal(FontWeights.SemiBold, sidebar.Caption("Resources").FontWeight);
        Assert.Equal(sidebar.Caption("Queries").FontWeight, sidebar.Caption("Resources").FontWeight);

        // One click on any other screen asks for it: nothing to open first.
        var requested = new List<string>();
        sidebar.PageRequested += requested.Add;
        Click(sidebar.Button("SecurityAudit"));
        Click(sidebar.Button("Waits"));
        Assert.Equal(["SecurityAudit", "Waits"], requested);

        sidebar.Highlight("Alerts");
        Assert.Same(panel.FindResource("NavButtonActive"), sidebar.Button("Alerts").Style);
        Assert.All(ServerTags, t => Assert.Same(panel.FindResource("NavButton"), sidebar.Button(t).Style));
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheFilter_NarrowsTheScreens_HidesEmptyCaptions_EnterOpensTheFirst_EscClears(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        var requested = new List<string>();
        sidebar.PageRequested += requested.Add;
        sidebar.ShowServer(Server("PROD-SQL01"));
        sidebar.ShowRecent(["Waits"]);
        Assert.Equal("Ctrl K", ((TextBlock)sidebar.KeysHint.Child).Text);

        sidebar.Filter.Text = "stat";
        Layout(panel);
        // Stale Stats and Table Statistics by name, others by a word of their tooltip, in the sidebar's order.
        var kept = sidebar.Shown.Select(b => (string)b.Tag).ToList();
        Assert.Equal(NavMenu.AllPages.Where(p => p.Tag != "Settings" && SidebarFindings.Matches("stat", p)).Select(p => p.Tag), kept);
        Assert.Contains("StaleStats", kept);
        Assert.Contains("TableStats", kept);
        Assert.DoesNotContain("Processes", kept);
        Assert.Equal(Visibility.Visible, sidebar.Caption("Maintenance").Visibility);
        Assert.Equal(Visibility.Collapsed, sidebar.Caption("Waits").Visibility);
        Assert.Equal(Visibility.Collapsed, sidebar.RecentRow.Visibility);
        Render(panel, theme, "filter");

        // Enter opens the best match, not Deadlocks above it (a word of its tooltip), and empties the box.
        Assert.Equal("Deadlocks", kept[0]);
        Assert.True(PressKey(sidebar.Filter, Key.Enter, preview: true));
        Assert.Equal(["StaleStats"], requested);
        Assert.Equal(string.Empty, sidebar.Filter.Text);

        // Empty, every screen shows again, and the Recent row.
        Assert.All(ServerTags, t => Assert.Equal(Visibility.Visible, sidebar.Button(t).Visibility));
        Assert.All(NavMenu.Groups, g => Assert.Equal(Visibility.Visible, sidebar.Caption(g.Id).Visibility));
        Assert.Equal(Visibility.Visible, sidebar.RecentRow.Visibility);

        // Esc clears it; with nothing in it, Esc is left to whatever else uses it.
        sidebar.Filter.Text = "blocking";
        Assert.Equal(["Processes"], sidebar.Shown.Select(b => (string)b.Tag));
        Assert.True(PressKey(sidebar.Filter, Key.Escape, preview: true));
        Assert.Equal(string.Empty, sidebar.Filter.Text);
        Assert.False(PressKey(sidebar.Filter, Key.Escape, preview: true));

        // Nothing matching: Enter does nothing.
        sidebar.Filter.Text = "zzz";
        Assert.Empty(sidebar.Shown);
        Assert.All(NavMenu.Groups, g => Assert.Equal(Visibility.Collapsed, sidebar.Caption(g.Id).Visibility));
        Assert.False(PressKey(sidebar.Filter, Key.Enter, preview: true));
        Assert.Single(requested);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheFilter_SkipsAScreenTheServerLacks_WhenEnterIsPressed(string theme) => OnSta(theme, (sidebar, _, _) =>
    {
        var requested = new List<string>();
        sidebar.PageRequested += requested.Add;
        sidebar.ShowServer(Server("Azure-DB"));
        sidebar.SetUnavailable(new Dictionary<string, string> { ["Backups"] = "Not available on Azure SQL Database" });

        // Only Backups' tooltip speaks of an RPO: shown, disabled, so Enter has nothing to open.
        sidebar.Filter.Text = "rpo";
        Assert.Equal(["Backups"], sidebar.Shown.Select(b => (string)b.Tag));
        Assert.False(PressKey(sidebar.Filter, Key.Enter, preview: true));
        Assert.Empty(requested);

        // Clicking a screen the filter kept opens it, and the whole list is back.
        sidebar.Filter.Text = "temp";
        Click(sidebar.Button("TempDb"));
        Assert.Equal(["TempDb"], requested);
        Assert.Equal(string.Empty, sidebar.Filter.Text);
        Assert.Equal(Visibility.Visible, sidebar.Button("Processes").Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheRecentRow_ShowsItsScreensAsChips_AndIsHiddenUntilThereIsOne(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        var requested = new List<string>();
        sidebar.PageRequested += requested.Add;
        sidebar.ShowServer(Server("PROD-SQL01"));
        Assert.Equal(Visibility.Collapsed, sidebar.RecentRow.Visibility);

        sidebar.ShowRecent(["Processes", "Waits", "Backups"]);
        Layout(panel);
        Assert.Equal(Visibility.Visible, sidebar.RecentRow.Visibility);
        Assert.Equal(["Processes", "Waits", "Backups"], sidebar.RecentChips.Select(c => (string)c.Tag));
        Assert.Equal(["Recent: Processes", "Recent: Waits", "Recent: Backups"], sidebar.RecentChips.Select(AutomationProperties.GetName));
        Assert.Same(panel.FindResource("RecentChip"), sidebar.RecentChips.First().Style);
        // After a clock, on one line, each chip inside the sidebar.
        Assert.Equal("Recent", AutomationProperties.GetName(sidebar.RecentRow));
        var tops = sidebar.RecentChips.Select(c => c.TranslatePoint(new Point(0, 0), sidebar.RecentRow)).ToList();
        Assert.Single(tops.Select(t => t.Y).Distinct());
        Assert.All(sidebar.RecentChips.Zip(tops), c => Assert.True(c.Second.X + c.First.ActualWidth <= Width - 8, $"{c.First.Tag} ends at {c.Second.X + c.First.ActualWidth}"));

        Click(sidebar.RecentChips.ElementAt(1));
        Assert.Equal(["Waits"], requested);

        sidebar.ShowRecent([]);
        Assert.Equal(Visibility.Collapsed, sidebar.RecentRow.Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void AScreenWithAFinding_ShowsTheWorstChecksNameAndDot_InItsColour(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("PROD-SQL01"));
        var findings = SidebarFindings.From(Score(
            (HealthIndicator.Blocking, HealthLevel.Warning, 240),
            (HealthIndicator.OpenTransaction, HealthLevel.Critical, 45),
            (HealthIndicator.BackupsPastRpo, HealthLevel.Warning, 2)));
        sidebar.ShowFindings(findings);
        sidebar.ShowRecent(["Waits", "Backups", "Overview"]);
        sidebar.Highlight("QueryStore");
        Layout(panel);

        // Two checks on Processes: the critical one names it, the tooltip lists both.
        var processes = sidebar.Mark("Processes");
        Assert.Equal(Visibility.Visible, processes.Visibility);
        Assert.Equal("open transaction", processes.NameText.Text);
        Assert.Equal(FindingMark.DotSize, processes.Dot.ActualWidth);
        Assert.Equal(Color(panel, "HealthCritical"), ((SolidColorBrush)processes.Dot.Fill).Color);
        Assert.Equal(Color(panel, "HealthCriticalText"), ((SolidColorBrush)processes.NameText.Foreground).Color);
        var tip = (string)sidebar.Button("Processes").ToolTip;
        Assert.StartsWith(string.Join("\n", findings["Processes"].Lines), tip);
        Assert.EndsWith(NavMenu.AllPages.Single(p => p.Tag == "Processes").ToolTip!, tip);
        Assert.Equal("Processes, open transaction, critical", AutomationProperties.GetName(sidebar.Button("Processes")));

        // The dot sits at the right of the row.
        var row = sidebar.Button("Processes");
        var at  = processes.Dot.TranslatePoint(new Point(0, 0), row);
        Assert.True(at.X > row.ActualWidth - 30, $"the dot is at {at.X} of {row.ActualWidth}");

        Assert.Equal("RPO missed", sidebar.Mark("Backups").NameText.Text);
        Assert.Equal(Color(panel, "HealthWarning"), ((SolidColorBrush)sidebar.Mark("Backups").Dot.Fill).Color);
        Assert.All(ServerTags.Except(["Processes", "Backups"]), t => Assert.Equal(Visibility.Collapsed, sidebar.Mark(t).Visibility));

        Render(panel, theme, "findings");

        // The next sample has none: the dots go, the tooltips and names are the screens' own again.
        sidebar.ShowFindings(SidebarFindings.None);
        Assert.Equal(Visibility.Collapsed, sidebar.Mark("Processes").Visibility);
        Assert.Equal(NavMenu.AllPages.Single(p => p.Tag == "Processes").ToolTip, sidebar.Button("Processes").ToolTip);
        Assert.Equal("Processes", AutomationProperties.GetName(sidebar.Button("Processes")));
    });

    [Theory, MemberData(nameof(Themes))]
    public void APageTheServerLacks_IsDisabled_AndSaysSoInText(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.ShowServer(Server("Azure-DB"));
        sidebar.Highlight("IndexHealth");
        sidebar.SetUnavailable(new Dictionary<string, string> { ["Backups"] = "Not available on Azure SQL Database" });
        var backups = sidebar.Button("Backups");
        Layout(panel);

        Assert.False(backups.IsEnabled);
        Assert.Equal("Not available on Azure SQL Database", backups.ToolTip);
        var notAvailable = (FrameworkElement)backups.Template.FindName("NotAvailable", backups);
        Assert.Equal(Visibility.Visible, notAvailable.Visibility);
        Assert.Equal(Color(panel, "TextDisabled"), ((SolidColorBrush)backups.Foreground).Color);
        Render(panel, theme, "disabled");

        // Back on a server that has it: enabled, with its own tooltip.
        sidebar.SetUnavailable(new Dictionary<string, string>());
        Assert.True(backups.IsEnabled);
        Assert.Equal(NavMenu.AllPages.Single(p => p.Tag == "Backups").ToolTip, backups.ToolTip);
    });

    [Theory, MemberData(nameof(Themes))]
    public void Alerts_ShowTheActiveCount(string theme) => OnSta(theme, (sidebar, panel, _) =>
    {
        sidebar.SetCount("Alerts", 2, "2 active alerts");
        Layout(panel);

        var badge = UiThread.Find<Border>(sidebar.Button("Alerts")).Single(b => b.Child is TextBlock { Text: "2" });
        Assert.Equal(Visibility.Visible, badge.Visibility);
        Assert.Equal("Alerts (2 active)", AutomationProperties.GetName(sidebar.Button("Alerts")));
        Assert.Equal("2 active alerts", sidebar.Button("Alerts").ToolTip);

        sidebar.SetCount("Alerts", 0, NavMenu.AlertsToolTip);
        Assert.Equal(Visibility.Collapsed, badge.Visibility);
        Assert.Equal("Alerts", AutomationProperties.GetName(sidebar.Button("Alerts")));
    });

    // ── Helpers ──────────────────────────────────────────────────────────

    private const double Width  = 232;
    private const double Height = 1000;

    private static ConnectionSelectorItem Server(string name) =>
        new(new ConnectionSettings { Server = "prod", Name = name, Database = "SalesDb" });

    private static HealthScore Score(params (HealthIndicator Indicator, HealthLevel Level, double Value)[] checks) =>
        HealthScore.From(checks.Select(c => new HealthReading(c.Indicator, c.Level, c.Value, new HealthThreshold(1, 2), null)).ToList(), DateTime.Now);

    private static Color Color(FrameworkElement element, string key) =>
        ((SolidColorBrush)element.FindResource(key)).Color;

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

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

    private static void Layout(FrameworkElement panel)
    {
        var host = Host(panel);
        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));
        host.UpdateLayout();
    }

    private static FrameworkElement Host(FrameworkElement panel)
    {
        FrameworkElement host = panel;
        while (host.Parent is FrameworkElement parent)
            host = parent;
        return host;
    }

    private static void Render(FrameworkElement panel, string theme, string state)
    {
        Layout(panel);
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "sidebar");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Host(panel));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{state}-{theme}.png"));
        encoder.Save(file);
    }

    // The sidebar in a 232 px wide column, the filter box on top, with the theme's resources and the
    // sidebar's own styles, laid out off screen.
    private static void OnSta(string theme, Action<NavSidebar, StackPanel, StackPanel> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                    new Uri("/SqlVitals.Desktop;component/Controls/SidebarStyles.xaml", UriKind.Relative)));
                var panel  = new StackPanel();
                var footer = new StackPanel();
                var host = new DockPanel { Resources = resources, Background = (Brush)resources["BgSidebar"], LastChildFill = false };
                var sidebar = new NavSidebar(panel, footer);
                DockPanel.SetDock(sidebar.FilterBar, Dock.Top);
                DockPanel.SetDock(footer, Dock.Bottom);
                DockPanel.SetDock(panel, Dock.Top);
                host.Children.Add(sidebar.FilterBar);
                host.Children.Add(footer);
                host.Children.Add(panel);
                Layout(panel);

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
