using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
/// The Ctrl+K quick switcher over the window (#144), in each theme, laid out off screen at
/// 1320 × 820 on an STA thread: the keyboard stays in the box, ↑ and ↓ move, Enter opens, Esc closes
/// and gives the focus back. Rendered to PNGs under %TEMP%\SqlVitalsTests\quick-switcher, to look at.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class QuickSwitcherOverlayTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private const double WindowWidth  = 1320;
    private const double WindowHeight = 820;

    private static readonly ConnectionSelectorItem Prod01    = Server("PROD-SQL01", "prod-sql01", 64, HealthLevel.Warning);
    private static readonly ConnectionSelectorItem Prod02    = Server("PROD-SQL02", "prod-sql02", 96, HealthLevel.Healthy);
    private static readonly ConnectionSelectorItem Reporting = Server("REPORTING", "rpt", 88, HealthLevel.Healthy);

    private const string AgentReason = "Not available on Azure SQL Database, which has no SQL Server Agent. Elastic Jobs run scheduled work there.";

    [Theory, MemberData(nameof(Themes))]
    public void Opening_ShowsTheRecentRoutes_WithTheKeyboardInTheBox_AndTheFirstSelected(string theme) => OnSta(theme, (switcher, host, page, _) =>
    {
        Assert.False(switcher.IsOpen);
        Open(switcher);

        Assert.True(switcher.IsOpen);
        Assert.Same(switcher.SearchBox, switcher.FocusMovedTo);
        var recent = Assert.Single(switcher.Sections);
        Assert.Equal(SwitcherGroup.Recent, recent.Group);
        // The page on screen isn't listed.
        Assert.Equal(["Overview · PROD-SQL01", "Waits · PROD-SQL01", "All servers"], recent.Items.Select(i => i.Text));
        Assert.Same(recent.Items[0], switcher.Selected);
        Assert.Equal(Visibility.Visible, switcher.TxtPrompt.Visibility);
        // Inside a server's workspace: the footer says how to keep the page on another.
        Assert.Equal(Visibility.Visible, switcher.TxtTip.Visibility);
        Render(host, theme, "1-recent");
    });

    [Theory, MemberData(nameof(Themes))]
    public void Typing_FindsPagesServersAndSettings_InGroups_TheFirstSelected_SoEnterOpensIt(string theme) => OnSta(theme, (switcher, host, page, _) =>
    {
        Route? opened = null;
        bool? intoPage = null;
        switcher.OpenRequested += (route, into) => (opened, intoPage) = (route, into);
        Open(switcher, focused: page.Child);

        switcher.SearchBox.Text = "temp";
        Layout(host);
        Assert.Equal(Visibility.Collapsed, switcher.TxtPrompt.Visibility);
        Assert.Equal(["On PROD-SQL01", "Same page on another server"], switcher.Sections.Take(2).Select(s => s.Title));
        Assert.Equal("TempDB", switcher.Selected!.Text);

        // The headings in capitals, read out as written; each row read out with its health.
        var headings = switcher.Results.Children.OfType<TextBlock>().ToList();
        Assert.Equal("ON PROD-SQL01", headings[0].Text);
        Assert.Equal("On PROD-SQL01", AutomationProperties.GetName(headings[0]));
        var rows = switcher.Results.Children.OfType<Border>().ToList();
        Assert.Contains(rows, r => AutomationProperties.GetName(r) == "TempDB on PROD-SQL02, prod-sql02, health score 96");
        Assert.Equal("TempDB, Resources, 1 of " + switcher.Openable.Count, AutomationProperties.GetName(switcher.TxtAnnounce));
        Render(host, theme, "2-temp");

        Assert.True(PressKey(switcher.SearchBox, Key.Enter));
        Assert.Equal(new Route("TempDb", ConnectionId: Prod01.Settings.Id), opened);
        Assert.False(switcher.IsOpen);
        // The keyboard was in the page, which is replaced: it goes into the new one, not back.
        Assert.True(intoPage);
        Assert.Same(switcher.SearchBox, switcher.FocusMovedTo);
    });

    [Theory, MemberData(nameof(Themes))]
    public void UpAndDown_MoveTheSelection_RoundTheResults_AndTabStaysInTheBox(string theme) => OnSta(theme, (switcher, host, _, _) =>
    {
        Open(switcher);
        switcher.SearchBox.Text = "waits";
        var openable = switcher.Openable;
        Assert.True(openable.Count >= 3);
        Assert.Same(openable[0], switcher.Selected);

        Assert.True(PressKey(switcher.SearchBox, Key.Down));
        Assert.Same(openable[1], switcher.Selected);
        Assert.True(PressKey(switcher.SearchBox, Key.Up));
        Assert.True(PressKey(switcher.SearchBox, Key.Up));
        Assert.Same(openable[^1], switcher.Selected);
        Assert.True(PressKey(switcher.SearchBox, Key.Down));
        Assert.Same(openable[0], switcher.Selected);

        Assert.True(PressKey(switcher.SearchBox, Key.Tab));
        Assert.True(switcher.IsOpen);
        // Other keys are the box's own.
        Assert.False(PressKey(switcher.SearchBox, Key.Left));
    });

    [Theory, MemberData(nameof(Themes))]
    public void Esc_Closes_AndGivesTheKeyboardBackToWhereItWas(string theme) => OnSta(theme, (switcher, host, _, button) =>
    {
        var opened = 0;
        switcher.OpenRequested += (_, _) => opened++;
        Open(switcher, focused: button);
        switcher.SearchBox.Text = "proc";

        Assert.True(PressKey(switcher.SearchBox, Key.Escape));
        Assert.False(switcher.IsOpen);
        Assert.Equal(0, opened);
        Assert.Same(button, switcher.FocusMovedTo);
    });

    // #191: the tip followed only a change, so a switcher first opened over a page about every
    // server (Alerts) showed it, as the XAML has it.
    [Theory, MemberData(nameof(Themes))]
    public void OverAPageAboutEveryServer_TheTipAboutKeepingThePage_IsHidden(string theme) => OnSta(theme, (switcher, host, _, _) =>
    {
        switcher.Open(_ => [], _ => null, keepsPage: false);
        Assert.Equal(Visibility.Collapsed, switcher.TxtTip.Visibility);

        switcher.Close(restoreFocus: false);
        Open(switcher);
        Assert.Equal(Visibility.Visible, switcher.TxtTip.Visibility);
    });

    [Theory, MemberData(nameof(Themes))]
    public void EnterFromOutsideThePage_GivesTheKeyboardBack_AsThatStays(string theme) => OnSta(theme, (switcher, host, _, button) =>
    {
        bool? intoPage = null;
        switcher.OpenRequested += (_, into) => intoPage = into;
        Open(switcher, focused: button);
        switcher.SearchBox.Text = "deadlocks";

        Assert.True(PressKey(switcher.SearchBox, Key.Enter));
        Assert.False(intoPage);
        Assert.Same(button, switcher.FocusMovedTo);
    });

    [Theory, MemberData(nameof(Themes))]
    public void APageTheServerLacks_IsListedWithItsReason_AndTheSelectionStepsOverIt(string theme) => OnSta(theme, (switcher, host, _, _) =>
    {
        Route? opened = null;
        switcher.OpenRequested += (route, _) => opened = route;
        Open(switcher, azure: true);
        switcher.SearchBox.Text = "agent";
        Layout(host);

        var jobs = switcher.Sections[0].Items[0];
        Assert.Equal("Agent Jobs", jobs.Text);
        Assert.False(jobs.CanOpen);
        Assert.DoesNotContain(jobs, switcher.Openable);
        // Agent Jobs on another server is the first that can be opened.
        Assert.Equal("Agent Jobs on PROD-SQL02", switcher.Selected!.Text);

        var row = switcher.Results.Children.OfType<Border>().First();
        Assert.Contains(UiThread.Find<TextBlock>(row), t => t.Text == AgentReason);
        Assert.EndsWith($"can't be opened: {AgentReason}", AutomationProperties.GetName(row));
        Render(host, theme, "3-unavailable");

        // A click on it does nothing.
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.Null(opened);
        Assert.True(switcher.IsOpen);
    });

    [Theory, MemberData(nameof(Themes))]
    public void AClickOnAResult_OpensIt_AndAClickOutsideTheBox_Closes(string theme) => OnSta(theme, (switcher, host, _, button) =>
    {
        Route? opened = null;
        switcher.OpenRequested += (route, _) => opened = route;
        Open(switcher, focused: button);
        switcher.SearchBox.Text = "prod-sql02";

        // Inside the box: stays open.
        PressMouse(switcher.Card);
        Assert.True(switcher.IsOpen);

        var row = switcher.Results.Children.OfType<Border>().First();
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Assert.Equal(new Route("Processes", ConnectionId: Prod02.Settings.Id), opened);
        Assert.False(switcher.IsOpen);

        Open(switcher, focused: button);
        PressMouse(switcher.Backdrop);
        Assert.False(switcher.IsOpen);
        Assert.Same(button, switcher.FocusMovedTo);
    });

    [Theory, MemberData(nameof(Themes))]
    public void NothingFound_SaysSo(string theme) => OnSta(theme, (switcher, host, _, _) =>
    {
        Open(switcher);
        switcher.SearchBox.Text = "zzzzqq";
        Layout(host);
        Assert.Empty(switcher.Sections);
        Assert.Null(switcher.Selected);
        Assert.Equal(Visibility.Visible, switcher.TxtEmpty.Visibility);
        Assert.Equal("No server, page or setting matches “zzzzqq”.", switcher.TxtEmpty.Text);
        // Enter does nothing.
        Assert.True(PressKey(switcher.SearchBox, Key.Enter));
        Assert.True(switcher.IsOpen);
        Render(host, theme, "4-nothing");
    });

    // ── Helpers ──────────────────────────────────────────────────────────────

    // On PROD-SQL01's Processes, having opened its Overview, Waits and All servers before. Azure:
    // PROD-SQL01 lacks Agent Jobs.
    private static void Open(QuickSwitcherOverlay switcher, UIElement? focused = null, bool azure = false)
    {
        var servers = new[] { Prod01, Prod02, Reporting };
        var id      = Prod01.Settings.Id;
        var current = new Route("Processes", ConnectionId: id);
        var scope = new SwitcherScope(current, id,
            servers.Select(s => new SwitcherServer(s.Settings.Id, s.DisplayName, s.Server)).ToList(),
            [current, Workspace.Overview(id), new Route("Waits", ConnectionId: id), new Route("Home")])
        {
            Unavailable = (tag, server) => azure && tag == "AgentJobs" && server == id ? AgentReason : null,
        };
        switcher.FocusedElement = () => focused;
        switcher.Open(text => QuickSwitcher.Search(text, scope), server => servers.FirstOrDefault(s => s.Settings.Id == server), keepsPage: true);
    }

    private static ConnectionSelectorItem Server(string name, string host, int score, HealthLevel level)
    {
        var item = new ConnectionSelectorItem(new ConnectionSettings { Server = host, Name = name, Database = "SalesDb" });
        void Set(string property, object value) => typeof(ConnectionSelectorItem).GetProperty(property)!.SetValue(item, value);
        Set(nameof(ConnectionSelectorItem.Score), new HealthScore(score, level, [], DateTime.Now));
        Set(nameof(ConnectionSelectorItem.HealthFill), HealthBrushes.For(level));
        Set(nameof(ConnectionSelectorItem.HealthStroke), HealthBrushes.For(level));
        return item;
    }

    private static void PressMouse(UIElement target) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });

    // True when the key press was handled.
    private static bool PressKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(WindowWidth, WindowHeight));
        host.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
        host.UpdateLayout();
    }

    private static void Render(FrameworkElement host, string theme, string state)
    {
        Layout(host);
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "quick-switcher");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)WindowWidth, (int)WindowHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{state}-{theme}.png"));
        encoder.Save(file);
    }

    // The switcher over a page holding a text box, beside a button outside the page (the top bar's).
    private static void OnSta(string theme, Action<QuickSwitcherOverlay, FrameworkElement, Border, Button> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                var button = new Button { Content = "Jump", Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                var page   = new Border { Child = new TextBox { Text = "a filter", Width = 200, Margin = new Thickness(24, 80, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top } };
                var switcher = new QuickSwitcherOverlay { PageHost = page };
                var root = new Grid();
                root.Children.Add(page);
                root.Children.Add(button);
                root.Children.Add(switcher);
                var host = new Border { Resources = resources, Child = root, Background = (Brush)resources["BgDeep"] };
                Layout(host);

                test(switcher, host, page, button);
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

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
