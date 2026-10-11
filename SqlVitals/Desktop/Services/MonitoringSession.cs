using System.Collections.Concurrent;
using System.Windows.Threading;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// One connection's monitoring, as the window sees it: an Engine <see cref="Collector"/>, which
/// collects on the thread pool (#171), and what it said, applied on the UI thread in the order
/// it said it. Pages and <see cref="MonitoringManager"/> read it there without locking, and its
/// events are raised there. It also keeps the rolling window of samples Live Metrics plots, so
/// switching connections shows a continuous chart instead of an empty one.
///
/// <see cref="Pause"/> and <see cref="Dispose"/> are called on the UI thread, and the alert ends
/// they cause are raised before they return, after anything the collector said before them.
/// </summary>
public sealed class MonitoringSession : IDisposable
{
    /// <summary>Rolling window kept per connection (and plotted by Live Metrics).</summary>
    public const int MaxPoints = Collector.MaxPoints;

    public const int DefaultIntervalSeconds = Collector.DefaultIntervalSeconds;

    private readonly Collector              _collector;
    private readonly Dispatcher             _dispatcher;
    private readonly ConcurrentQueue<CollectorUpdate> _pending = new();
    private readonly List<LiveMetricSample> _samples = new();
    private CollectorState                  _state;
    private int                             _drainPosted;
    private bool                            _disposed;

    /// <param name="history">
    /// Saves this session's samples and alerts to the local history; null (the ad-hoc Live
    /// Metrics session) keeps nothing beyond the in-memory window and raises no alerts.
    /// </param>
    /// <param name="time">The collector's clock; the system clock when null (tests pass a fake one).</param>
    public MonitoringSession(Guid? connectionId, IWaitStatsRepository repository, string fingerprint,
                             int intervalSeconds = DefaultIntervalSeconds, HistoryRecorder? history = null,
                             TimeProvider? time = null)
    {
        ConnectionId = connectionId;
        Repository   = repository;
        Fingerprint  = fingerprint;
        _dispatcher  = Dispatcher.CurrentDispatcher;
        // A session that keeps history reads the slow checks too (#174); the ad-hoc one doesn't.
        _collector   = new Collector(connectionId, repository, intervalSeconds, history, time, ConnectionSettingsService.RedactSecrets,
                                     checks: history is null ? null : repository);
        _state       = _collector.State;
        _collector.Updated += OnUpdated;
    }

    /// <summary>The saved connection this session monitors; null for an ad-hoc session.</summary>
    public Guid? ConnectionId { get; }

    /// <summary>The connection's repositories; the collector reads live metrics through them, pages the rest.</summary>
    public IWaitStatsRepository Repository { get; }

    /// <summary>Saves this session's samples to the local history; null for an ad-hoc session.</summary>
    public HistoryRecorder? History => _collector.History;

    /// <summary>What the repository was built from; a change means the session must be recreated.</summary>
    public string Fingerprint { get; }

    public IReadOnlyList<LiveMetricSample> Samples => _samples;

    public bool IsRunning => _state.IsRunning;

    public HealthLevel Health => _state.Health;

    /// <summary>One-line, credential-free description of the latest result, for tooltips.</summary>
    public string HealthText => _state.HealthText;

    /// <summary>
    /// The latest result as a 0–100 score, with the checks that lowered it (#36); null until the
    /// first collection finishes.
    /// </summary>
    public HealthScore? Score => _state.Score;

    /// <summary>The score of the last collection that worked, kept while later ones fail (#130); null before one has.</summary>
    public HealthScore? LastGoodScore => _state.LastGoodScore;

    /// <summary>When the last collection that worked finished, on this PC's clock; null before one has.</summary>
    public DateTime? LastSuccess => _state.LastSuccess;

    /// <summary>When collections started failing, on this PC's clock; null while the last one worked.</summary>
    public DateTime? FailingSince => _state.FailingSince;

    /// <summary>Collections in a row that have failed; zero while the last one worked.</summary>
    public int ConsecutiveFailures => _state.ConsecutiveFailures;

    /// <summary>Why the last collection failed, credentials masked; null while the last one worked.</summary>
    public string? LastError => _state.LastError;

    public int IntervalSeconds
    {
        get => _state.IntervalSeconds;
        set { _collector.IntervalSeconds = value; DrainHere(); }
    }

    /// <summary>
    /// What the health dot is graded on. A change re-grades the latest sample straight away,
    /// rather than at the next collection.
    /// </summary>
    public HealthThresholds Thresholds
    {
        get => _collector.Thresholds;
        set { _collector.Thresholds = value; DrainHere(); }
    }

    /// <summary>Time between reads of the slow checks (#174): the history's detail interval.</summary>
    public TimeSpan ChecksInterval
    {
        get => _collector.ChecksInterval;
        set => _collector.ChecksInterval = value;
    }

    /// <summary>The RPO the backups check counts databases against (#174), from its next read.</summary>
    public BackupRpo BackupRpo
    {
        get => _collector.BackupRpo;
        set => _collector.BackupRpo = value;
    }

    /// <summary>
    /// Samples in a row an indicator must stay past a threshold for an alert to start, and back
    /// to normal for it to end. Applies from the next sample.
    /// </summary>
    public int AlertSamples
    {
        get => _collector.AlertSamples;
        set => _collector.AlertSamples = value;
    }

    /// <summary>This connection's alerts that have started and not ended; none for an ad-hoc session.</summary>
    public IReadOnlyList<Alert> ActiveAlerts => _state.ActiveAlerts;

    /// <summary>Time left before the next scheduled collection; zero when paused.</summary>
    public TimeSpan TimeUntilNextSample => _collector.TimeUntilNextSample;

    /// <summary>Raised on the UI thread after each successful collection.</summary>
    public event Action<MonitoringSession, LiveMetricSample>? SampleAdded;

    /// <summary>Raised on the UI thread when health, running state or interval changes.</summary>
    public event Action<MonitoringSession>? StateChanged;

    /// <summary>Raised on the UI thread when alerts start, escalate, worsen or end.</summary>
    public event Action<MonitoringSession, IReadOnlyList<AlertChange>>? AlertsChanged;

    /// <summary>Starts (or resumes) scheduled collection, on the thread pool. The first sample is taken right away.</summary>
    public void Start()
    {
        _collector.Start();
        DrainHere();
    }

    /// <summary>
    /// Stops scheduled collection. The history is kept; active alerts end, since nothing is
    /// watching any more, and are raised before this returns.
    /// </summary>
    public void Pause()
    {
        _collector.Pause();
        DrainHere();
    }

    /// <summary>
    /// Collects a sample now and resets the schedule. Joins a collection already in flight rather
    /// than starting a second one. Throws when the collection fails, so callers can report it.
    /// What it found is applied before the returned task completes.
    /// </summary>
    public async Task CollectNowAsync()
    {
        try
        {
            await _collector.CollectNowAsync();
        }
        finally
        {
            // Back on the caller's thread (the UI's), so what the collection said is applied
            // before the caller reads it.
            DrainHere();
        }
    }

    // From the collector, on any thread and under its lock: only queued, so no window code runs
    // under the lock. From the UI thread, the call that caused it applies it when the collector
    // returns (DrainHere); from the thread pool, one post applies whatever has queued by then.
    private void OnUpdated(CollectorUpdate update)
    {
        _pending.Enqueue(update);
        if (!_dispatcher.CheckAccess() && Interlocked.Exchange(ref _drainPosted, 1) == 0)
            _dispatcher.BeginInvoke(() =>
            {
                // Reset first: an update queued while this drains posts again.
                Volatile.Write(ref _drainPosted, 0);
                Drain();
            });
    }

    // On the UI thread: applies what's queued, after the collector call that caused it returned.
    private void DrainHere()
    {
        if (_dispatcher.CheckAccess())
            Drain();
    }

    // On the UI thread, first in first out. A handler that causes another update (a Pause, a
    // re-grade) drains again from inside, so its updates, such as the alert ends a Dispose raises,
    // are applied before the call returns, in their place in the queue.
    private void Drain()
    {
        while (_pending.TryDequeue(out var update))
            Apply(update);
    }

    private void Apply(CollectorUpdate update)
    {
        _state = update.State;
        switch (update.Kind)
        {
            // Once disposed, only alerts: the ends Dispose raises, and what came before them.
            case CollectorUpdateKind.Sample when update.Sample is { } sample && !_disposed:
                _samples.Add(sample);
                if (_samples.Count > MaxPoints)
                    _samples.RemoveRange(0, _samples.Count - MaxPoints);
                SampleAdded?.Invoke(this, sample);
                break;
            case CollectorUpdateKind.Alerts when update.Alerts is { Count: > 0 } changes:
                AlertsChanged?.Invoke(this, changes);
                break;
            case CollectorUpdateKind.State when !_disposed:
                StateChanged?.Invoke(this);
                break;
        }
    }

    /// <summary>
    /// Stops collecting for good. Active alerts end: they're queued to the history and raised
    /// (after anything still waiting to be applied) before this returns.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _collector.Dispose();
        Drain();
        // Dispose raises nothing when no alert was active; the state must still say it stopped.
        _state = _collector.State;
        _collector.Updated -= OnUpdated;
        SampleAdded   = null;
        StateChanged  = null;
        AlertsChanged = null;
    }
}
