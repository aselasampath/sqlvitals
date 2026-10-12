using System.IO;
using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Service;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// What the collectors watch, as the app last read it (#172): the service on this PC, and a
/// collector on another PC whose share copy the app reads. Empty when neither is set up: then the
/// app works exactly as it did without them.
/// </summary>
/// <param name="CanEdit">collector.json can be read and written by this Windows user: hand over and take back work.</param>
/// <param name="Problem">Why the service's settings couldn't be read; null when they could.</param>
/// <param name="HandedOver">The app connections in the service's settings, by id.</param>
/// <param name="Watches">Every app connection a collector watches, with what its history says now.</param>
/// <param name="Texts">The same connections' reasons, for <see cref="Engine.Monitoring.MonitoringPlan"/>.</param>
/// <param name="Sources">The collectors' history files, for <see cref="CombinedHistoryReader"/>.</param>
/// <param name="RemoteProblem">Why the other PC's copy couldn't be read; null when it could or none is set.</param>
public sealed record CollectorView(
    CollectorServiceState                     State,
    bool                                      CanEdit,
    string?                                   Problem,
    IReadOnlySet<Guid>                        HandedOver,
    IReadOnlyDictionary<Guid, ElsewhereWatch> Watches,
    IReadOnlyDictionary<Guid, string>         Texts,
    IReadOnlyList<HistorySource>              Sources,
    string?                                   RemotePath    = null,
    string?                                   RemoteProblem = null)
{
    public static readonly CollectorView None = new(
        CollectorServiceState.NotInstalled, false, null, new HashSet<Guid>(), new Dictionary<Guid, ElsewhereWatch>(),
        new Dictionary<Guid, string>(), []);

    public bool IsInstalled => State != CollectorServiceState.NotInstalled;
}

/// <summary>
/// The app's side of the collector service (#172): reads what it watches every
/// <see cref="CheckEvery"/>, hands servers to it and takes them back, and starts and stops it.
/// <see cref="MonitoringManager"/> stops collecting a server a collector watches, and reads its
/// history from both files (<see cref="MonitoringManager.SetWatchedElsewhere"/>).
/// </summary>
public sealed class CollectorLink : IDisposable
{
    /// <summary>How often the service's state and settings are read again: as often as it reads them.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);

    /// <summary>The service, as chat messages and tooltips call it.</summary>
    public const string ServiceName = "the SqlVitals Collector service";

    /// <summary>The service as a tile's footer names it, short enough to fit.</summary>
    public const string ShortName = "the collector service";

    // A collector samples every few seconds; one that has said nothing for this long before its
    // history was read (on this PC) or copied (another PC's share copy) isn't watching.
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(5);

    // The longest a share copy may go unwritten: the collector's longest interval, a day, and a
    // little. An older copy is a collector that has stopped writing it.
    private static readonly TimeSpan StaleCopy = TimeSpan.FromHours(26);

    private readonly ICollectorControl          _control;
    private readonly ConnectionSettingsService  _settings;
    private readonly IUiDispatcher              _ui;
    private readonly TimeProvider               _time;
    private readonly ILogger                    _log;
    private readonly Func<string, bool, IHistoryReader> _openHistory;
    private ITimer? _timer;
    private string  _signature = string.Empty;
    private string  _failureSaid = string.Empty;
    private volatile bool _disposed;

    // One read at a time: the timer's is skipped while one runs, an explicit one (after a hand
    // over) waits for it and reads again, so what it saved is what it shows.
    private readonly SemaphoreSlim _reading = new(1, 1);

    /// <param name="openHistory">Opens a collector's history file, read-only or not: the SQLite file, or a test's.</param>
    public CollectorLink(ICollectorControl control, ConnectionSettingsService settings, IUiDispatcher ui, TimeProvider time,
                         ILogger<CollectorLink>? log = null, Func<string, bool, IHistoryReader>? openHistory = null)
    {
        _control     = control;
        _settings    = settings;
        _ui          = ui;
        _time        = time;
        _log         = log ?? AppLog.For<CollectorLink>();
        _openHistory = openHistory ?? ((path, readOnly) => new HistoryReader(path, readOnly));
    }

    /// <summary>What the collectors watch, as last read; on the UI thread.</summary>
    public CollectorView View { get; private set; } = CollectorView.None;

    /// <summary>Raised on the UI thread when <see cref="View"/> says something new.</summary>
    public event Action? Changed;

    /// <summary>Reads now, then every <see cref="CheckEvery"/>.</summary>
    public void Start()
    {
        _timer ??= _time.CreateTimer(_ => _ = RefreshAsync(skipWhileBusy: true), null, CheckEvery, CheckEvery);
        _ = RefreshAsync();
    }

    /// <summary>Reads the service and the other PC's copy again, off the UI thread, and applies what changed.</summary>
    /// <param name="skipWhileBusy">The timer's: nothing when a read is running. Otherwise it waits for that one and reads again.</param>
    public async Task RefreshAsync(bool skipWhileBusy = false)
    {
        if (_disposed)
            return;
        if (skipWhileBusy ? !await _reading.WaitAsync(0).ConfigureAwait(false) : !await Enter().ConfigureAwait(false))
            return;
        try
        {
            // The settings service isn't thread-safe: read on the UI thread, like everything else.
            ConnectionStore store = null!;
            await _ui.InvokeAsync(() => store = _settings.Load()).ConfigureAwait(false);
            var (view, signature) = await Task.Run(() => Read(store)).ConfigureAwait(false);
            await _ui.InvokeAsync(() =>
            {
                // Exiting: monitoring is being disposed, and mustn't be told anything more.
                if (_disposed || signature == _signature)
                    return;
                _signature = signature;
                View = view;
                Changed?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Said("Collector: couldn't read what the collector service watches", ex);
        }
        finally
        {
            _reading.Release();
        }
    }

    private async Task<bool> Enter()
    {
        try
        {
            await _reading.WaitAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hands a connection to the service and starts the service when it's stopped. The app stops
    /// collecting it at the next read, which this does at once. Throws with the reason when it
    /// can't; a service that won't start (or an admin prompt cancelled) takes the server back
    /// first, so it's never left to a service that isn't running.
    /// </summary>
    public async Task HandOverAsync(Guid connectionId)
    {
        ConnectionStore store = null!;
        await _ui.InvokeAsync(() => store = _settings.Load()).ConfigureAwait(false);
        var connection = store.Connections.FirstOrDefault(c => c.Id == connectionId)
                         ?? throw new InvalidOperationException("That connection isn't saved any more.");
        var had = false;
        await Task.Run(() =>
        {
            var read = _control.Read();
            had = read.Settings.Servers.Any(s => s.Id == connectionId);
            _control.Save(CollectorHandover.HandOver(read.Settings, store, connection));
        }).ConfigureAwait(false);
        try
        {
            if (_control.State is CollectorServiceState.Stopped)
                await _control.StartAsync().ConfigureAwait(false);
        }
        catch when (!had)
        {
            await Task.Run(() =>
            {
                var settings = _control.Read().Settings;
                if (CollectorHandover.TakeBack(settings, connectionId))
                    _control.Save(settings);
            }).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
            throw;
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the service the Backups page's RPO (#174), when it watches one of this app's servers
    /// and counts against another. A service without the app's servers keeps its own.
    /// </summary>
    public async Task ApplyBackupRpoAsync(Engine.Backups.BackupRpo rpo, IReadOnlyCollection<Guid> appConnections)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(_control.DataDirectory))
                return;
            var settings = _control.Read().Settings;
            if (settings.BackupRpo == rpo || !settings.Servers.Any(s => appConnections.Contains(s.Id)))
                return;
            settings.BackupRpo = rpo;
            _control.Save(settings);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the service the app's choice of how query text is kept (#175), when it watches one of
    /// this app's servers and keeps it another way. A service without the app's servers keeps its own.
    /// </summary>
    public async Task ApplyQueryTextStorageAsync(Engine.History.QueryTextStorage storage, IReadOnlyCollection<Guid> appConnections)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(_control.DataDirectory))
                return;
            var settings = _control.Read().Settings;
            if (settings.QueryText == storage || !settings.Servers.Any(s => appConnections.Contains(s.Id)))
                return;
            settings.QueryText = storage;
            _control.Save(settings);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the service on this PC, when there is one, to clear the query text in its history too
    /// (#175): it owns that file, and does it within a settings check, or when it next starts.
    /// False when it isn't installed, even with its folder left behind.
    /// </summary>
    public async Task<bool> RequestQueryTextClearAsync()
    {
        return await Task.Run(() =>
        {
            if (_control.State == CollectorServiceState.NotInstalled || !Directory.Exists(_control.DataDirectory))
                return false;
            var settings = _control.Read().Settings;
            settings.QueryTextClearRequestedUtc = DateTime.UtcNow;
            _control.Save(settings);
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>Takes a connection back from the service: the app watches it again as its settings say.</summary>
    public async Task TakeBackAsync(Guid connectionId)
    {
        await Task.Run(() =>
        {
            var settings = _control.Read().Settings;
            if (CollectorHandover.TakeBack(settings, connectionId))
                _control.Save(settings);
        }).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task StartServiceAsync()
    {
        await _control.StartAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task StopServiceAsync()
    {
        await _control.StopAsync().ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    // Off the UI thread. The signature says whether anything the window shows changed.
    private (CollectorView View, string Signature) Read(ConnectionStore store)
    {
        var nowUtc  = _time.GetUtcNow().UtcDateTime;
        var app     = store.Connections.Select(c => new AppConnection(c.Id, c.Server, c.DatabaseLabel)).ToList();
        var watches = new Dictionary<Guid, ElsewhereWatch>();
        var texts   = new Dictionary<Guid, string>();
        var sources = new List<HistorySource>();
        var handed  = new HashSet<Guid>();
        var state   = _control.State;
        string? problem = null;
        var canEdit = false;

        if (state != CollectorServiceState.NotInstalled)
        {
            CollectorSettings? service = null;
            try
            {
                var read = _control.Read();
                service  = read.Settings;
                canEdit  = true;
            }
            catch (UnauthorizedAccessException)
            {
                problem = $"The collector service's settings aren't shared with you: {CollectorPaths.DataDirectory} was set up by another Windows user. " +
                          "Run SqlVitals.Collector.exe install as you, or ask them to hand servers over.";
            }
            catch (Exception ex) when (ex is CollectorSettingsException or IOException)
            {
                problem = ex.Message;
            }

            if (service is not null)
            {
                var history = _openHistory(CollectorPaths.HistoryFile(_control.DataDirectory), false);
                var servers = service.Servers
                    .Select(s => new StoredConnection(s.Id, s.DisplayName, s.Server.Trim(), s.DatabaseLabel, nowUtc)).ToList();
                // Watched now: what collector.json has. Read from it too: what it had before.
                var watched = CollectorMatch.Match(app, servers);
                var ids = new Dictionary<Guid, Guid>(CollectorMatch.Match(app, Safely(history.ReadConnections) ?? []));
                foreach (var (appId, fileId) in watched)
                    ids[appId] = fileId;
                sources.Add(new HistorySource(history, ServiceName, ids));
                handed.UnionWith(watched.Keys);

                foreach (var (appId, fileId) in watched)
                {
                    var server = service.Servers.First(s => s.Id == fileId);
                    var cant = server.Unusable ?? (server.NeedsPassword ? "its SQL login has no password the service can use" : null);
                    var (last, active) = Latest(history, fileId, appId, nowUtc);
                    var notWatching = state != CollectorServiceState.Running
                        ? $"Handed to {ServiceName}, which is {state.ToString().ToLowerInvariant()}."
                        : cant is not null ? $"{ServiceName} can't watch it: {cant}."
                        : last is { } l && nowUtc - l.TimeUtc > Quiet ? $"No sample from {ServiceName} since {l.TimeUtc.ToLocalTime():HH:mm}."
                        : null;
                    watches[appId] = new ElsewhereWatch(ShortName, last, active, notWatching);
                    texts[appId]   = notWatching is null
                        ? $"Watched by {ServiceName} on this PC, which keeps going when SqlVitals is closed."
                        : notWatching + " Start it, or take the server back, in Settings → Collector service.";
                }
            }
        }

        // A collector on another PC, from its share copy: what it watches that this PC doesn't.
        string? remotePath = null, remoteProblem = null;
        if (!string.IsNullOrWhiteSpace(store.CollectorHistoryPath))
        {
            remotePath = store.CollectorHistoryPath.Trim();
            var file = Directory.Exists(remotePath) ? Path.Combine(remotePath, HistoryShareCopy.FileName) : remotePath;
            if (!File.Exists(file))
            {
                remoteProblem = $"There's no collector history at {file}.";
            }
            else
            {
                var history = _openHistory(file, true);
                try
                {
                    var copiedUtc = File.GetLastWriteTimeUtc(file);
                    var map = CollectorMatch.Match(app.Where(a => !handed.Contains(a.Id)), history.ReadConnections());
                    // Read for the history all the same: what it recorded stays in the charts.
                    sources.Add(new HistorySource(history, $"the collector at {remotePath}", map));
                    if (nowUtc - copiedUtc > StaleCopy)
                        remoteProblem = $"The collector history at {file} was last written {copiedUtc.ToLocalTime():g}: that collector has stopped writing it, so this PC watches its servers.";
                    var by = $"the collector at {remotePath}";
                    foreach (var (appId, fileId) in map)
                    {
                        var (last, active) = Latest(history, fileId, appId, nowUtc);
                        // Watching only while it sampled when the copy was written, and the copy is
                        // current; otherwise this PC watches it, as without that collector.
                        if (remoteProblem is not null || last is null || copiedUtc - last.TimeUtc > Quiet)
                            continue;
                        watches[appId] = new ElsewhereWatch(by, last, active);
                        texts[appId]   = $"Watched by {by}. To watch it from this PC, clear that path in Settings → Collector service.";
                    }
                }
                catch (Exception ex)
                {
                    remoteProblem = $"The collector history at {file} couldn't be read: {ex.Message}";
                }
            }
        }

        var view = new CollectorView(state, canEdit, problem, handed, watches, texts, sources, remotePath, remoteProblem);
        var signature = string.Join("|", state, canEdit, problem, remotePath, remoteProblem,
                                    string.Join(",", watches.OrderBy(w => w.Key).Select(w =>
                                        $"{w.Key}:{w.Value.Last?.TimeUtc.Ticks}:{w.Value.Last?.Score}:{w.Value.NotWatching}:" +
                                        string.Join(";", w.Value.ActiveAlerts.Select(a => $"{a.Id}{a.Severity}")))),
                                    string.Join(",", texts.Values));
        return (view, signature);
    }

    // The last score of the last quarter of an hour, and the alerts still open, under the app's id.
    private (ScoreHistoryPoint? Last, IReadOnlyList<Alert> Active) Latest(IHistoryReader history, Guid fileId, Guid appId, DateTime nowUtc)
    {
        var last = LastOf(Safely(() => history.ReadScores(fileId, nowUtc - TimeSpan.FromMinutes(15), nowUtc.AddMinutes(1), TimeSpan.FromSeconds(1))));
        // With nothing in the quarter hour, the newest the file has, reachable or not.
        last ??= LastOf(Safely(() => history.ReadLastSampleTime(fileId) is { } t
                   ? history.ReadScores(fileId, t.AddSeconds(-1), t.AddSeconds(1), TimeSpan.FromSeconds(1))
                   : []));
        var active = (Safely(() => history.ReadAlerts(fileId, nowUtc.AddDays(-30), nowUtc.AddMinutes(1))) ?? [])
            .Where(a => a.Alert.IsActive)
            .Select(a => a.Alert with { ConnectionId = appId })
            .ToList();
        return (last, active);
    }

    private static ScoreHistoryPoint? LastOf(IReadOnlyList<ScoreHistoryPoint>? points) =>
        points is { Count: > 0 } ? points[^1] : null;

    private T? Safely<T>(Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Said("Collector: couldn't read the collector service's history", ex);
            return null;
        }
    }

    // A failure is logged once, not every 10 s, until another one is.
    private void Said(string message, Exception ex)
    {
        var text = $"{message}: {ex.GetType().Name}: {ex.Message}";
        if (Interlocked.Exchange(ref _failureSaid, text) != text)
            _log.Error(message, ex);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
