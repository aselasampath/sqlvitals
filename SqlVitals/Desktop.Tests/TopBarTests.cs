using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The top bar (#140) as WPF builds it, in each theme, laid out off screen on an STA thread: what
/// it says for a live page, a snapshot and history, and that a screen reader hears the same. Each
/// state is also rendered to a PNG under %TEMP%\SqlVitalsTests\top-bar, to look at.
/// </summary>
public sealed class TopBarTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static readonly Guid Prod = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 14, 2, 36, TimeSpan.Zero);

    private const double Width = 1130;   // the content area beside the 190 px sidebar, at the default 1320

    [Theory, MemberData(nameof(Themes))]
    public void Live_ShowsTheBadge_TheInterval_AsOf_AndPause(string theme) => OnSta(theme, (bar, host, _) =>
    {
        bar.ShowPage(new TimeRangePicker(), Live(paused: false), canPause: true, database: null);
        Layout(host);

        Assert.Equal("Live", bar.TxtBadge.Text);
        Assert.Equal(Visibility.Visible, bar.BadgeDot.Visibility);
        Assert.Equal("every 5 s · As of 14:02:30", bar.TxtDetail.Text);
        Assert.Equal(Color(host, "LiveBadgeText"), Brush(bar.TxtBadge.Foreground));
        Assert.Equal(Visibility.Visible, bar.BtnPause.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.BtnRefresh.Visibility);
        Assert.Equal("Pause live updates", AutomationProperties.GetName(bar.BtnPause));
        Assert.IsType<TimeRangePicker>(bar.ToolsHost.Content);
        Assert.Equal(Visibility.Collapsed, bar.BtnDatabase.Visibility);

        Render(host, theme, "live");
    });

    [Theory, MemberData(nameof(Themes))]
    public void Live_Paused_OffersResume_AndRefresh(string theme) => OnSta(theme, (bar, host, _) =>
    {
        var requests = new List<bool>();
        bar.PauseRequested += requests.Add;
        bar.ShowPage(null, Live(paused: false), canPause: true, database: null);
        Click(bar.BtnPause);
        bar.ShowContext(Live(paused: true), canPause: true, database: null);
        Layout(host);

        Assert.Equal("Paused", bar.TxtBadge.Text);
        Assert.Equal(Color(host, "TextMuted"), Brush(bar.TxtBadge.Foreground));
        Assert.Equal("Resume live updates", AutomationProperties.GetName(bar.BtnPause));
        Assert.Equal(Visibility.Visible, bar.BtnRefresh.Visibility);
        Click(bar.BtnPause);
        Assert.Equal([true, false], requests);

        Render(host, theme, "live-paused");
    });

    [Theory, MemberData(nameof(Themes))]
    public void Snapshot_ShowsWhenItWasTaken_TheDatabase_AndRefresh(string theme) => OnSta(theme, (bar, host, _) =>
    {
        bar.ShowPage(null, new PageFreshness(PageKind.Snapshot, Taken: Now.DateTime.AddMinutes(-2)), canPause: false, database: "SalesDb");
        bar.CanRefresh = true;
        Layout(host);

        Assert.Equal("Snapshot", bar.TxtBadge.Text);
        Assert.Equal(Visibility.Collapsed, bar.BadgeDot.Visibility);
        Assert.Equal("Taken 14:00:36 · 2 min ago", bar.TxtDetail.Text);
        Assert.Equal(Color(host, "TextMuted"), Brush(bar.TxtDetail.Foreground));
        Assert.Equal(Visibility.Visible, bar.BtnDatabase.Visibility);
        Assert.Equal("Database: SalesDb", AutomationProperties.GetName(bar.BtnDatabase));
        Assert.Equal(Visibility.Collapsed, bar.BtnPause.Visibility);
        Assert.True(bar.BtnRefresh.IsEnabled);

        Render(host, theme, "snapshot");
    });

    [Theory, MemberData(nameof(Themes))]
    public void Snapshot_TurnsAmber_AfterFifteenMinutes(string theme) => OnSta(theme, (bar, host, clock) =>
    {
        bar.ShowPage(null, new PageFreshness(PageKind.Snapshot, Taken: Now.DateTime.AddMinutes(-14)), canPause: false, database: null);
        Assert.Equal("Taken 13:48:36 · 14 min ago", bar.TxtDetail.Text);
        Assert.Equal(Color(host, "TextMuted"), Brush(bar.TxtDetail.Foreground));

        // The bar's timer moves the age on; a minute later it is stale.
        clock.Advance(TimeSpan.FromMinutes(1));
        bar.Tick();
        Layout(host);

        Assert.Equal("Taken 13:48:36 · 15 min ago", bar.TxtDetail.Text);
        Assert.Equal(Color(host, "StaleText"), Brush(bar.TxtDetail.Foreground));
        Assert.Equal(FontWeights.SemiBold, bar.TxtDetail.FontWeight);
        // Not only the colour: the badge reads it out.
        Assert.EndsWith("more than 15 minutes old", AutomationProperties.GetName(bar.TxtBadge));

        Render(host, theme, "snapshot-stale");
    });

    [Theory, MemberData(nameof(Themes))]
    public void History_ShowsItsWindow_AndWhereTheDataEnds(string theme) => OnSta(theme, (bar, host, _) =>
    {
        bar.ShowPage(null, new PageFreshness(PageKind.History, Window: "Last 24 hours", CompareWith: "the 7 days before",
                                              DataTo: Now.DateTime.AddMinutes(-7)),
                     canPause: false, database: "SalesDb");
        Layout(host);

        Assert.Equal("History", bar.TxtBadge.Text);
        Assert.Equal("Last 24 hours · compared with the 7 days before · Data to 13:55", bar.TxtDetail.Text);
        Assert.Equal(Color(host, "HistoryBadgeText"), Brush(bar.TxtBadge.Foreground));
        Assert.Equal(Visibility.Visible, bar.BtnRefresh.Visibility);

        Render(host, theme, "history");
    });

    [Theory, MemberData(nameof(Themes))]
    public void APageWithNoKind_HasNoContext(string theme) => OnSta(theme, (bar, host, _) =>
    {
        bar.ShowPage(null, new PageFreshness(PageKind.None), canPause: false, database: null);
        Layout(host);

        Assert.Equal(Visibility.Collapsed, bar.ContextPanel.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.Badge.Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void AnError_ShowsInTheBar_UntilCleared(string theme) => OnSta(theme, (bar, host, _) =>
    {
        bar.ShowPage(null, new PageFreshness(PageKind.Snapshot, Taken: Now.DateTime), canPause: false, database: null);
        bar.ShowMessage("Error [WaitStatsRepository.GetBackupsAsync]: Timeout expired", isError: true);
        Layout(host);

        Assert.True(bar.MessageIsError);
        Assert.Equal(Visibility.Visible, bar.TxtMessage.Visibility);
        Assert.Equal(Color(host, "Danger"), Brush(bar.TxtMessage.Foreground));
        Assert.Equal("Error: Error [WaitStatsRepository.GetBackupsAsync]: Timeout expired", AutomationProperties.GetName(bar.TxtMessage));
        Render(host, theme, "error");

        // The next page starts without it.
        bar.ShowPage(null, new PageFreshness(PageKind.Snapshot), canPause: false, database: null);
        Assert.False(bar.MessageIsError);
        Assert.Equal(Visibility.Collapsed, bar.TxtMessage.Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheBar_IsAToolbar_AndEveryButtonHasAName(string theme) => OnSta(theme, (bar, host, _) =>
    {
        bar.ShowPage(null, Live(paused: false), canPause: true, database: "SalesDb");
        Layout(host);

        var peer = UIElementAutomationPeer.CreatePeerForElement(bar);
        Assert.Equal(AutomationControlType.ToolBar, peer.GetAutomationControlType());
        Assert.Equal("Navigation and page context", peer.GetName());

        var buttons = UiThread.Find<Button>(bar).ToList();
        Assert.True(buttons.Count >= 10, $"{buttons.Count} buttons");
        Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b)), $"{b.Name} has no name"));
        // The placeholders say what they will be, and that they are not there yet.
        Assert.False(bar.BtnSidebar.IsEnabled);
        Assert.False(bar.BtnJump.IsEnabled);
        Assert.False(bar.BtnAskAi.IsEnabled);
        Assert.Equal("Ask AI", AutomationProperties.GetName(bar.BtnAskAi));
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheBreadcrumb_OpensEachPart_AndShowsTheServersHealth(string theme) => OnSta(theme, (bar, host, _) =>
    {
        var opened = new List<Route>();
        bar.CrumbRequested += opened.Add;
        bar.ShowPage(null, Live(paused: false), canPause: true, database: null);
        Layout(host);

        var parts = UiThread.Find<Button>(bar.Crumbs).ToList();
        Assert.Equal(["All servers", "PROD-SQL01", "Activity now"], parts.Select(AutomationProperties.GetName));
        Assert.Equal("All servers › PROD-SQL01 › Activity now › Deadlocks", bar.BreadcrumbText);
        // The server's health dot.
        Assert.Single(UiThread.Find<System.Windows.Shapes.Ellipse>(bar.Crumbs));

        foreach (var part in parts)
            Click(part);
        Assert.Equal([new Route("Home"), new Route("ServerDetail", ServerId: Prod), new Route("Processes", ConnectionId: Prod)], opened);

        // A new sample for the server updates its part in place: a part with the keyboard focus keeps it.
        var server = new ConnectionSelectorItem(new ConnectionSettings { Server = "prod", Name = "PROD-SQL01" });
        bar.ShowBreadcrumb(Breadcrumb.For(new Route("Deadlocks", ConnectionId: Prod), id => id == Prod ? "PROD-SQL01" : null),
                           id => id == Prod ? server : null);
        Assert.Equal(parts, UiThread.Find<Button>(bar.Crumbs));

        Render(host, theme, "breadcrumb");
    });

    [Fact]
    public void AShortBar_GivesUpTheExtras_ThenWrapsThePagesControls_ButKeepsTheServer() => OnSta("DarkTheme", (bar, host, _) =>
    {
        // Live Metrics, with its range picker and interval, at a 1010 px window.
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(new TimeRangePicker());
        tools.Children.Add(new ComboBox { Width = 110, Height = 26 });
        bar.ShowPage(tools, Live(paused: false), canPause: true, database: null);
        Layout(host, 820);
        Render(host, "DarkTheme", "narrow");

        // Giving up the extras isn't enough, so the page's controls wrap to a row below, and with
        // them out of the way the extras fit again: nothing is lost.
        Assert.True(bar.ToolsWrapped);
        Assert.Equal(Visibility.Visible, bar.TxtDetail.Visibility);
        Assert.Equal(Visibility.Visible, bar.BtnAskAi.Visibility);
        Assert.Same(tools, bar.ToolsRow.Content);
        Assert.True(host.ActualHeight > 52);
        // The server's name is shown in full.
        var server = UiThread.Find<Button>(bar.Crumbs).Single(b => AutomationProperties.GetName(b) == "PROD-SQL01");
        Assert.True(server.RenderSize.Width >= server.DesiredSize.Width - 0.5);
        Assert.True(bar.Crumbs.Spare >= 0, $"{bar.Crumbs.Spare} px short");
        Assert.Empty(bar.Crumbs.Dropped.Where(d => !CrumbPanel.GetCanDrop(d)));
        // Still said: the badge's tooltip and spoken name carry the detail.
        Assert.Contains("as of 14:02:30", (string)bar.Badge.ToolTip);

        // With room again, the controls come back into the bar, and the extras with them.
        Layout(host, Width);
        Assert.False(bar.ToolsWrapped);
        Assert.Same(tools, bar.ToolsHost.Content);
        Assert.Equal(52, bar.DesiredSize.Height, 1.5);
        Assert.Equal(Visibility.Visible, bar.TxtDetail.Visibility);
        Assert.Equal(Visibility.Visible, bar.BtnAskAi.Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void AtAnyWidth_TheServerIsShownInFull_AndASecondLayoutChangesNothing(string theme) => OnSta(theme, (bar, host, _) =>
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(new TimeRangePicker());
        tools.Children.Add(new ComboBox { Width = 110, Height = 26 });
        bar.ShowPage(tools, Live(paused: false), canPause: true, database: "SalesDb");

        foreach (var width in Enumerable.Range(0, 31).Select(i => 600.0 + i * 20).Concat(Enumerable.Range(0, 31).Select(i => 1200.0 - i * 20)))
        {
            Layout(host, width);
            var state = (bar.ToolsWrapped, bar.TxtDetail.Visibility, bar.BtnAskAi.Visibility, Dropped: string.Join("|", bar.Crumbs.Dropped.Select(AutomationProperties.GetName)));
            var server = UiThread.Find<Button>(bar.Crumbs).Single(b => AutomationProperties.GetName(b) == "PROD-SQL01");
            Assert.True(server.RenderSize.Width >= server.DesiredSize.Width - 0.5, $"At {width} px the server is cut");
            Assert.DoesNotContain("PROD-SQL01", state.Dropped);

            bar.InvalidateMeasure();
            Layout(host, width);
            Assert.Equal(state, (bar.ToolsWrapped, bar.TxtDetail.Visibility, bar.BtnAskAi.Visibility,
                                 string.Join("|", bar.Crumbs.Dropped.Select(AutomationProperties.GetName))));
        }
    });

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PageFreshness Live(bool paused) =>
        new(PageKind.Live, TimeSpan.FromSeconds(5), paused, AsOf: Now.DateTime.AddSeconds(-6));

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static Color Color(FrameworkElement host, string key) => ((SolidColorBrush)host.FindResource(key)).Color;

    private static Color Brush(Brush brush) => ((SolidColorBrush)brush).Color;

    private static void Layout(FrameworkElement host, double width = Width)
    {
        host.Width = width;
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    private static void Render(FrameworkElement host, string theme, string state)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "top-bar");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{state}-{theme}.png"));
        encoder.Save(file);
    }

    // The bar in a theme, on the Deadlocks page of PROD-SQL01 (not monitored), at the fake clock.
    private static void OnSta(string theme, Action<TopBar, Border, FakeTimeProvider> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                var clock = new FakeTimeProvider(Now);
                clock.SetLocalTimeZone(TimeZoneInfo.Utc);
                var bar  = new TopBar { Clock = clock };
                var host = new Border { Resources = resources, Child = bar, Background = (Brush)resources["BgDeep"] };

                var server = new ConnectionSelectorItem(new ConnectionSettings { Server = "prod", Name = "PROD-SQL01" });
                var route  = new Route("Deadlocks", ConnectionId: Prod);
                bar.ShowBreadcrumb(Breadcrumb.For(route, id => id == Prod ? "PROD-SQL01" : null), id => id == Prod ? server : null);
                bar.ShowHistory("Live Metrics · PROD-SQL01", null);

                test(bar, host, clock);
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
}
