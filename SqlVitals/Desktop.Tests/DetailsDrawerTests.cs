using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The details drawer (#147): closed, floating or pinned, its width, what it remembers, and the
/// selected row's details from Processes and Query Store (rendered to %TEMP%\SqlVitalsTests\details-drawer).
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class DetailsDrawerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static readonly string RenderDir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "details-drawer");

    private ConnectionSettingsService Settings() => new(_dir);

    // ── States and width ─────────────────────────────────────────────────

    [Fact]
    public void CtrlJ_OpensItAsLastPinnedOrFloating_AndClosesIt()
    {
        var layout = new DrawerLayout();
        Assert.Equal(DrawerState.Closed, layout.State);

        layout.Toggle();
        Assert.Equal(DrawerState.Floating, layout.State);
        layout.TogglePin();
        Assert.Equal(DrawerState.Pinned, layout.State);
        layout.Toggle();
        Assert.Equal(DrawerState.Closed, layout.State);

        // Pinned is remembered for the next time it opens.
        layout.Toggle();
        Assert.Equal(DrawerState.Pinned, layout.State);
        Assert.Equal(DrawerState.Pinned, Opened(new DrawerLayout(pinned: true)));
        Assert.Equal(DrawerState.Floating, Opened(new DrawerLayout(pinned: false)));

        static DrawerState Opened(DrawerLayout l) { l.Open(); return l.State; }
    }

    [Fact]
    public void Esc_ClosesOnlyAFloatingDrawer()
    {
        var floating = new DrawerLayout();
        floating.Open();
        Assert.True(floating.Escape());
        Assert.Equal(DrawerState.Closed, floating.State);
        Assert.False(floating.Escape());

        var pinned = new DrawerLayout(pinned: true);
        pinned.Open();
        Assert.False(pinned.Escape());
        Assert.Equal(DrawerState.Pinned, pinned.State);
    }

    [Theory]
    [InlineData(100, 320)]
    [InlineData(320, 320)]
    [InlineData(455, 455)]
    [InlineData(900, 640)]
    [InlineData(double.NaN, 380)]
    [InlineData(double.PositiveInfinity, 380)]
    public void TheWidth_StaysWithin320And640(double asked, double width)
    {
        Assert.Equal(width, DrawerLayout.Clamp(asked));
        Assert.Equal(width, new DrawerLayout(width: asked).Width);
        Assert.Equal(width, new DrawerLayout().Resize(asked));
    }

    [Fact]
    public void PinnedAndWidth_SurviveSaveAndLoad_NotInTheActivityLog()
    {
        Assert.Equal((false, 380.0), (Settings().Load().DrawerPinned, Settings().Load().DrawerWidth));
        var settings = Settings();
        var logged = new List<ActivityEntry>();
        settings.Changed += logged.AddRange;

        settings.SetDrawer(pinned: true, width: 2000);

        var store = Settings().Load();
        Assert.Equal((true, 640.0), (store.DrawerPinned, store.DrawerWidth));
        Assert.Empty(logged);
    }

    // ── The host ─────────────────────────────────────────────────────────

    private static (DrawerHost Host, Border Page) Host(bool pinned = false)
    {
        var page = new Border { Background = Brushes.Transparent };
        var host = new DrawerHost { Page = page, Layout = new DrawerLayout(pinned, 400) };
        return (host, page);
    }

    [Fact]
    public void Pinned_TheDrawerTakesItsWidthFromThePage_Floating_ItLiesOverIt() => UiThread.Run(() =>
    {
        var (host, page) = Host();
        UiThread.Layout(host, 1000, 600);
        Assert.Equal(Visibility.Collapsed, host.Drawer.Visibility);
        Assert.Equal(1000, page.ActualWidth);

        host.Toggle();   // floating
        UiThread.Layout(host, 1000, 600);
        Assert.Equal(1000, page.ActualWidth);
        Assert.Equal(400, host.Drawer.ActualWidth);
        Assert.False(host.Drawer.IsPinned);

        host.Drawer.PinButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        UiThread.Layout(host, 1000, 600);
        Assert.Equal(DrawerState.Pinned, host.State);
        Assert.Equal(600, page.ActualWidth);
        Assert.Equal(400, host.Drawer.ActualWidth);
    });

    [Fact]
    public void DraggingTheEdge_Resizes_WithinItsLimits_AndRemembersWhenDropped() => UiThread.Run(() =>
    {
        var (host, _) = Host(pinned: true);
        var saved = new List<(bool, double)>();
        host.PreferencesChanged += (pinned, width) => saved.Add((pinned, width));
        host.Open();

        host.Drawer.Edge.RaiseEvent(new DragDeltaEventArgs(-80, 0));   // dragged left: wider
        Assert.Equal(480, host.Drawer.Width);
        host.Drawer.Edge.RaiseEvent(new DragDeltaEventArgs(-500, 0));
        Assert.Equal(640, host.Drawer.Width);
        host.Drawer.Edge.RaiseEvent(new DragDeltaEventArgs(1000, 0));
        Assert.Equal(320, host.Drawer.Width);
        Assert.Empty(saved);

        host.Drawer.Edge.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
        Assert.Equal([(true, 320.0)], saved);
    });

    [Fact]
    public void TheCloseButton_AndEsc_Close() => UiThread.Run(() =>
    {
        var (host, _) = Host();
        host.Open();
        Assert.True(host.Escape());
        Assert.Equal(DrawerState.Closed, host.State);

        host.Open();
        host.Drawer.CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(DrawerState.Closed, host.State);
    });

    // ── Processes ────────────────────────────────────────────────────────

    private static readonly DateTime Now = DateTime.Now;

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0, string status = "running", int openTran = 0) =>
        new(spid, blockedBy, status, "svc_orders", "APP-WEB03", "OrderService", blockedBy != 0 ? "LCK_M_X" : null, waitMs,
            null, null, "SalesDb", 120, 40, 2, Now.AddHours(-3), Now.AddMinutes(-4), openTran, "AWAITING COMMAND",
            "BEGIN TRANSACTION;\nUPDATE dbo.Orders SET Status = @p0 WHERE OrderId = @p1;", "",
            openTran > 0 ? Now.AddMinutes(-4).AddSeconds(-12) : null, Now);

    private static IWaitStatsRepository Sessions(params ProcessNode[] nodes) => new FakeRepository
    {
        GetProcessesAsync   = () => Task.FromResult<IEnumerable<ProcessNode>>(nodes),
        GetSessionPlanAsync = _ => Task.FromResult(new SessionPlan(null, false)),
    };

    [Fact]
    public void Processes_WithNothingSelected_SaysWhatToSelect_AndAClickOpensTheSession() => UiThread.Run(() =>
    {
        var page = Wave2Pages.Processes(Sessions(Node(87, status: "sleeping", openTran: 1), Node(112, 87, 245_000), Node(5)), Settings());
        var source = (IDetailsSource)page;
        var raised = new List<bool>();
        source.DetailsChanged += raised.Add;
        UiThread.Wait(page.ViewModel.RefreshAsync());

        Assert.Null(source.Details.Body);
        Assert.Equal("Select a session on the map or in the grid to see its details.", source.Details.Hint);

        // The verdict's Go to session 87, as a click on its ball would.
        page.ProcessMap.ShowSession(87);
        Assert.Equal([true], raised);
        Assert.Equal("Session 87", source.Details.Title);
        var view = Assert.IsType<SessionDetailsView>(source.Details.Body);
        Assert.Contains("Blocking: 1 session · longest 4.1 min", view.RowTexts);
        Assert.Contains("Transaction open: 4 min · since " + Now.AddMinutes(-4).AddSeconds(-12).ToString("HH:mm:ss", CultureInfo.CurrentCulture), view.RowTexts);
        Assert.StartsWith("BEGIN TRANSACTION;", view.Statement);

        // The KILL script, to review.
        view.ShowScriptButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Contains("KILL 87;", view.Script);

        // A refresh keeps the same view, and so the script; selecting by the keyboard doesn't open.
        UiThread.Wait(page.ViewModel.RefreshAsync());
        Assert.Same(view, source.Details.Body);
        Assert.Equal([true, false], raised);
        Assert.Contains("KILL 87;", view.Script);
    });

    [Fact]
    public void Processes_ASessionThatHasGone_SaysSo() => UiThread.Run(() =>
    {
        var nodes = new List<ProcessNode> { Node(87), Node(5) };
        var page = Wave2Pages.Processes(new FakeRepository
        {
            GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>(nodes),
        }, Settings());
        UiThread.Wait(page.ViewModel.RefreshAsync());
        page.ProcessMap.ShowSession(87);

        page.ProcessMap.UpdateNodes([Node(5)]);

        var source = (IDetailsSource)page;
        Assert.Null(source.Details.Body);
        Assert.StartsWith("Session 87 has gone since it was selected", source.Details.Hint);
    });

    [Fact]
    public void Processes_TheSessionsPlan_IsReadWhenAsked() => UiThread.Run(() =>
    {
        var page = Wave2Pages.Processes(Sessions(Node(87)), Settings());
        UiThread.Wait(page.ViewModel.RefreshAsync());
        page.ProcessMap.ShowSession(87);
        var view = (SessionDetailsView)((IDetailsSource)page).Details.Body!;

        view.ShowPlanButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        UiThread.Wait(Task.Delay(50));

        Assert.Equal("No plan is cached for it: it hasn't run a statement, or its plan has left the cache.", view.PlanNote);
    });

    // ── Query Store ──────────────────────────────────────────────────────

    [Fact]
    public void QueryStore_ASelectedRow_ShowsItsQuery_AndKeepsItWhenTheSelectionGoes() => UiThread.Run(() =>
    {
        var health = new QueryStoreHealth("SalesDb", "READ_WRITE", "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, "AUTO", "AUTO", null,
                                          200, 1482, 1630, 0, DateTime.UtcNow.AddDays(-20), DateTime.Now);
        var row = new QueryStoreQueryRow(4183, 0, null, "SELECT o.OrderId FROM dbo.Orders o", 2, 8_412, 0, 18_120, 18_120, 2_310, 41_000_000,
                                         null, 902, DateTime.UtcNow.AddMinutes(-3));
        var page = new QueryStorePage(new FakeRepository
        {
            GetQueryStoreStatusAsync          = () => Task.FromResult(new QueryStoreStatus("SalesDb", health, QueryStoreFeatures.None)),
            GetQueryStoreTopQueriesAsync      = (_, _, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreQueryRow>([row])),
            GetQueryStoreRegressionStatsAsync = _ => Task.FromResult(new QueryStoreRegressionStats("SalesDb", "READ_WRITE", true, [])),
            GetQueryStoreQueryDetailsAsync    = (_, _) => Task.FromResult(new QueryStoreQueryDetails(null, [], [])),
        }, Settings());
        var source = (IDetailsSource)page;
        page.Tabs.SelectedItem = page.TabTop;   // off screen, no tab is selected until it is shown
        UiThread.Wait(page.RefreshAsync());
        UiThread.Wait(page.VerdictLoaded);
        Assert.Equal("Select a query in any tab to see its text, its figures and its plans.", source.Details.Hint);

        page.TopGrid.SelectedItem = row;
        UiThread.Wait(page.DetailsLoaded);

        Assert.Equal("Query 4183", source.Details.Title);
        var view = Assert.IsType<QueryDetailsView>(source.Details.Body);
        Assert.Contains("Plans: 2", view.RowTexts);
        Assert.Equal("None of its plans ran in this period.", view.PlansNote);

        page.TopGrid.SelectedItem = null;
        Assert.Equal("Query 4183", source.Details.Title);
    });

    // ── The drawer, pinned beside Processes ──────────────────────────────

    [Fact]
    public void ThePinnedDrawer_BesideProcesses_Renders() => UiThread.Run(() =>
    {
        var page = Wave2Pages.Processes(Sessions(Node(87, status: "sleeping", openTran: 1), Node(112, 87, 252_000), Node(115, 87, 231_000),
                                              Node(130, 112, 40_000), Node(5), Node(6)), Settings());
        var frame = new Frame { NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden };
        var host  = new DrawerHost { Page = frame, Layout = new DrawerLayout(pinned: true, 380) };
        host.SetResourceReference(Panel.BackgroundProperty, "BgDeep");
        var window = new Window { Content = host, Width = 1320, Height = 760, Left = -20_000, Top = -20_000,
                                  WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            frame.Navigate(page);
            UiThread.Wait(Task.Delay(100));
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.ProcessMap.ShowSession(87);
            var source = (IDetailsSource)page;
            host.Show(source.Details);
            host.Open();
            ((SessionDetailsView)source.Details.Body!).ShowScriptButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            UiThread.Wait(Task.Delay(100));
            window.UpdateLayout();

            Assert.Equal("Session 87", host.Drawer.TitleText);
            Assert.Equal(host.ActualWidth - 380, frame.ActualWidth, 1);

            Directory.CreateDirectory(RenderDir);
            var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(RenderDir, "processes-pinned.png"));
            encoder.Save(file);
        }
        finally
        {
            window.Close();
        }
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
