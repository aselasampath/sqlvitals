using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Home's view model on its own (#189): no page, no STA thread, the monitoring over fake servers.
/// Three saved connections: "Hot" (the active one, CPU at 99 %, so critical), "Down" (monitored in
/// the background, every collection fails) and "Quiet" (not monitored). It lists them most
/// attention first, follows the monitoring only while shown, keeps a breakdown open across samples
/// and reads, holds the order while the pointer is over the tiles, and says what a tile's menu offers.
/// </summary>
public sealed class HomeViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeShellStatus _shell = new();

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

    private IWaitStatsRepository Repository(ConnectionSettings conn) =>
        conn.Server == "hot" ? MonitoringManagerTests.Hot(conn.Server, _calls) : MonitoringManagerTests.Counting(conn.Server, _calls);

    // The connections saved, Hot active, monitoring started automatically, and a sample from each monitored one.
    private HomeViewModel Connected(params ConnectionSettings[] connections)
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in connections)
            _settings.Upsert(conn);
        if (connections.Length > 0)
            _settings.SetActive(connections[0].Id);
        _settings.SetStartMonitoringAutomatically(true);

        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (conn, _) => Repository(conn));
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);
        Sample(_hot);
        Sample(_down);

        var model = new HomeViewModel(_monitoring, _settings, new DirectWatchStarter(_monitoring), _ui);
        model.Connect("Home", _shell, new RefreshScheduler(TimeProvider.System, _ui));
        return model;
    }

    private HomeViewModel Connected() => Connected(_hot, _down, _quiet);

    // A sample from a monitored connection, applied before it returns; Down's fails.
    private void Sample(ConnectionSettings conn)
    {
        if (_monitoring!.Get(conn.Id) is not { } session)
            return;
        try { UiThread.Wait(session.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }
        if (conn == _hot)
            MonitoringManagerTests.WaitForServerChecks(session);
    }

    private static HomeTile Tile(HomeViewModel model, ConnectionSettings conn) => model.Tiles.Single(t => t.Tile.Id == conn.Id);

    // ── Loaded ───────────────────────────────────────────────────────────

    [Fact]
    public void Loaded_ListsEveryConnection_MostAttentionFirst_AndCountsThem() => OnDispatcher.Run(() =>
    {
        var model = Connected();

        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Equal(["Down", "Hot", "Quiet"], model.Tiles.Select(t => t.Tile.Name));
        Assert.Equal([TileStatus.Unreachable, TileStatus.Critical, TileStatus.Off], model.Tiles.Select(t => t.Tile.Status));
        Assert.Equal("3 connections · 2 monitored · sorted by most attention needed", model.Headline);
        Assert.Equal([TileStatus.Unreachable, TileStatus.Critical], model.Counts.Select(c => c.Status));
        Assert.False(model.ShowsNote);
        // Its view of a tile: the score's bars by level, Start where it isn't monitored.
        var hot = Tile(model, _hot);
        Assert.True(hot.ShowsScore);
        Assert.Equal(HealthLevel.Critical, hot.Bars.Single(b => b.Label == "CPU").Level);
        Assert.True(Tile(model, _quiet).IsOff);
        // Home contacts no server: the samples were the monitoring's.
        Assert.DoesNotContain(_calls, c => c.Server == "quiet");
    });

    [Fact]
    public void NothingSaved_SaysToAddOne() => OnDispatcher.Run(() =>
    {
        var model = Connected([]);

        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.Empty(model.Tiles);
        Assert.Equal("No saved connections yet. Add one to see its health here.", model.Note);
        Assert.True(model.ShowsNote);
        Assert.Equal("0 connections", model.Headline.Split(" · ")[0]);
    });

    [Fact]
    public void NothingMonitored_SaysHowToStart() => OnDispatcher.Run(() =>
    {
        var model = Connected(_quiet);
        _monitoring!.Stop(_quiet.Id, ActivitySources.Home);

        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.Equal(TileStatus.Off, Assert.Single(model.Tiles).Tile.Status);
        Assert.StartsWith("Nothing is monitored, so no scores are being taken.", model.Note);
    });

    [Fact]
    public void AHistoryFromANewerSqlVitals_LeavesTheSparklinesToTheLiveScores() => OnDispatcher.Run(() =>
    {
        // Written by a later version: this one can't read it, and says nothing about it.
        Directory.CreateDirectory(_dir);
        using (var file = new SqliteConnection($"Data Source={Path.Combine(_dir, "history.db")};Pooling=False"))
        {
            file.Open();
            using var command = file.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {HistoryStore.SchemaVersion + 1};";
            command.ExecuteNonQuery();
        }
        var model = Connected();

        UiThread.Wait(model.OnNavigatedToAsync());

        Assert.Equal(["Read Load"], _shell.Reports);
        Assert.Equal(3, model.Tiles.Count);
        Assert.All(model.Tiles, t => Assert.Null(t.Coverage));
        Assert.Single(Tile(model, _hot).Trend.Points);   // the live score only
    });

    // ── While shown, and after ───────────────────────────────────────────

    [Fact]
    public void ASample_MovesItsTile_AndTheTopBar_OnlyWhileShown() => OnDispatcher.Run(() =>
    {
        var model = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        var hot    = Tile(model, _hot);
        var before = hot.Tile;

        Sample(_hot);
        Assert.Equal(["Read Load", "Read Timed"], _shell.Reports);
        Assert.NotSame(before, hot.Tile);

        model.OnNavigatedFrom();
        _monitoring!.Stop(_hot.Id, ActivitySources.Home);
        Assert.Equal(TileStatus.Critical, hot.Tile.Status);   // not followed any more
        Assert.Equal(["Read Load", "Read Timed"], _shell.Reports);

        // Shown again, it reads the tiles as they are.
        UiThread.Wait(model.OnNavigatedToAsync());
        Assert.Equal(TileStatus.Off, Tile(model, _hot).Tile.Status);
    });

    [Fact]
    public void ABreakdownLeftOpen_FollowsItsServer_AcrossSamplesAndReads() => OnDispatcher.Run(() =>
    {
        var model = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        var hot = Tile(model, _hot);

        model.MenuFor(hot).Single(c => c?.Header == "Why this score…")!.Choose();
        Assert.Equal(_hot.Id, model.BreakdownFor);
        var first = model.Breakdown!;
        Assert.Equal(("Hot", false), (first.Name, first.Paused));

        Sample(_hot);
        Assert.NotSame(first, model.Breakdown);
        Assert.Equal("Hot", model.Breakdown!.Name);
        UiThread.Wait(model.RefreshAsync());
        Assert.Equal(_hot.Id, model.BreakdownFor);
        Assert.Same(_monitoring!.Get(_hot.Id)!.Score, model.Breakdown!.Score);

        // Stopped: what it says without a running session.
        _monitoring.Stop(_hot.Id, ActivitySources.Home);
        Assert.True(model.Breakdown!.Paused || model.Breakdown.Score is null);

        model.BreakdownFor = null;   // the popup closed
        Assert.Null(model.Breakdown);
    });

    [Fact]
    public void LeavingWhileTheHistoryReads_DropsWhatItRead() => OnDispatcher.Run(() =>
    {
        var model = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        Assert.NotNull(Tile(model, _quiet).Coverage);

        var reading = model.RefreshAsync();
        model.OnNavigatedFrom();
        UiThread.Wait(reading);

        // Left before it finished: cancelled, and said nowhere.
        Assert.False(model.IsBusy);
        Assert.Equal(["Read Load"], _shell.Reports);
    });

    [Fact]
    public void ANewOrder_WaitsWhileThePointerIsOverTheTiles() => OnDispatcher.Run(() =>
    {
        var model = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        Assert.Equal(["Down", "Hot", "Quiet"], model.Tiles.Select(t => t.Tile.Name));

        model.HoldOrder = true;
        _monitoring!.Stop(_down.Id, ActivitySources.Home);   // Down is off now: it would go last
        Assert.Equal(["Down", "Hot", "Quiet"], model.Tiles.Select(t => t.Tile.Name));

        model.HoldOrder = false;
        Assert.Equal(["Hot", "Down", "Quiet"], model.Tiles.Select(t => t.Tile.Name));
    });

    [Fact]
    public void AConnectionSavedElsewhere_AddsItsTile_OnTheUiThread() => OnDispatcher.Run(() =>
    {
        var model = Connected(_hot, _quiet);
        UiThread.Wait(model.OnNavigatedToAsync());

        var added = Conn("new", "New");
        _settings!.Upsert(added);
        Assert.Equal(2, model.Tiles.Count);   // told on the settings' thread: posted

        _ui.RunPosted();
        Assert.Contains(model.Tiles, t => t.Tile.Name == "New");
    });

    // ── Its buttons and menu ─────────────────────────────────────────────

    [Fact]
    public void ATilesButton_StartsOpensOrRetries_AsTheTileSays() => OnDispatcher.Run(() =>
    {
        var model  = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        var opened = new List<Guid>();
        model.OpenRequested += opened.Add;
        int TriesOfDown() => _calls.Count(c => c.Server == "down" && c.Method == nameof(IWaitStatsRepository.GetLiveMetricsAsync));
        var tries = TriesOfDown();

        UiThread.Wait(model.ActOnTileCommand.ExecuteAsync(Tile(model, _quiet)));
        Assert.True(_monitoring!.IsMonitoring(_quiet.Id));
        Assert.Empty(opened);

        UiThread.Wait(model.ActOnTileCommand.ExecuteAsync(Tile(model, _hot)));
        UiThread.Wait(model.ActOnTileCommand.ExecuteAsync(Tile(model, _down)));
        Assert.Equal([_hot.Id, _down.Id], opened);
        UiThread.WaitUntil(() => TriesOfDown() > tries);   // Retry tried it again
    });

    [Fact]
    public void ATilesMenu_OffersWhatItsServerAllows() => OnDispatcher.Run(() =>
    {
        var model  = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        var edited = new List<Guid>();
        var asked  = new List<HomeTile>();
        model.EditRequested += edited.Add;
        model.MenuRequested += asked.Add;

        model.ShowMenuCommand.Execute(Tile(model, _hot));
        Assert.Same(Tile(model, _hot), Assert.Single(asked));

        var hot = model.MenuFor(Tile(model, _hot));
        Assert.Equal(["Open", "Stop monitoring", "Why this score…", null, "Edit connection…"], hot.Select(c => c?.Header));
        Assert.All(hot.OfType<HomeViewModel.MenuChoice>(), c => Assert.True(c.IsEnabled));
        hot[1]!.Choose();
        Assert.False(_monitoring!.IsMonitoring(_hot.Id));

        var quiet = model.MenuFor(Tile(model, _quiet));
        Assert.Equal("Start monitoring", quiet[1]!.Header);
        Assert.False(quiet[2]!.IsEnabled);   // no score to explain
        quiet[4]!.Choose();
        Assert.Equal([_quiet.Id], edited);
    });

    [Fact]
    public void Investigate_OpensTheWorstAlertsEvidence_AndAddAConnection_AsksForSettings() => OnDispatcher.Run(() =>
    {
        var model = Connected();
        UiThread.Wait(model.OnNavigatedToAsync());
        var alerts = new List<Engine.Alerts.Alert>();
        var adds   = 0;
        model.InvestigateRequested   += alerts.Add;
        model.AddConnectionRequested += () => adds++;

        // Hot alerts after a few samples at 99 % CPU.
        for (var i = 0; i < 3; i++)
            Sample(_hot);
        var hot = Tile(model, _hot);
        Assert.True(hot.ShowsInvestigate);
        model.InvestigateCommand.Execute(hot);
        Assert.Equal(HealthIndicator.Cpu, Assert.Single(alerts).Indicator);

        model.InvestigateCommand.Execute(Tile(model, _quiet));   // none to open
        model.AddConnectionCommand.Execute(null);
        Assert.Single(alerts);
        Assert.Equal(1, adds);
    });

    public void Dispose()
    {
        _monitoring?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
