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
/// Home (#130) as WPF builds it, off screen, over three saved
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
        ? MonitoringManagerTests.Hot(conn.Server, _calls)
        : MonitoringManagerTests.Counting(conn.Server, _calls);

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

    // A day of a connection's scores in the history (#170): every 5 minutes, except a 10-hour
    // night with none, ending a minute ago. Written before the monitoring opens the file.
    private void SeedADayWithANightOff(ConnectionSettings conn)
    {
        var now   = DateTime.UtcNow;
        var night = (From: now.AddHours(-20), To: now.AddHours(-10));
        var who   = new HistoryConnection(conn.Id, conn.DisplayName, conn.Server, "");
        var records = new List<HistoryRecord>();
        for (var t = now.AddHours(-24).AddMinutes(1); t < now; t = t.AddMinutes(5))
            if (t < night.From || t > night.To)
                records.Add(new HealthScoreRecord(who, t, 90, Reachable: true));

        Directory.CreateDirectory(_dir);
        using var store = new HistoryStore(Path.Combine(_dir, "history.db"));
        store.Open();
        store.Write(records);
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

    // Quiet handed to the collector service (#172): its last score 72, and a critical CPU alert open.
    private void QuietWatchedByTheService(MonitoringManager monitoring)
    {
        var alert = new Engine.Alerts.Alert(Guid.NewGuid(), _quiet.Id, Engine.Monitoring.HealthIndicator.Cpu, Engine.Monitoring.HealthLevel.Critical,
                                            DateTime.UtcNow.AddMinutes(-4), null, 97, 90, "CPU 97%");
        monitoring.SetWatchedElsewhere(
            new Dictionary<Guid, ElsewhereWatch>
            {
                [_quiet.Id] = new(CollectorLink.ShortName, new ScoreHistoryPoint(DateTime.UtcNow.AddSeconds(-20), 72, true), [alert]),
            },
            new Dictionary<Guid, string> { [_quiet.Id] = "Watched by the SqlVitals Collector service on this PC." },
            []);
    }

    [Fact]
    public void AServerTheServiceWatches_ShowsWhatItsHistorySays_AndCountsAsMonitored() => UiThread.Run(() =>
    {
        var (monitoring, settings) = Setup();
        QuietWatchedByTheService(monitoring);
        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);

        var quiet = page.TilesShown.Single(t => t.Tile.Name == "Quiet").Tile;
        Assert.Equal((TileStatus.Critical, "72", "Alerting on CPU."), (quiet.Status, quiet.ScoreText, quiet.Reason));
        Assert.EndsWith(" · by the collector service", quiet.Footer);
        Assert.Equal(TileAction.Open, quiet.Action);
        var texts = UiThread.Find<TextBlock>(page).Where(t => VisibleUpTo(t, page)).Select(t => t.Text).ToList();
        Assert.Contains("3 connections · 3 monitored · sorted by most attention needed", texts);
        // The app doesn't collect it.
        Assert.Null(monitoring.Get(_quiet.Id));
    });

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
        // The retry reads on the thread pool (#171).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (_calls.Count(c => c.Server == "down" && c.Method == nameof(IWaitStatsRepository.GetLiveMetricsAsync)) == before)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "Retry didn't try the server.");
            Thread.Sleep(5);
        }
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

    [Fact]
    public void ATile_SaysHowMuchOfTheLastDayWasMonitored() => UiThread.Run(() =>
    {
        SeedADayWithANightOff(_quiet);
        var (monitoring, settings) = Setup();

        var page = new HomePage(monitoring, settings);
        UiThread.Layout(page, 1100, 900);
        UiThread.Wait(page.RefreshAsync());

        var quiet = page.TilesShown.Single(t => t.Tile.Id == _quiet.Id);
        Assert.Equal("Monitored 14 h of the last 24 h", quiet.CoverageText);
        Assert.StartsWith("Longest gap ", quiet.CoverageToolTip);
        Assert.Contains("(10 h", quiet.CoverageToolTip);
        Assert.Contains("Grey: not monitored", quiet.TrendToolTip);
    });

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
