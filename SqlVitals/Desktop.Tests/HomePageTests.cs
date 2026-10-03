using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Home and the server detail page (#130) as WPF builds them, off screen, over three saved
/// connections: "Hot" (the active one, CPU at 99 %, so critical), "Down" (monitored in the
/// background, every collection fails) and "Quiet" (not monitored).
/// </summary>
public sealed class HomePageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();

    private readonly ConnectionSettings _hot   = Conn("hot",   "Hot");
    private readonly ConnectionSettings _down  = Conn("down",  "Down", background: true);
    private readonly ConnectionSettings _quiet = Conn("quiet", "Quiet");

    private MonitoringManager?         _monitoring;
    private ConnectionSettingsService? _settings;

    private static ConnectionSettings Conn(string server, string name, bool background = false) => new()
    {
        Server              = server,
        Name                = name,
        Database            = "Sales",
        Authentication      = SqlAuthMode.Windows,
        MonitorInBackground = background,
    };

    private IWaitStatsRepository Repository(ConnectionSettings conn) => conn.Server == "hot"
        ? MonitoringManagerTests.HotRepository.Create(conn.Server, _calls)
        : MonitoringManagerTests.CountingRepository.Create(conn.Server, _calls);

    // On the UI thread: the three connections saved, Hot active, monitoring started automatically,
    // and one sample collected from each monitored connection.
    private (MonitoringManager Monitoring, ConnectionSettingsService Settings) Setup()
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in new[] { _hot, _down, _quiet })
            _settings.Upsert(conn);
        _settings.SetActive(_hot.Id);
        _settings.SetStartMonitoringAutomatically(true);

        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (conn, _) => Repository(conn));
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);

        UiThread.Wait(_monitoring.Get(_hot.Id)!.CollectNowAsync());
        try { UiThread.Wait(_monitoring.Get(_down.Id)!.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }

        return (_monitoring, _settings);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static Button ButtonOn(FrameworkElement page, string content, string tileName) =>
        UiThread.Find<Button>(page).Single(b => Equals(b.Content, content) &&
                                               b.DataContext is HomeTile { Tile.Name: var name } && name == tileName &&
                                               VisibleUpTo(b, page));

    // Whether nothing between the element and the page is collapsed.
    private static bool VisibleUpTo(FrameworkElement element, FrameworkElement page)
    {
        for (DependencyObject? e = element; e is not null && !ReferenceEquals(e, page); e = System.Windows.Media.VisualTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible })
                return false;
        return true;
    }

    // ── Home ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_ListsEverySavedConnection_MostAttentionFirst_WithItsStatusInWords() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);

        Assert.Equal(["Down", "Hot", "Quiet"], page.TilesShown.Select(t => t.Tile.Name));
        Assert.Equal([TileStatus.Unreachable, TileStatus.Critical, TileStatus.Off], page.TilesShown.Select(t => t.Tile.Status));
        Assert.Equal("80", page.TilesShown[1].Tile.ScoreText);   // CPU past critical: −20
        Assert.True(page.TilesShown[1].Tile.IsActive);

        var texts = UiThread.Find<TextBlock>(page).Where(t => VisibleUpTo(t, page)).Select(t => t.Text).ToList();
        Assert.Contains("UNREACHABLE", texts);
        Assert.Contains("CRITICAL", texts);
        Assert.Contains("OFF", texts);
        Assert.Contains("ACTIVE", texts);
        Assert.Contains("3 connections · 2 monitored · sorted by most attention needed", texts);
        Assert.Contains("Lowered by CPU −20.", texts);

        // Three tiles across, then "+ Add a connection".
        Assert.Equal(4, page.Tiles.Items.Count);
        Assert.Equal("+ Add a connection", ((Button)page.Tiles.Items[3]).Content);
    });

    [Fact]
    public void Open_AsksToOpenThatServersDetails() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);
        var opened = new List<Guid>();
        page.OpenRequested += opened.Add;

        Click(ButtonOn(page, "Open", "Hot"));

        Assert.Equal([_hot.Id], opened);
    });

    [Fact]
    public void TheTileMenu_Opens_StartsOrStopsMonitoring_ShowsTheScore_AndEdits() => UiThread.Run(() =>
    {
        // What the sidebar's connection selector did for each server (#141).
        var (monitoring, settings) = Setup();
        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);
        var opened = new List<Guid>();
        var edited = new List<Guid>();
        page.OpenRequested += opened.Add;
        page.EditRequested += edited.Add;

        MenuItem Item(string tile, string header)
        {
            page.TileMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            Click(ButtonOn(page, "\uE712", tile));
            return page.TileMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
        }
        void Choose(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal("More for Hot", AutomationProperties.GetName(ButtonOn(page, "\uE712", "Hot")));

        // Hot is monitored and has a score: its breakdown, then Stop.
        Choose(Item("Hot", "Why this score…"));
        page.TileMenu!.IsOpen = false;
        page.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        // Off screen a popup can't hold the mouse, so it closes at once: what it would show is checked.
        Assert.StartsWith("Hot · latest sample at", page.Breakdown.TxtSubtitle.Text);
        Assert.Same(ButtonOn(page, "", "Hot"), page.BreakdownPopup.PlacementTarget);
        page.BreakdownPopup.IsOpen = false;
        Choose(Item("Hot", "Stop monitoring"));
        Assert.False(monitoring.IsMonitoring(_hot.Id));

        // Quiet isn't: no score to explain, and Start.
        Assert.False(Item("Quiet", "Why this score…").IsEnabled);
        Choose(Item("Quiet", "Start monitoring"));
        Assert.True(monitoring.IsMonitoring(_quiet.Id));

        Choose(Item("Down", "Open"));
        Choose(Item("Down", "Edit connection…"));
        page.TileMenu!.IsOpen = false;
        Assert.Equal([_down.Id], opened);
        Assert.Equal([_down.Id], edited);
    });

    [Fact]
    public void Retry_TriesTheServerAgain_AndOpensItsDetails() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);
        var opened = new List<Guid>();
        page.OpenRequested += opened.Add;
        var before = _calls.Count(c => c.Server == "down" && c.Method == nameof(IWaitStatsRepository.GetLiveMetricsAsync));

        Click(ButtonOn(page, "Retry", "Down"));

        Assert.Equal([_down.Id], opened);
        Assert.Equal(before + 1, _calls.Count(c => c.Server == "down" && c.Method == nameof(IWaitStatsRepository.GetLiveMetricsAsync)));
    });

    [Fact]
    public void Start_OnATileThatIsntMonitored_StartsMonitoringIt_AndNotAnotherServer() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);

        Click(ButtonOn(page, "▶ Start monitoring", "Quiet"));

        Assert.True(monitoring.IsMonitoring(_quiet.Id));
        Assert.True(monitoring.IsMonitoring(_hot.Id));
        Assert.Equal(3, monitoring.MonitoredCount);
    });

    [Fact]
    public void Home_ContactsNoServer() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var before = _calls.Count;

        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);
        UiThread.Wait(page.RefreshAsync());

        Assert.Equal(before, _calls.Count);
    });

    // ── Server detail ────────────────────────────────────────────────────────

    [Fact]
    public void Detail_ShowsTheScoreAndChecks_WithALinkToThePageThatExplainsTheOneLosingPoints() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new ServerDetailPage(_hot.Id, monitoring, settings);
        UiThread.Layout(page, 1200, 1000);
        UiThread.Wait(page.RefreshAsync());
        UiThread.Layout(page, 1200, 1000);
        var opened = new List<(Guid, string)>();
        page.OpenPageRequested += (id, tag) => opened.Add((id, tag));

        Assert.Equal("CRITICAL", page.TxtStatus.Text);
        Assert.Equal("80", page.RunScore.Text);
        Assert.Equal("Lowered by CPU −20.", page.TxtReason.Text);
        Assert.Equal(Visibility.Collapsed, page.OutageRow.Visibility);
        Assert.Equal(Visibility.Collapsed, page.BtnMakeActive.Visibility);   // it's already active
        Assert.Equal(Visibility.Collapsed, page.BlockingCard.Visibility);    // nothing blocked

        var links = UiThread.Find<TextBlock>(page.Breakdown).SelectMany(t => t.Inlines.OfType<Hyperlink>()).ToList();
        var link  = Assert.Single(links);
        Assert.Equal("Top CPU queries", System.Windows.Automation.AutomationProperties.GetName(link));
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal([(_hot.Id, "ResourceQueries")], opened);
    });

    [Fact]
    public void Detail_OfAServerThatCantBeReached_SaysWhy_WhatToCheck_AndOffersRetry() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new ServerDetailPage(_down.Id, monitoring, settings);
        UiThread.Wait(page.RefreshAsync());
        UiThread.Layout(page, 1200, 1000);
        var madeActive = new List<Guid>();
        page.MakeActiveRequested += madeActive.Add;

        Assert.Equal("UNREACHABLE", page.TxtStatus.Text);
        Assert.Equal(Visibility.Visible, page.OutageRow.Visibility);
        Assert.Equal(Visibility.Collapsed, page.ScoreCard.Visibility);
        Assert.Equal("SqlVitals can't reach down", page.TxtOutageTitle.Text);
        Assert.Equal("No server in tests.", page.TxtOutageError.Text);
        Assert.StartsWith("Since ", page.TxtOutageSince.Text);
        Assert.Contains("The SQL Server service on down is running.", (IEnumerable<string>)page.ThingsList.ItemsSource);
        Assert.Equal("No sample has worked since monitoring started.", page.TxtBefore.Text);

        Click(page.BtnMakeActive);
        Assert.Equal([_down.Id], madeActive);
    });

    [Fact]
    public void Detail_OfARemovedConnection_SaysSo() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        var page = new ServerDetailPage(Guid.NewGuid(), monitoring, settings);
        UiThread.Layout(page, 1200, 1000);

        Assert.Equal(Visibility.Visible, page.TxtMissing.Visibility);
        Assert.Equal(Visibility.Collapsed, page.Main.Visibility);
    });

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
