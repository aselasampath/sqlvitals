using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Keeps one <see cref="MonitoringSession"/> running per monitored connection: the active one,
/// plus every saved connection with <see cref="ConnectionSettings.MonitorInBackground"/> set.
/// Every session's samples are also saved to the local history (see <see cref="HistoryWriter"/>).
/// </summary>
public sealed class MonitoringManager : IDisposable
{
    private readonly Dictionary<Guid, MonitoringSession> _sessions = new();

    // Shared by every session: one background writer, one SQLite file.
    private readonly HistoryWriter _history = new(HistoryWriter.DefaultPath, AppLog.Error);

    // From Settings; applied to every recorder, including ones created later.
    private TimeSpan _detailInterval = TimeSpan.FromMinutes(HistorySettings.DefaultIntervalMinutes);

    // Interval and paused state chosen on the Live Metrics page, kept when a session is
    // recreated because its connection was edited.
    private readonly Dictionary<Guid, int>  _intervals = new();
    private readonly HashSet<Guid>          _paused    = new();

    // Entra MFA connections the user has made active this session. Background collection for
    // any other Entra connection would pop up a Microsoft sign-in window out of nowhere, so
    // those wait until the user has signed in to them once (SqlClient then caches the token).
    private readonly HashSet<Guid> _signedIn = new();

    private readonly Dictionary<Guid, string> _notMonitoredReasons = new();

    public MonitoringManager()
    {
        // Queued before any session exists, so it can only end what a previous run left active.
        _history.EndAlertsLeftOpen();
    }

    // From Settings; applied to every session, including ones created later.
    private int _alertSamples = AlertSettings.DefaultSamples;

    /// <summary>Raised on the UI thread when a connection's session changes health or state.</summary>
    public event Action<Guid>? SessionStateChanged;

    /// <summary>
    /// Raised on the UI thread when a connection's alerts start, escalate, worsen or end,
    /// including when they end because its monitoring stopped.
    /// </summary>
    public event Action<Guid, IReadOnlyList<AlertChange>>? AlertsChanged;

    /// <summary>Every monitored connection's alerts that have started and not ended.</summary>
    public IReadOnlyList<Alert> ActiveAlerts =>
        _sessions.Values.SelectMany(s => s.ActiveAlerts).ToList();

    /// <summary>
    /// Reads the local history file: past ranges, alerts. Any thread may use it; each read opens
    /// its own short-lived connection.
    /// </summary>
    public HistoryReader HistoryReader => _reader ??= new HistoryReader(_history.Path);

    /// <summary>True when the history file is from a newer SqlVitals, so nothing is being saved.</summary>
    public bool IsHistoryDisabled => _history.IsDisabled;

    public MonitoringSession? Get(Guid? connectionId) =>
        connectionId is { } id && _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>Why a saved connection has no session, for the selector tooltip.</summary>
    public string NotMonitoredReason(Guid connectionId) =>
        _notMonitoredReasons.TryGetValue(connectionId, out var reason) ? reason : "Not monitored in the background.";

    /// <summary>The local history file every session writes to.</summary>
    public string HistoryPath => _history.Path;

    /// <summary>
    /// The history of a saved connection, for the trend pages; null for none. It is read from
    /// the file, so it includes what was recorded before the app was last restarted.
    /// </summary>
    public ConnectionHistory? HistoryFor(Guid? connectionId) =>
        connectionId is { } id ? new ConnectionHistory(HistoryReader, id) : null;

    private HistoryReader? _reader;

    /// <summary>Deletes all monitoring history; fails with the reason when it couldn't.</summary>
    public Task ClearHistoryAsync() => _history.ClearAsync();

    /// <summary>
    /// Applies the history snapshot interval and retention to the writer and every running
    /// session. A shorter retention deletes the older history straight away.
    /// </summary>
    public void ApplyHistorySettings(ConnectionStore store)
    {
        _detailInterval    = TimeSpan.FromMinutes(store.HistoryIntervalMinutes);
        _history.Retention = TimeSpan.FromDays(store.HistoryRetentionDays);
        foreach (var session in _sessions.Values)
            if (session.History is { } recorder)
                recorder.DetailInterval = _detailInterval;
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

    /// <summary>Starts, stops or recreates sessions to match the saved connections.</summary>
    public void Sync(ConnectionStore store)
    {
        ApplyHistorySettings(store);
        ApplyAlertSettings(store);

        if (store.Active is { } active)
            _signedIn.Add(active.Id);

        _notMonitoredReasons.Clear();
        var wanted = new Dictionary<Guid, ConnectionSettings>();
        foreach (var conn in store.Connections)
        {
            var reason = conn switch
            {
                _ when conn.Id != store.ActiveConnectionId && !conn.MonitorInBackground
                    => "Background monitoring is off for this connection (Settings → Monitor in background).",
                { NeedsPassword: true }
                    => "Not monitored: the password isn't saved. Connect to it once to start monitoring.",
                { Authentication: SqlAuthMode.EntraMfa } when !_signedIn.Contains(conn.Id)
                    => "Not monitored yet: switch to it once to complete the Microsoft sign-in.",
                _ => null,
            };

            if (reason is null)
                wanted[conn.Id] = conn;
            else
                _notMonitoredReasons[conn.Id] = reason;
        }

        foreach (var (id, session) in _sessions.ToList())
        {
            if (!wanted.TryGetValue(id, out var conn) ||
                RepositoryFactory.Fingerprint(conn, store.CommandTimeoutSeconds) != session.Fingerprint)
            {
                Remove(id);
            }
        }

        foreach (var (id, conn) in wanted)
        {
            if (_sessions.ContainsKey(id))
                continue;

            var repo     = RepositoryFactory.Create(conn, store.CommandTimeoutSeconds, out _);
            var recorder = new HistoryRecorder(
                new HistoryConnection(id, conn.DisplayName, conn.Server.Trim(), conn.DatabaseLabel),
                repo, _history, AppLog.Error)
            {
                DetailInterval = _detailInterval,
            };
            var session = new MonitoringSession(
                id, repo, RepositoryFactory.Fingerprint(conn, store.CommandTimeoutSeconds),
                _intervals.TryGetValue(id, out var seconds) ? seconds : MonitoringSession.DefaultIntervalSeconds,
                recorder)
            {
                Thresholds   = store.ThresholdsFor(conn),
                AlertSamples = _alertSamples,
            };

            session.StateChanged  += OnSessionStateChanged;
            session.AlertsChanged += OnSessionAlertsChanged;
            _sessions[id] = session;

            if (!_paused.Contains(id))
                session.Start();
        }

        // Thresholds aren't part of the fingerprint, so an edit reaches the running sessions here.
        ApplyHealthThresholds(store);

        // Forget preferences for connections that were deleted.
        var existing = store.Connections.Select(c => c.Id).ToHashSet();
        _intervals.Keys.Where(id => !existing.Contains(id)).ToList().ForEach(id => _intervals.Remove(id));
        _paused.RemoveWhere(id => !existing.Contains(id));
        _signedIn.RemoveWhere(id => !existing.Contains(id));
    }

    private void OnSessionStateChanged(MonitoringSession session)
    {
        if (session.ConnectionId is not { } id)
            return;

        _intervals[id] = session.IntervalSeconds;
        if (session.IsRunning)
            _paused.Remove(id);
        else
            _paused.Add(id);

        SessionStateChanged?.Invoke(id);
    }

    private void OnSessionAlertsChanged(MonitoringSession session, IReadOnlyList<AlertChange> changes)
    {
        if (session.ConnectionId is { } id)
            AlertsChanged?.Invoke(id, changes);
    }

    private void Remove(Guid id)
    {
        if (!_sessions.Remove(id, out var session))
            return;

        // Still subscribed to its alerts while it's disposed: the ones it ends are reported.
        session.StateChanged -= OnSessionStateChanged;
        session.Dispose();
        session.AlertsChanged -= OnSessionAlertsChanged;
        SessionStateChanged?.Invoke(id);
    }

    public void Dispose()
    {
        SessionStateChanged = null;   // the window is closing; nothing left to update
        AlertsChanged       = null;
        foreach (var id in _sessions.Keys.ToList())
            Remove(id);

        // After the sessions, so their last samples are queued; waits a few seconds at most.
        _history.Dispose();
    }
}
