using System.Collections.Concurrent;
using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Service;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The app's side of the collector service (#172): handing a server over and back, what the app
/// reads of what the service watches, the question Start asks, and monitoring leaving such a
/// server to the service. The service is a fake; its settings and history are real files.
/// </summary>
public sealed class CollectorLinkTests : IDisposable
{
    private const string Webhook = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeCollectorControl _control;
    private readonly ConnectionSettingsService _settings;
    private readonly FakeDialogService _dialogs = new();
    private readonly ConcurrentQueue<(string, string)> _calls = new();

    public CollectorLinkTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "collector"));
        _control  = new FakeCollectorControl(Path.Combine(_dir, "collector"));
        _settings = new ConnectionSettingsService(Path.Combine(_dir, "app"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a history file still closing */ }
    }

    private ConnectionSettings Save(string server, SqlAuthMode auth = SqlAuthMode.Windows, string password = "")
    {
        var conn = new ConnectionSettings
        {
            Server = server, Name = server.ToUpperInvariant(), Authentication = auth, UserName = auth == SqlAuthMode.Windows ? "" : "vitals",
            Password = password, SavePassword = password.Length > 0,
        };
        _settings.Upsert(conn);
        return conn;
    }

    private CollectorLink Link() => new(_control, _settings, new FakeUiDispatcher(), TimeProvider.System);

    private MonitoringManager Monitoring() =>
        new(new HistoryWriter(Path.Combine(_dir, "app-history.db"), (_, _) => { }),
            (conn, _) => MonitoringManagerTests.Counting(conn.Server, _calls));

    // The service's history, as it writes it: a score a minute ago, and an alert still open.
    private void ServiceHistory(Guid id, string server, bool reachable = true, int minutesAgo = 1, bool alert = false)
    {
        using var store = new HistoryStore(CollectorPaths.HistoryFile(_control.DataDirectory));
        store.Open();
        var conn = new HistoryConnection(id, server.ToUpperInvariant(), server, "(default database)");
        var at   = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var records = new List<HistoryRecord> { new HealthScoreRecord(conn, at, 81, reachable) };
        if (alert)
            records.Add(new AlertRecord(conn, at, new Alert(Guid.NewGuid(), id, HealthIndicator.Cpu, HealthLevel.Critical, at, null, 97, 90, "CPU 97%")));
        store.Write(records);
    }

    // ── Handing over and back ─────────────────────────────────────────────────

    [Fact]
    public void HandingOver_KeepsTheId_AndGivesTheServiceItsChannels_ForThatServerOnly()
    {
        var conn  = Save("sql01", SqlAuthMode.SqlServer, password: "Pa55word");
        var other = Save("sql02");
        _settings.SaveChatChannel(new ChatChannel { Name = "DBA", WebhookUrl = Webhook, CriticalOnly = true, HeartbeatEvery = HeartbeatEvery.Hours, HeartbeatHours = 6 });
        _settings.SaveChatChannel(new ChatChannel { Name = "Other team", WebhookUrl = Webhook + "x", AllConnections = false, ConnectionIds = [other.Id] });
        var store = _settings.Load();

        var service = CollectorHandover.HandOver(new CollectorSettings(), store, store.Connections.First(c => c.Id == conn.Id));

        var server = Assert.Single(service.Servers);
        Assert.Equal((conn.Id, "SQL01", "sql01", CollectorAuth.SqlLogin, "vitals", "Pa55word"),
                     (server.Id, server.Name, server.Server, server.Authentication, server.UserName, server.Password));
        var channel = Assert.Single(service.ChatChannels);
        Assert.Equal(("DBA", false, true, 6), (channel.Name, channel.AllConnections, channel.CriticalOnly, channel.HeartbeatHours));
        Assert.Equal([conn.Id], channel.ConnectionIds);

        Assert.True(CollectorHandover.TakeBack(service, conn.Id));
        Assert.Empty(service.Servers);
        Assert.Empty(service.ChatChannels);   // it posted for no other server
        Assert.False(CollectorHandover.TakeBack(service, conn.Id));
    }

    [Fact]
    public void APasswordTypedForThisRunOnly_IsntTheServicesToSave()
    {
        Save("sql01", SqlAuthMode.SqlServer);
        var typed = _settings.Load().Connections[0];
        typed.Password = "typed";   // as the session holds it, with Remember password off

        Assert.Contains("Remember password is off", CollectorHandover.WhyNot(typed));
    }

    [Fact]
    public async Task AServiceThatWontStart_GetsTheServerTakenBack_SoSqlVitalsCanWatchIt()
    {
        var conn = Save("sql01");
        _control.State = CollectorServiceState.Stopped;
        _control.StartFails = new OperationCanceledException("The administrator prompt was cancelled.");
        using var monitoring = Monitoring();
        monitoring.Sync(_settings.Load(), "test");
        var link = Link();
        await link.RefreshAsync();
        _dialogs.WatchAnswer = new WatchAnswer(WatchChoice.Service, Remember: false);

        await Starter(monitoring, link).StartAsync(conn.Id, "test");

        Assert.Empty(_control.Saved().Servers);
        Assert.Empty(link.View.HandedOver);
        Assert.True(monitoring.IsMonitoring(conn.Id));
        Assert.Contains(_dialogs.Shown, s => s.StartsWith("Warn: Start monitoring: Couldn't hand SQL01 to the collector service, so SqlVitals watches it instead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnotherPcsCopy_WhoseCollectorStoppedSampling_LeavesTheServerToThisPc()
    {
        var conn  = Save("sql01");
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        using (var store = new HistoryStore(Path.Combine(share, "live.db")))
        {
            store.Open();
            // Its last sample an hour before the copy was written: it no longer watches sql01.
            store.Write([new HealthScoreRecord(new HistoryConnection(Guid.NewGuid(), "Sales", "sql01", "(default database)"), DateTime.UtcNow.AddHours(-1), 64, true)]);
        }
        HistoryShareCopy.Write(Path.Combine(share, "live.db"), Path.Combine(share, HistoryShareCopy.FileName));
        _settings.SetCollectorHistoryPath(share);
        var link = Link();

        await link.RefreshAsync();

        Assert.Empty(link.View.Watches);
        Assert.Empty(link.View.Texts);
        // Its history is still read, for the charts.
        Assert.NotNull(link.View.Sources.Single().IdFor(conn.Id));
    }

    [Fact]
    public async Task AfterDispose_ARead_ChangesNothing()
    {
        Save("sql01");
        _control.State = CollectorServiceState.Running;
        var link = Link();
        var changes = 0;
        link.Changed += () => changes++;

        link.Dispose();
        await link.RefreshAsync();

        Assert.Equal(0, changes);
        Assert.False(link.View.IsInstalled);
    }

    [Theory]
    [InlineData(SqlAuthMode.EntraMfa, "", "Entra MFA")]
    [InlineData(SqlAuthMode.SqlServer, "", "password isn't saved")]
    public void AServerTheServiceCantSignInTo_IsntHandedOver(SqlAuthMode auth, string password, string why)
    {
        var conn = Save("sql01", auth, password);

        Assert.Contains(why, CollectorHandover.WhyNot(_settings.Load().Connections[0]));
        Assert.Throws<InvalidOperationException>(() => CollectorHandover.HandOver(new CollectorSettings(), _settings.Load(), conn));
    }

    [Fact]
    public async Task HandOverAsync_SavesTheServiceSettings_AndStartsAStoppedService()
    {
        var conn = Save("sql01");
        _control.State = CollectorServiceState.Stopped;
        var link = Link();

        await link.HandOverAsync(conn.Id);

        Assert.Equal(["save", "start"], _control.Calls);
        Assert.Equal(conn.Id, Assert.Single(_control.Saved().Servers).Id);
        Assert.Contains(conn.Id, link.View.HandedOver);
        Assert.Null(link.View.Watches[conn.Id].NotWatching);

        await link.TakeBackAsync(conn.Id);
        Assert.Empty(_control.Saved().Servers);
        Assert.Empty(link.View.Watches);
    }

    // ── What the app reads of it ──────────────────────────────────────────────

    [Fact]
    public async Task NotInstalled_TheViewIsEmpty_AndNothingChanges()
    {
        Save("sql01");
        var link = Link();

        await link.RefreshAsync();

        Assert.False(link.View.IsInstalled);
        Assert.Empty(link.View.Watches);
        Assert.Empty(link.View.Sources);
    }

    [Fact]
    public async Task AHandedOverServer_ShowsTheServicesLastScoreAndOpenAlerts()
    {
        var conn = Save("sql01");
        _control.State = CollectorServiceState.Running;
        _control.Save(CollectorHandover.HandOver(new CollectorSettings(), _settings.Load(), conn));
        ServiceHistory(conn.Id, "sql01", alert: true);
        var link = Link();
        var changes = 0;
        link.Changed += () => changes++;

        await link.RefreshAsync();
        await link.RefreshAsync();   // the same again: not a change

        var watch = link.View.Watches[conn.Id];
        Assert.Equal((81, true), (watch.Last!.Score, watch.Last.Reachable));
        Assert.Equal(conn.Id, Assert.Single(watch.ActiveAlerts).ConnectionId);
        Assert.Null(watch.NotWatching);
        Assert.StartsWith("Watched by the SqlVitals Collector service on this PC", link.View.Texts[conn.Id]);
        Assert.Single(link.View.Sources);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("stopped", 1, "which is stopped")]
    [InlineData("quiet", 30, "No sample from the SqlVitals Collector service since")]
    public async Task AServiceThatIsntWatching_SaysSo(string why, int minutesAgo, string says)
    {
        var conn = Save("sql01");
        _control.State = why == "stopped" ? CollectorServiceState.Stopped : CollectorServiceState.Running;
        _control.Save(CollectorHandover.HandOver(new CollectorSettings(), _settings.Load(), conn));
        ServiceHistory(conn.Id, "sql01", minutesAgo: minutesAgo);
        var link = Link();

        await link.RefreshAsync();

        Assert.Contains(says, link.View.Watches[conn.Id].NotWatching);
    }

    [Fact]
    public async Task SettingsSetUpByAnotherUser_SayWhy_AndCantBeChanged()
    {
        Save("sql01");
        _control.State  = CollectorServiceState.Running;
        _control.Denied = true;
        var link = Link();

        await link.RefreshAsync();

        Assert.False(link.View.CanEdit);
        Assert.Contains("set up by another Windows user", link.View.Problem);
        Assert.Empty(link.View.Watches);
    }

    [Fact]
    public async Task AnotherPcsShareCopy_WatchesTheServersThatMatch_ByServerAndDatabase()
    {
        var conn  = Save("sql01");
        Save("sql02");
        var share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        var theirs = Guid.NewGuid();
        using (var store = new HistoryStore(Path.Combine(share, "live.db")))
        {
            store.Open();
            store.Write([new HealthScoreRecord(new HistoryConnection(theirs, "Sales", "SQL01", "(default database)"), DateTime.UtcNow.AddMinutes(-2), 64, true)]);
        }
        HistoryShareCopy.Write(Path.Combine(share, "live.db"), Path.Combine(share, HistoryShareCopy.FileName));
        _settings.SetCollectorHistoryPath(share);
        var link = Link();

        await link.RefreshAsync();

        Assert.Equal([conn.Id], link.View.Watches.Keys);
        Assert.Equal(64, link.View.Watches[conn.Id].Last!.Score);
        Assert.Equal(theirs, link.View.Sources.Single().IdFor(conn.Id));
        Assert.Contains($"the collector at {share}", link.View.Texts[conn.Id]);
        Assert.Null(link.View.RemoteProblem);
    }

    // ── Monitoring leaves it to the service ───────────────────────────────────

    [Fact]
    public async Task AServerTheServiceWatches_IsntCollectedByTheApp_AndCountsAsMonitored()
    {
        var conn = Save("sql01");
        _settings.SetStartMonitoringAutomatically(true);
        _settings.SetActive(conn.Id);
        _control.State = CollectorServiceState.Running;
        using var monitoring = Monitoring();
        var watching = new List<WatchChange>();
        monitoring.WatchingChanged += watching.Add;
        monitoring.Sync(_settings.Load(), "test");
        Assert.True(monitoring.IsMonitoring(conn.Id));

        _control.Save(CollectorHandover.HandOver(new CollectorSettings(), _settings.Load(), conn));
        ServiceHistory(conn.Id, "sql01");
        var link = Link();
        await link.RefreshAsync();
        monitoring.SetWatchedElsewhere(link.View.Watches, link.View.Texts, link.View.Sources);

        Assert.False(monitoring.IsMonitoring(conn.Id));
        Assert.True(monitoring.IsWatchedElsewhere(conn.Id));
        Assert.Equal(1, monitoring.MonitoredCount);
        Assert.Empty(monitoring.NotMonitoredNames());
        Assert.Equal(MonitoringReason.WatchedElsewhere, monitoring.Decision(conn.Id)!.Reason);
        Assert.Equal(WatchStop.WatchedElsewhere, Assert.Single(watching.Last().Stopped).Why);
        Assert.IsType<CombinedHistoryReader>(monitoring.HistoryReader);
        Assert.Single(monitoring.HistoryReader.ReadScores(conn.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1), TimeSpan.FromMinutes(1)));
        Assert.NotNull(monitoring.WatchFor(conn.Id));

        // Taken back: the app watches it again.
        monitoring.SetWatchedElsewhere(new Dictionary<Guid, Engine.Home.ElsewhereWatch>(), new Dictionary<Guid, string>(), []);
        Assert.True(monitoring.IsMonitoring(conn.Id));
        Assert.IsType<HistoryReader>(monitoring.HistoryReader);
    }

    // ── What Start does ───────────────────────────────────────────────────────

    private WatchStarter Starter(MonitoringManager monitoring, CollectorLink link) => new(monitoring, link, _settings, _dialogs);

    [Fact]
    public async Task WithoutTheService_StartIsAsBefore_NoQuestion()
    {
        var conn = Save("sql01");
        using var monitoring = Monitoring();
        monitoring.Sync(_settings.Load(), "test");
        var link = Link();
        await link.RefreshAsync();

        await Starter(monitoring, link).StartAsync(conn.Id, "test");

        Assert.True(monitoring.IsMonitoring(conn.Id));
        Assert.Empty(_dialogs.Shown);
    }

    [Theory]
    [InlineData(WatchChoice.Service, false)]
    [InlineData(WatchChoice.Service, true)]
    [InlineData(WatchChoice.App, true)]
    public async Task WithTheService_StartAsks_AndDoesWhatsChosen_RememberingItWhenAsked(WatchChoice choice, bool remember)
    {
        var conn = Save("sql01");
        _control.State = CollectorServiceState.Running;
        using var monitoring = Monitoring();
        monitoring.Sync(_settings.Load(), "test");
        var link = Link();
        await link.RefreshAsync();
        _dialogs.WatchAnswer = new WatchAnswer(choice, remember);

        await Starter(monitoring, link).StartAsync(conn.Id, "test");

        Assert.Equal(["AskWhereToWatch: SQL01 (service running)"], _dialogs.Shown);
        Assert.Equal(choice == WatchChoice.Service, _control.Saved().Servers.Any(s => s.Id == conn.Id));
        Assert.Equal(choice == WatchChoice.App, monitoring.IsMonitoring(conn.Id));
        Assert.Equal(remember ? choice : WatchChoice.Ask, _settings.Load().WatchChoice);
    }

    [Fact]
    public async Task Cancelled_NothingStarts_AndARememberedChoice_IsntAsked()
    {
        var conn = Save("sql01");
        _control.State = CollectorServiceState.Running;
        using var monitoring = Monitoring();
        monitoring.Sync(_settings.Load(), "test");
        var link = Link();
        await link.RefreshAsync();

        await Starter(monitoring, link).StartAsync(conn.Id, "test");   // WatchAnswer null: Cancel
        Assert.False(monitoring.IsMonitoring(conn.Id));
        Assert.Empty(_control.Saved().Servers);

        _settings.SetWatchChoice(WatchChoice.App);
        _dialogs.Shown.Clear();
        await Starter(monitoring, link).StartAsync(conn.Id, "test");
        Assert.Empty(_dialogs.Shown);
        Assert.True(monitoring.IsMonitoring(conn.Id));
    }

    [Fact]
    public async Task AServerHandedToAStoppedService_StartOffersToStartIt()
    {
        var conn = Save("sql01");
        _control.State = CollectorServiceState.Stopped;
        _control.Save(CollectorHandover.HandOver(new CollectorSettings(), _settings.Load(), conn));
        using var monitoring = Monitoring();
        var link = Link();
        await link.RefreshAsync();
        monitoring.SetWatchedElsewhere(link.View.Watches, link.View.Texts, link.View.Sources);
        monitoring.Sync(_settings.Load(), "test");
        _dialogs.ConfirmAnswer = true;

        await Starter(monitoring, link).StartAsync(conn.Id, "test");

        Assert.StartsWith("Confirm: Start monitoring: SQL01 is handed to the SqlVitals Collector service, which is stopped", Assert.Single(_dialogs.Shown));
        Assert.Contains("start", _control.Calls);
        Assert.False(monitoring.IsMonitoring(conn.Id));
    }

    [Fact]
    public async Task AnEntraServer_IsWatchedInSqlVitals_WithoutAQuestion()
    {
        var conn = Save("sql01", SqlAuthMode.EntraMfa);
        _control.State = CollectorServiceState.Running;
        using var monitoring = Monitoring();
        monitoring.Sync(_settings.Load(), "test");
        var link = Link();
        await link.RefreshAsync();

        await Starter(monitoring, link).StartAsync(conn.Id, "test");

        Assert.Empty(_dialogs.Shown);
        Assert.Empty(_control.Saved().Servers);
    }
}
