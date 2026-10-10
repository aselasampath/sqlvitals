using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Service;
// The namespace SqlVitals.Collector would hide the Engine type.
using ServerCollector = SqlVitals.Engine.Monitoring.Collector;

namespace SqlVitals.Collector;

/// <summary>The repositories a watched server is read through: its live metrics and its history detail.</summary>
internal sealed record ServerRepositories(ILiveMetricsRepository Live, IHistoryRepository History);

/// <summary>
/// The collector service's monitoring (#172), without the service around it: an Engine
/// <see cref="ServerCollector"/> per server in collector.json, all saving to the service's own
/// history.db, and the chat channels told about their alerts, what is watched, and the heartbeats
/// (#170). The settings file is checked every <see cref="SettingsCheckEvery"/>, so a server added
/// by the command line, or handed over by the app, starts without restarting the service.
///
/// What the app's MonitoringManager and ChatNotificationService do on its UI thread is done here
/// on one thread of the host's own (<see cref="SerialContext"/>), in the same order: a server's
/// alert ends reach the channels before the message that says it is no longer watched.
/// </summary>
public sealed class CollectorHost : IDisposable
{
    /// <summary>How often the settings file is checked for a change.</summary>
    public static readonly TimeSpan SettingsCheckEvery = TimeSpan.FromSeconds(10);

    private readonly string                 _dataDirectory;
    private readonly ISecretProtector       _protector;
    private readonly Func<CollectorServer, CollectorSettings, ServerRepositories> _repositories;
    private readonly IWebhookSender         _sender;
    private readonly ILogger                _log;
    private readonly TimeProvider           _time;
    private readonly SerialContext          _serial;
    private readonly ConcurrentQueue<(Guid Id, CollectorUpdate Update)> _updates = new();
    private int _drainPosted;

    // From here on, the host's thread only.
    private readonly Dictionary<Guid, Watched>  _watched    = new();
    private readonly Dictionary<Guid, DateTime> _stoppedUtc = new();
    private HistoryWriter?           _history;
    private HistoryReader?           _reader;
    private ChatNotificationService? _chat;
    private ITimer?                  _settingsTimer;
    private (DateTime WrittenUtc, long Length)? _fileStamp;
    private string                   _problemsSaid = string.Empty;
    private bool                     _started;
    private bool                     _stopped;

    // One watched server: its settings as started, and its collector.
    private sealed class Watched(CollectorServer server, ServerCollector collector, HistoryRecorder recorder, string fingerprint)
    {
        public CollectorServer Server      { get; } = server;
        public ServerCollector Collector   { get; } = collector;
        public HistoryRecorder Recorder    { get; } = recorder;
        public string          Fingerprint { get; } = fingerprint;
        public int             Failures    { get; set; }
    }

    /// <param name="dataDirectory">Where collector.json, history.db and the logs are: <see cref="CollectorPaths.DataDirectory"/>, or a test's folder.</param>
    /// <param name="repositories">A server's repositories: the real ones, or a test's fakes.</param>
    /// <param name="sender">Posts to the chat channels: <see cref="HttpWebhookSender"/>, or a test's fake. Disposed with the chat service.</param>
    internal CollectorHost(string dataDirectory, ISecretProtector protector,
                           Func<CollectorServer, CollectorSettings, ServerRepositories> repositories,
                           IWebhookSender sender, ILogger log, TimeProvider? time = null)
    {
        _dataDirectory = dataDirectory;
        _protector     = protector;
        _repositories  = repositories;
        _sender        = sender;
        _log           = log;
        _time          = time ?? TimeProvider.System;
        _serial        = new SerialContext("SqlVitals collector", (message, ex) => _log.Error(message, ex));
    }

    /// <summary>The real repositories: one <see cref="WaitStatsRepository"/> per server.</summary>
    internal static ServerRepositories RealRepositories(CollectorServer server, CollectorSettings settings)
    {
        var repository = new WaitStatsRepository(server.Target(settings.CommandTimeoutSeconds));
        return new ServerRepositories(repository, repository);
    }

    /// <summary>"SRV01 (collector service)": who the chat messages say is watching.</summary>
    public static string PcName => $"{Environment.MachineName} (collector service)";

    public string SettingsPath => CollectorPaths.SettingsFile(_dataDirectory);

    public string HistoryPath => CollectorPaths.HistoryFile(_dataDirectory);

    /// <summary>
    /// Reads the settings, opens the history and starts watching. Fails, and watches nothing,
    /// when the settings file can't be read at all: the service then doesn't start, and says why.
    /// </summary>
    public Task StartAsync() => _serial.RunAsync(() =>
    {
        if (_started)
            return;
        var read = LoadSettings();
        _started = true;

        _history = new HistoryWriter(HistoryPath, (message, ex) => _log.Error(message, ex),
                                     TimeSpan.FromDays(read.Settings.HistoryRetentionDays), _time,
                                     onActivityNotSaved: e => _log.Info($"Activity not saved to the history: {e.Event} · {e.ConnectionName ?? "all servers"}"));
        // Queued before anything is collected, so it can only end what an earlier run left active.
        _history.EndAlertsLeftOpen();
        _reader = new HistoryReader(HistoryPath);

        var version = typeof(CollectorHost).Assembly.GetName().Version;
        Log(new ActivityEntry(_time.GetUtcNow().UtcDateTime, ActivityEvents.AppStarted, null, null, ActivitySources.CollectorService,
                              Detail: $"SqlVitals Collector {version?.Major}.{version?.Minor}.{version?.Build}"));
        _log.Info($"SqlVitals Collector {version?.Major}.{version?.Minor}.{version?.Build} started: settings {SettingsPath}, history {HistoryPath}");

        _chat = new ChatNotificationService(_sender, PcName, text => _log.Info(text), _serial, _time);
        Apply(read, ActivitySources.CollectorService);

        _settingsTimer = _time.CreateTimer(_ => _serial.Post(_ => CheckSettings(), null), null, SettingsCheckEvery, SettingsCheckEvery);
    });

    /// <summary>
    /// Stops watching: every channel is told why (<see cref="WatchStop.ServiceStopped"/>), with
    /// the alerts that were still active, and gets a few seconds to send it; then the history is
    /// flushed. Windows gives a stopping service about 20 seconds; this takes under 10.
    /// </summary>
    public async Task StopAsync()
    {
        var stopping = false;
        await _serial.RunAsync(() =>
        {
            if (!_started || _stopped)
                return;
            _stopped = stopping = true;
            _settingsTimer?.Dispose();
            _chat!.BeginExit(WatchStop.ServiceStopped);
            foreach (var id in _watched.Keys.ToList())
                StopWatching(id, ActivitySources.CollectorService, "The collector service stopped.");
            Drain();
            _chat.OnAllStopped();
            Log(new ActivityEntry(_time.GetUtcNow().UtcDateTime, ActivityEvents.AppExited, null, null, ActivitySources.CollectorService));
        }).ConfigureAwait(false);
        if (!stopping)
            return;

        // Off the host's thread, which the queues never need: they send meanwhile.
        _chat!.Shutdown(ChatNotificationService.ExitTime);
        _chat.Dispose();
        // After the collectors, so their last samples are queued; waits a few seconds at most.
        _history!.Dispose();
        _log.Info("SqlVitals Collector stopped.");
    }

    /// <summary>The servers being watched now, by id: for tests and the log.</summary>
    internal async Task<IReadOnlyList<Guid>> WatchingAsync()
    {
        IReadOnlyList<Guid> ids = [];
        await _serial.RunAsync(() => ids = _watched.Keys.ToList()).ConfigureAwait(false);
        return ids;
    }

    /// <summary>Checks the settings file now rather than at the next check: for tests.</summary>
    internal Task CheckSettingsNowAsync() => _serial.RunAsync(CheckSettings);

    // On the host's thread: the file changed (written by the command line or the app), so the
    // servers and channels are applied again. A file that can't be read keeps what runs now.
    private void CheckSettings()
    {
        if (!_started || _stopped || Stamp() == _fileStamp)
            return;
        try
        {
            Apply(LoadSettings(), ActivitySources.CollectorSettings);
        }
        catch (CollectorSettingsException ex)
        {
            // Said once: the file's stamp is kept, so the next check waits for it to change again.
            _log.Error("Collector: the changed settings couldn't be read, so the servers and channels stay as they were", ex);
        }
        catch (Exception ex)
        {
            _log.Error("Collector: applying the changed settings failed part way; they are applied again when the file next changes", ex);
        }
    }

    private CollectorSettingsRead LoadSettings()
    {
        // Taken before reading, so a write while it reads is seen at the next check; kept when
        // the file can't be read, so a broken file is said once rather than at every check.
        _fileStamp = Stamp();
        return CollectorSettingsFile.Load(SettingsPath, _protector);
    }

    private (DateTime, long)? Stamp()
    {
        try
        {
            var info = new FileInfo(SettingsPath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _fileStamp;
        }
    }

    // On the host's thread: starts, stops or restarts collectors to match the settings, then
    // tells the channels, as MonitoringManager.Sync does for the app.
    private void Apply(CollectorSettingsRead read, string source)
    {
        var settings = read.Settings;
        SayProblems(settings, read.Problems);
        _history!.Retention = TimeSpan.FromDays(settings.HistoryRetentionDays);

        var wanted    = settings.Servers.Where(CanWatch).ToDictionary(s => s.Id);
        var started   = new List<WatchedConnection>();
        var stopped   = new List<StoppedWatching>();
        var restarted = new List<Guid>();

        foreach (var (id, watched) in _watched.ToList())
        {
            if (settings.Servers.All(s => s.Id != id))
            {
                StopWatching(id, source, "The server was removed from the collector's settings.");
                stopped.Add(new StoppedWatching(id, watched.Server.DisplayName, WatchStop.ConnectionRemoved));
            }
            else if (!wanted.TryGetValue(id, out var server))
            {
                StopWatching(id, source, settings.Servers.First(s => s.Id == id).Unusable is { } why
                    ? $"It can't be watched: {why}."
                    : "Its SQL login has no password the service can use.");
                stopped.Add(new StoppedWatching(id, watched.Server.DisplayName, WatchStop.NeedsSetup));
            }
            else if (server.Fingerprint(settings.CommandTimeoutSeconds) != watched.Fingerprint)
            {
                // Started again below with its new details.
                StopWatching(id, ActivitySources.ConnectionEdited, "Restarted with the server's new details.");
                restarted.Add(id);
            }
        }

        foreach (var server in wanted.Values.Where(s => !_watched.ContainsKey(s.Id)))
        {
            var again = restarted.Contains(server.Id);
            // Read before it starts, so its first sample can't be taken for the last one.
            if (!again)
                started.Add(new WatchedConnection(server.Id, server.DisplayName, LastWatched(server.Id)));
            StartWatching(server, settings, again ? ActivitySources.ConnectionEdited : source);
        }

        // Thresholds, alert samples and intervals aren't part of the fingerprint: they reach the
        // running collectors here.
        var detail = TimeSpan.FromMinutes(settings.HistoryIntervalMinutes);
        foreach (var watched in _watched.Values)
        {
            var server = wanted[watched.Server.Id];
            watched.Collector.Thresholds      = settings.ThresholdsFor(server);
            watched.Collector.AlertSamples    = settings.AlertSamples;
            watched.Collector.IntervalSeconds = settings.SampleSeconds;
            watched.Recorder.DetailInterval   = detail;
        }

        // The alert ends of the servers that stopped, before the message saying they stopped.
        Drain();
        _chat!.Apply(settings.ChatChannels, settings.Servers.ToDictionary(s => s.Id, s => s.DisplayName));
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var stop in stopped)
            _stoppedUtc[stop.Id] = now;
        var change = new WatchChange(started, stopped, restarted);
        if (!change.IsEmpty)
            _chat.OnWatchingChanged(change);

        if (started.Count > 0 || stopped.Count > 0 || restarted.Count > 0 || source == ActivitySources.CollectorService)
            _log.Info(_watched.Count == 0
                ? $"Collector: watching no servers. Add one: SqlVitals.Collector add-server --server <name> (settings {SettingsPath})"
                : $"Collector: watching {_watched.Count} server(s): {string.Join(", ", _watched.Values.Select(w => w.Server.DisplayName))}; " +
                  $"{settings.ChatChannels.Count(c => c.Enabled)} chat channel(s) on");
    }

    private static bool CanWatch(CollectorServer server) =>
        !string.IsNullOrWhiteSpace(server.Server) && !server.NeedsPassword && server.Unusable is null;

    // Each problem once, until the settings change what they say.
    private void SayProblems(CollectorSettings settings, IReadOnlyList<string> problems)
    {
        var all = problems.Concat(settings.Servers
            .Where(s => s.NeedsPassword && !s.PasswordUnreadable)
            .Select(s => $"{s.DisplayName}: its SQL login has no password, so it isn't watched. " +
                         $"Set one: SqlVitals.Collector set-password \"{s.DisplayName}\""))
            .ToList();
        var said = string.Join("\n", all);
        if (said == _problemsSaid)
            return;
        _problemsSaid = said;
        foreach (var problem in all)
            _log.Warning($"Collector: {problem}");
    }

    // When a server was last watched: when it stopped this run, else its last sample in the
    // history. A history that can't be read gives no gap rather than failing the start.
    private DateTime? LastWatched(Guid id)
    {
        if (_stoppedUtc.TryGetValue(id, out var stopped))
            return stopped;
        try { return _reader!.ReadLastSampleTime(id); }
        catch (Exception ex)
        {
            _log.Error("Collector: couldn't read when a server was last watched", ex);
            return null;
        }
    }

    private void StartWatching(CollectorServer server, CollectorSettings settings, string source)
    {
        var id           = server.Id;
        var repositories = _repositories(server, settings);
        var recorder     = new HistoryRecorder(
            new HistoryConnection(id, server.DisplayName, server.Server.Trim(), server.DatabaseLabel),
            repositories.History, _history!, (message, ex) => _log.Error(message, ex), _time)
        {
            DetailInterval = TimeSpan.FromMinutes(settings.HistoryIntervalMinutes),
        };
        var collector = new ServerCollector(id, repositories.Live, settings.SampleSeconds, recorder, _time, Secrets.MaskPasswords)
        {
            Thresholds   = settings.ThresholdsFor(server),
            AlertSamples = settings.AlertSamples,
        };
        collector.Updated += update => OnUpdated(id, update);
        _watched[id] = new Watched(server.Clone(), collector, recorder, server.Fingerprint(settings.CommandTimeoutSeconds));
        collector.Start();

        Log(new ActivityEntry(_time.GetUtcNow().UtcDateTime, ActivityEvents.MonitoringStarted, id, server.DisplayName, source,
                              Detail: "Watched by the collector service."));
    }

    // Its alert ends are queued by its Dispose; the caller drains them.
    private void StopWatching(Guid id, string source, string detail)
    {
        if (!_watched.Remove(id, out var watched))
            return;
        watched.Collector.Dispose();
        Log(new ActivityEntry(_time.GetUtcNow().UtcDateTime, ActivityEvents.MonitoringStopped, id, watched.Server.DisplayName,
                              source, Detail: detail));
    }

    // From a collector, on any thread and under its lock: only queued, so nothing else runs under
    // the lock. From the host's thread (a Dispose), the caller drains; from the thread pool, one
    // post drains whatever has queued by then.
    private void OnUpdated(Guid id, CollectorUpdate update)
    {
        _updates.Enqueue((id, update));
        if (!_serial.IsCurrent && Interlocked.Exchange(ref _drainPosted, 1) == 0)
            _serial.Post(_ =>
            {
                // Reset first: an update queued while this drains posts again.
                Volatile.Write(ref _drainPosted, 0);
                Drain();
            }, null);
    }

    // On the host's thread, first in first out.
    private void Drain()
    {
        while (_updates.TryDequeue(out var item))
            Handle(item.Id, item.Update);
    }

    private void Handle(Guid id, CollectorUpdate update)
    {
        if (update.Kind == CollectorUpdateKind.Alerts && update.Alerts is { Count: > 0 } changes)
        {
            // Raised for a server that just stopped, too: the channels hear its alerts end.
            _chat?.OnAlertsRaised(id, changes);
            return;
        }

        // A server that starts or stops failing is said once, not at every retry.
        if (!_watched.TryGetValue(id, out var watched) || watched.Failures == update.State.ConsecutiveFailures)
            return;
        if (watched.Failures == 0 && update.State.ConsecutiveFailures > 0)
            _log.Warning($"Collector: {watched.Server.DisplayName} can't be read: {update.State.LastError}");
        else if (update.State.ConsecutiveFailures == 0)
            _log.Info($"Collector: {watched.Server.DisplayName} answers again, after {watched.Failures} failed sample(s)");
        watched.Failures = update.State.ConsecutiveFailures;
    }

    // Through the history's queue; never blocks. The service's account and this PC say who did it.
    private void Log(ActivityEntry entry) =>
        _history!.Enqueue(new ActivityRecord(entry with
        {
            WindowsUser = string.IsNullOrEmpty(Environment.UserDomainName)
                ? Environment.UserName
                : $@"{Environment.UserDomainName}\{Environment.UserName}",
            Machine = Environment.MachineName,
        }));

    public void Dispose() => _serial.Dispose();
}
