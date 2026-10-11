using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Keeps one <see cref="MonitoringSession"/> running per monitored connection, as
/// <see cref="MonitoringPlan"/> decides (#115): with Start monitoring automatically on, the active
/// connection and every one with <see cref="ConnectionSettings.MonitorInBackground"/>; with it off,
/// none. Start and Stop, from Live Metrics, the connection selector or the tray menu, override that
/// per connection until SqlVitals exits. Every session's samples are saved to the local history
/// (see <see cref="HistoryWriter"/>), and every start and stop to its activity log.
/// </summary>
public sealed class MonitoringManager : IDisposable
{
    private readonly Dictionary<Guid, MonitoringSession> _sessions = new();

    // Shared by every session: one background writer, one SQLite file.
    private readonly HistoryWriter _history;

    // Builds a session's repository; the real one unless a test hands in a fake.
    private readonly Func<ConnectionSettings, int, IWaitStatsRepository> _createRepository;

    // From Settings; applied to every recorder, including ones created later.
    private TimeSpan _detailInterval = TimeSpan.FromMinutes(HistorySettings.DefaultIntervalMinutes);

    // Interval chosen on the Live Metrics page, kept when a session is stopped, or recreated
    // because its connection was edited.
    private readonly Dictionary<Guid, int> _intervals = new();

    // What the user started and stopped this run.
    private readonly MonitoringOverrides _overrides = new();

    // Entra MFA connections the user has made active this session. Background collection for
    // any other Entra connection would pop up a Microsoft sign-in window out of nowhere, so
    // those wait until the user has signed in to them once (SqlClient then caches the token).
    private readonly HashSet<Guid> _signedIn = new();

    private IReadOnlyDictionary<Guid, MonitoringDecision> _decisions = new Dictionary<Guid, MonitoringDecision>();

    // The store the last Sync applied: Start and Stop decide again from it.
    private ConnectionStore? _store;

    // When each connection stopped being watched this run, for the gap in "watching again" (#170).
    private readonly Dictionary<Guid, DateTime> _stoppedUtc = new();

    // Sessions' failures, and handlers that throw.
    private readonly ILogger _log;

    /// <param name="history">The local history every session saves to; disposed with this.</param>
    /// <param name="createRepository">A connection's repositories, with Settings' command timeout: <see cref="IRepositoryFactory"/>, or a fake.</param>
    /// <param name="log">The app log; <see cref="AppLog"/>'s when null (tests).</param>
    internal MonitoringManager(HistoryWriter history, Func<ConnectionSettings, int, IWaitStatsRepository> createRepository,
                               ILogger<MonitoringManager>? log = null)
    {
        _history          = history;
        _createRepository = createRepository;
        _log              = log ?? AppLog.For<MonitoringManager>();

        // Queued before any session exists, so it can only end what a previous run left active.
        _history.EndAlertsLeftOpen();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.AppStarted, null, null, ActivitySources.AutomaticAtStartup,
                              Detail: $"SqlVitals {version?.Major}.{version?.Minor}.{version?.Build}"));
    }

    // From Settings; applied to every session, including ones created later.
    private int _alertSamples = AlertSettings.DefaultSamples;

    /// <summary>
    /// Raised on the UI thread when a connection's session changes health or state, or it starts
    /// or stops being monitored.
    /// </summary>
    public event Action<Guid>? SessionStateChanged;

    /// <summary>
    /// Raised on the UI thread after monitoring started or stopped for any connection, so
    /// everything that says what is monitored (tray, Alerts, Live Metrics) can say it again.
    /// </summary>
    public event Action? MonitoringChanged;

    /// <summary>
    /// Raised on the UI thread when a connection's alerts start, escalate, worsen or end,
    /// including when they end because its monitoring stopped.
    /// </summary>
    public event Action<Guid, IReadOnlyList<AlertChange>>? AlertsChanged;

    /// <summary>
    /// Raised with <see cref="AlertsChanged"/>, for services that outlive the window, such as the
    /// chat channels (#121). Unlike <see cref="AlertsChanged"/>, it's still raised during
    /// <see cref="Dispose"/>, for the ends of the alerts that were active at Exit (#123).
    /// </summary>
    public event Action<Guid, IReadOnlyList<AlertChange>>? AlertsRaised;

    /// <summary>
    /// Raised on the UI thread after a <see cref="Sync"/> started or stopped watching connections,
    /// with why each stopped and how long each started one wasn't watched (#170). Not raised at
    /// Exit: <see cref="AllStopped"/> says that everything stopped.
    /// </summary>
    public event Action<WatchChange>? WatchingChanged;

    /// <summary>
    /// Raised once during <see cref="Dispose"/> (Exit), after every session has stopped and
    /// <see cref="AlertsRaised"/> has had their ends, and before the history is flushed, so a
    /// service can start sending its last messages while that runs (#123).
    /// </summary>
    public event Action? AllStopped;

    /// <summary>Every monitored connection's alerts that have started and not ended.</summary>
    public IReadOnlyList<Alert> ActiveAlerts =>
        _sessions.Values.SelectMany(s => s.ActiveAlerts).ToList();

    /// <summary>
    /// Reads the local history file: past ranges, alerts, the activity log. Any thread may use it;
    /// each read opens its own short-lived connection.
    /// </summary>
    public IHistoryReader HistoryReader => _reader ??= _sources.Count == 0
        ? new HistoryReader(_history.Path)
        : new CombinedHistoryReader(new HistoryReader(_history.Path), _sources, OnSourceFailed);

    // The collectors' history files (#172), read with the app's own.
    private IReadOnlyList<HistorySource> _sources = [];

    // What a collector watches instead of the app (#172): the reason for each, what its history
    // says, and those it really is watching now (its service running, its samples recent).
    private IReadOnlyDictionary<Guid, string>         _elsewhere = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, ElsewhereWatch> _watches   = new Dictionary<Guid, ElsewhereWatch>();
    private IReadOnlySet<Guid>                        _watchedElsewhere = new HashSet<Guid>();

    // Each collector's file that can't be read is said once, until it can be again.
    private readonly HashSet<string> _sourcesFailing = new();

    /// <summary>
    /// What the collectors watch (#172, <see cref="CollectorLink"/>): the app stops collecting
    /// those connections, with <paramref name="reasons"/> as why, and reads their history from
    /// <paramref name="sources"/> as well as its own. Empty arguments put everything back as it was.
    /// Every such connection is redrawn (<see cref="SessionStateChanged"/>): its last score or
    /// alerts may be new.
    /// </summary>
    public void SetWatchedElsewhere(IReadOnlyDictionary<Guid, ElsewhereWatch> watches, IReadOnlyDictionary<Guid, string> reasons,
                                    IReadOnlyList<HistorySource> sources)
    {
        var redraw = _watches.Keys.Concat(watches.Keys).ToHashSet();
        _watches          = watches;
        _elsewhere        = reasons;
        _watchedElsewhere = watches.Where(w => w.Value.NotWatching is null).Select(w => w.Key).ToHashSet();
        if (!ReferenceEquals(sources, _sources))
        {
            _sources = sources;
            _reader  = null;
            _sourcesFailing.Clear();
        }
        Resync(ActivitySources.CollectorSettings);
        foreach (var id in redraw)
            SessionStateChanged?.Invoke(id);
    }

    /// <summary>What a collector's history says of a connection it watches (#172); null when none does.</summary>
    public ElsewhereWatch? WatchFor(Guid connectionId) => _watches.TryGetValue(connectionId, out var watch) ? watch : null;

    /// <summary>Why a collector watches a connection instead of the app; null when none does.</summary>
    public string? WatchedElsewhereReason(Guid connectionId) => _elsewhere.TryGetValue(connectionId, out var why) ? why : null;

    /// <summary>Whether a collector is watching a connection right now, so the app needn't.</summary>
    public bool IsWatchedElsewhere(Guid connectionId) => _watchedElsewhere.Contains(connectionId);

    private void OnSourceFailed(HistorySource source, Exception ex)
    {
        lock (_sourcesFailing)
            if (!_sourcesFailing.Add(source.Name))
                return;
        _log.Error($"History: couldn't read the history of {source.Name}; its servers show only what this PC recorded", ex);
    }

    public MonitoringSession? Get(Guid? connectionId) =>
        connectionId is { } id && _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>Whether a saved connection is being monitored right now.</summary>
    public bool IsMonitoring(Guid? connectionId) => Get(connectionId) is { IsRunning: true };

    /// <summary>How many connections are being monitored right now, by the app or a collector (#172).</summary>
    public int MonitoredCount => _sessions.Values.Count(s => s.IsRunning) + _watchedElsewhere.Count;

    /// <summary>The setting the last Sync applied: Start monitoring automatically.</summary>
    public bool StartsAutomatically { get; private set; }

    /// <summary>Whether a connection is monitored and why, as the last Sync decided; null for an unknown one.</summary>
    public MonitoringDecision? Decision(Guid connectionId) =>
        _decisions.TryGetValue(connectionId, out var decision) ? decision : null;

    /// <summary>Whether Start can be pressed for a connection: false while its password or sign-in is missing.</summary>
    public bool CanStart(Guid connectionId) =>
        !_decisions.TryGetValue(connectionId, out var decision) || decision.CanStart;

    /// <summary>
    /// A saved connection's monitoring state in words, for the selector tooltip and Live Metrics:
    /// "Monitoring.", "Stopped by you at 10:42…", "Not monitored: the password isn't saved…".
    /// </summary>
    public string NotMonitoredReason(Guid connectionId) =>
        _decisions.TryGetValue(connectionId, out var decision) ? decision.Text : MonitoringPlan.AutomaticOffText;

    /// <summary>
    /// Saved connections that raise no alerts because they aren't monitored, by name, for the
    /// Alerts banner. A connection left out on purpose (Monitor in background off while another
    /// is active) isn't listed.
    /// </summary>
    public IReadOnlyList<string> NotMonitoredNames() =>
        (_store?.Connections ?? [])
            .Where(c => _decisions.TryGetValue(c.Id, out var d) && !d.Monitor && d.Reason != MonitoringReason.BackgroundOff &&
                        !_watchedElsewhere.Contains(c.Id))
            .Select(c => c.DisplayName)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The local history file every session writes to.</summary>
    public string HistoryPath => _history.Path;

    /// <summary>
    /// The history of a saved connection, for the trend pages; null for none. It is read from
    /// the file, so it includes what was recorded before the app was last restarted.
    /// </summary>
    public ConnectionHistory? HistoryFor(Guid? connectionId) =>
        connectionId is { } id ? new ConnectionHistory(HistoryReader, id) : null;

    private IHistoryReader? _reader;

    /// <summary>
    /// Deletes all monitoring history, and logs that it did; fails with the reason when it
    /// couldn't. The activity log is kept.
    /// </summary>
    public Task ClearHistoryAsync(string source) =>
        _history.ClearAsync(Record(new ActivityEntry(DateTime.UtcNow, ActivityEvents.HistoryCleared, null, null, source)));

    /// <summary>
    /// Adds a row to the activity log, with this Windows account and PC. Never blocks: it goes
    /// through the history writer's queue, and to the app log when it can't be saved.
    /// </summary>
    public void Log(ActivityEntry entry) => _history.Enqueue(Record(entry));

    private static ActivityRecord Record(ActivityEntry entry) =>
        new(entry with
        {
            WindowsUser = string.IsNullOrEmpty(Environment.UserDomainName)
                ? Environment.UserName
                : $@"{Environment.UserDomainName}\{Environment.UserName}",
            Machine = Environment.MachineName,
        });

    /// <summary>The log line for an activity row the history couldn't save.</summary>
    internal static string NotSaved(ActivityEntry e) =>
        $"Activity not saved to the history: {e.Event} · {e.ConnectionName ?? "all connections"} · {e.Source}" +
        (e.Setting is null ? "" : $" · {e.Setting}: {e.OldValue ?? "–"} → {e.NewValue ?? e.Detail ?? "–"}") +
        (e.Detail is null || e.Setting is not null ? "" : $" · {e.Detail}");

    /// <summary>
    /// Applies the history snapshot interval and retention to the writer and every running
    /// session. A shorter retention deletes the older history straight away.
    /// </summary>
    public void ApplyHistorySettings(ConnectionStore store)
    {
        _detailInterval    = TimeSpan.FromMinutes(store.HistoryIntervalMinutes);
        _history.Retention = TimeSpan.FromDays(store.HistoryRetentionDays);
        foreach (var session in _sessions.Values)
        {
            if (session.History is { } recorder)
                recorder.DetailInterval = _detailInterval;
            session.ChecksInterval = _detailInterval;
        }
    }

    /// <summary>
    /// Gives every running session the RPO set on the Backups page, which the backups check counts
    /// databases against (#174), from its next read.
    /// </summary>
    public void ApplyBackupRpo(ConnectionStore store)
    {
        foreach (var session in _sessions.Values)
            session.BackupRpo = store.BackupRpo;
    }

    /// <summary>
    /// Gives every running session its connection's health thresholds (its own, or the ones in
    /// Settings). The dots update straight away.
    /// </summary>
    public void ApplyHealthThresholds(ConnectionStore store)
    {
        foreach (var conn in store.Connections)
            if (_sessions.TryGetValue(conn.Id, out var session))
                session.Thresholds = store.ThresholdsFor(conn);
    }

    /// <summary>
    /// Applies how many samples in a row start and end an alert to every running session, from
    /// its next sample.
    /// </summary>
    public void ApplyAlertSettings(ConnectionStore store)
    {
        _alertSamples = store.AlertSamples;
        foreach (var session in _sessions.Values)
            session.AlertSamples = _alertSamples;
    }

    /// <summary>The Live Metrics interval of a connection, while it is monitored or not.</summary>
    public int IntervalFor(Guid? connectionId) =>
        Get(connectionId)?.IntervalSeconds
        ?? (connectionId is { } id && _intervals.TryGetValue(id, out var seconds) ? seconds : MonitoringSession.DefaultIntervalSeconds);

    /// <summary>Sets a connection's Live Metrics interval; kept for when it is next monitored.</summary>
    public void SetInterval(Guid connectionId, int seconds)
    {
        _intervals[connectionId] = Math.Max(1, seconds);
        if (Get(connectionId) is { } session)
            session.IntervalSeconds = seconds;
    }

    /// <summary>Starts monitoring a connection until SqlVitals exits. The first sample is taken straight away.</summary>
    /// <param name="source">Where the user pressed Start, for the activity log.</param>
    public void Start(Guid connectionId, string source)
    {
        _overrides.Start(connectionId);
        Resync(source);
    }

    /// <summary>Stops monitoring a connection until SqlVitals exits. Its active alerts end.</summary>
    public void Stop(Guid connectionId, string source)
    {
        _overrides.Stop(connectionId, DateTime.Now);
        Resync(source);
    }

    /// <summary>Starts the active connection and every one with Monitor in background (the tray menu).</summary>
    public void StartAll(string source)
    {
        foreach (var conn in _store?.Connections ?? [])
            if (conn.Id == _store!.ActiveConnectionId || conn.MonitorInBackground)
                _overrides.Start(conn.Id);
        Resync(source);
    }

    /// <summary>Stops every connection being monitored (the tray menu).</summary>
    public void StopAll(string source)
    {
        var now = DateTime.Now;
        foreach (var id in _sessions.Keys.ToList())
            _overrides.Stop(id, now);
        Resync(source);
    }

    private void Resync(string source)
    {
        if (_store is not null)
            Sync(_store, source);
    }

    /// <summary>
    /// Starts, stops or recreates sessions to match the saved connections, Start monitoring
    /// automatically, and what the user started and stopped this run.
    /// </summary>
    /// <param name="source">Why, for the activity log rows of any connection that starts or stops.</param>
    public void Sync(ConnectionStore store, string source)
    {
        ApplyHistorySettings(store);
        ApplyAlertSettings(store);
        ApplyBackupRpo(store);

        // Turning the setting on or off applies straight away to every connection, including
        // any the user started or stopped by hand.
        if (_store is not null && store.StartMonitoringAutomatically != StartsAutomatically)
        {
            _overrides.Clear();
            source = ActivitySources.SettingChanged;
        }
        StartsAutomatically = store.StartMonitoringAutomatically;
        _store = store;

        if (store.Active is { } active)
            _signedIn.Add(active.Id);

        // Forget choices for connections that were deleted.
        var existing = store.Connections.Select(c => c.Id).ToHashSet();
        _intervals.Keys.Where(id => !existing.Contains(id)).ToList().ForEach(id => _intervals.Remove(id));
        _overrides.KeepOnly(existing);
        _signedIn.RemoveWhere(id => !existing.Contains(id));

        var previous = _decisions;
        _decisions = MonitoringPlan.Decide(
            store.StartMonitoringAutomatically,
            store.Connections.Select(c => new MonitoringCandidate(
                c.Id, c.Id == store.ActiveConnectionId, c.MonitorInBackground, c.NeedsPassword,
                c.Authentication == SqlAuthMode.EntraMfa && !_signedIn.Contains(c.Id),
                _elsewhere.TryGetValue(c.Id, out var elsewhere) ? elsewhere : null)),
            _overrides);

        var changed = false;
        var stopped   = new List<StoppedWatching>();
        var started   = new List<WatchedConnection>();
        var restarted = new List<Guid>();
        foreach (var (id, session) in _sessions.ToList())
        {
            var conn = store.Connections.FirstOrDefault(c => c.Id == id);
            if (conn is null)
            {
                var name = session.History?.Connection.Name ?? ChannelRouter.DeletedConnection;
                Remove(id, ActivitySources.ConnectionEdited, "The connection was removed.");
                stopped.Add(new StoppedWatching(id, name, WatchStop.ConnectionRemoved));
                changed = true;
            }
            else if (!_decisions[id].Monitor)
            {
                Remove(id, source, conn, _decisions[id].Reason switch
                {
                    MonitoringReason.StoppedByUser => "Stopped by the user; stays stopped until SqlVitals exits.",
                    MonitoringReason.AutomaticOff  => "Start monitoring automatically is off.",
                    MonitoringReason.BackgroundOff => "Another connection is active, and Monitor in background is off for this one.",
                    _                              => _decisions[id].Text,
                });
                stopped.Add(new StoppedWatching(id, conn.DisplayName, WatchChange.StopFor(_decisions[id].Reason)));
                changed = true;
            }
            else if (RepositoryFactory.Fingerprint(conn, store.CommandTimeoutSeconds) != session.Fingerprint)
            {
                // Recreated below with the new details.
                Remove(id, ActivitySources.ConnectionEdited, conn, "Restarted with the connection's new details.");
                changed = true;
            }
        }

        foreach (var conn in store.Connections)
        {
            if (!_decisions[conn.Id].Monitor || _sessions.ContainsKey(conn.Id))
                continue;

            var again = previous.TryGetValue(conn.Id, out var was) && was.Monitor;
            // Read before its session starts, so its first sample can't be taken for the last one.
            if (again)
                restarted.Add(conn.Id);
            else
                started.Add(new WatchedConnection(conn.Id, conn.DisplayName, LastWatched(conn.Id)));
            Add(conn, store, again ? ActivitySources.ConnectionEdited : source, _decisions[conn.Id]);
            changed = true;
        }

        var now = DateTime.UtcNow;
        foreach (var stop in stopped)
            _stoppedUtc[stop.Id] = now;
        var watching = new WatchChange(started, stopped, restarted);
        if (!watching.IsEmpty)
        {
            try { WatchingChanged?.Invoke(watching); }
            catch (Exception ex) { _log.Error("Monitoring: a background service failed when monitoring started or stopped", ex); }
        }

        // Thresholds aren't part of the fingerprint, so an edit reaches the running sessions here.
        ApplyHealthThresholds(store);

        // A connection whose reason changed (stopped by you, password now saved…) redraws too.
        foreach (var (id, decision) in _decisions)
            if (!previous.TryGetValue(id, out var before) || before != decision)
                SessionStateChanged?.Invoke(id);

        if (changed || !previous.Keys.ToHashSet().SetEquals(_decisions.Keys))
            MonitoringChanged?.Invoke();
    }

    // When a connection was last watched: when it stopped this run, else its last sample in the
    // history. Read only when someone listens (the chat channels); a history that can't be read
    // gives no gap rather than failing the start.
    private DateTime? LastWatched(Guid id)
    {
        if (WatchingChanged is null)
            return null;
        if (_stoppedUtc.TryGetValue(id, out var stopped))
            return stopped;
        try { return HistoryReader.ReadLastSampleTime(id); }
        catch (Exception ex)
        {
            _log.Error("Monitoring: couldn't read when a connection was last watched", ex);
            return null;
        }
    }

    private void Add(ConnectionSettings conn, ConnectionStore store, string source, MonitoringDecision decision)
    {
        var id       = conn.Id;
        var repo     = _createRepository(conn, store.CommandTimeoutSeconds);
        var recorder = new HistoryRecorder(
            new HistoryConnection(id, conn.DisplayName, conn.Server.Trim(), conn.DatabaseLabel),
            repo, _history, _log.Error)
        {
            DetailInterval = _detailInterval,
        };
        var session = new MonitoringSession(
            id, repo, RepositoryFactory.Fingerprint(conn, store.CommandTimeoutSeconds),
            _intervals.TryGetValue(id, out var seconds) ? seconds : MonitoringSession.DefaultIntervalSeconds,
            recorder)
        {
            Thresholds     = store.ThresholdsFor(conn),
            AlertSamples   = _alertSamples,
            ChecksInterval = _detailInterval,
            BackupRpo      = store.BackupRpo,
        };

        session.StateChanged  += OnSessionStateChanged;
        session.AlertsChanged += OnSessionAlertsChanged;
        _sessions[id] = session;
        session.Start();

        Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.MonitoringStarted, id, conn.DisplayName, source,
                              Detail: decision.Reason switch
                              {
                                  MonitoringReason.StartedByUser => "Started by the user; runs until SqlVitals exits.",
                                  _ when id == store.ActiveConnectionId => "The active connection, with Start monitoring automatically on.",
                                  _ => "Monitor in background is on, with Start monitoring automatically on.",
                              }));
    }

    private void OnSessionStateChanged(MonitoringSession session)
    {
        if (session.ConnectionId is not { } id)
            return;

        _intervals[id] = session.IntervalSeconds;
        SessionStateChanged?.Invoke(id);
    }

    private void OnSessionAlertsChanged(MonitoringSession session, IReadOnlyList<AlertChange> changes)
    {
        if (session.ConnectionId is not { } id)
            return;

        // Each on its own, services first. This runs inside the session's collection (and its
        // Dispose), so a subscriber that throws would otherwise show the connection as "Monitoring
        // failed", or stop the other subscribers hearing the alert.
        Raise(AlertsRaised, id, changes, "a background service");
        Raise(AlertsChanged, id, changes, "the window");
    }

    private void Raise(Action<Guid, IReadOnlyList<AlertChange>>? handlers, Guid id,
                              IReadOnlyList<AlertChange> changes, string who)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Guid, IReadOnlyList<AlertChange>>>())
        {
            try { handler(id, changes); }
            catch (Exception ex) { _log.Error($"Alerts: {who} failed to handle an alert change", ex); }
        }
    }

    private void Remove(Guid id, string source, ConnectionSettings conn, string? detail = null) =>
        Remove(id, source, detail, conn.DisplayName);

    private void Remove(Guid id, string source, string? detail, string? name = null)
    {
        if (!_sessions.Remove(id, out var session))
            return;

        // Still subscribed to its alerts while it's disposed: the ones it ends are reported.
        session.StateChanged -= OnSessionStateChanged;
        session.Dispose();
        session.AlertsChanged -= OnSessionAlertsChanged;

        Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.MonitoringStopped, id,
                              name ?? session.History?.Connection.Name, source, Detail: detail));
        SessionStateChanged?.Invoke(id);
    }

    public void Dispose()
    {
        SessionStateChanged = null;   // the window is closing; nothing left to update
        MonitoringChanged   = null;
        WatchingChanged     = null;   // Exit is AllStopped's
        AlertsChanged       = null;   // the window and the Alerts page stop here…
        foreach (var id in _sessions.Keys.ToList())
            Remove(id, ActivitySources.AppExit, "SqlVitals exited.");
        // …while the services still hear the ends of the alerts that were active (#123): the
        // chat channels' "stopped monitoring". Neither closing page subscribes to this one.
        AlertsRaised = null;
        // Before the history's wait below, so the chat channels send meanwhile, not after it.
        try { AllStopped?.Invoke(); }
        catch (Exception ex) { _log.Error("Exit: a background service failed when monitoring stopped", ex); }
        AllStopped = null;
        Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.AppExited, null, null, ActivitySources.AppExit));

        // After the sessions, so their last samples are queued; waits a few seconds at most.
        _history.Dispose();
    }
}
