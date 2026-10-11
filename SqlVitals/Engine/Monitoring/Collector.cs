using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// What a <see cref="Collector"/> knows at one moment (#171): health, score, failures, alerts and
/// schedule. Immutable, so another thread (the window's) can keep and read it.
/// </summary>
/// <param name="LastSuccess">When the last collection that worked finished, on this PC's clock; null before one has.</param>
/// <param name="FailingSince">When collections started failing, on this PC's clock; null while the last one worked.</param>
/// <param name="LastError">Why the last collection failed, secrets removed; null while the last one worked.</param>
public sealed record CollectorState(
    bool                 IsRunning,
    HealthLevel          Health,
    string               HealthText,
    HealthScore?         Score,
    HealthScore?         LastGoodScore,
    DateTime?            LastSuccess,
    DateTime?            FailingSince,
    int                  ConsecutiveFailures,
    string?              LastError,
    IReadOnlyList<Alert> ActiveAlerts,
    int                  IntervalSeconds,
    DateTime             NextDueUtc);

/// <summary>What changed: alerts, a new sample, or the state alone.</summary>
public enum CollectorUpdateKind
{
    Alerts,
    Sample,
    State,
}

/// <summary>
/// One change from a <see cref="Collector"/>, with its state just after it (#171). Raised in the
/// order they happen, so a listener that applies them in order sees what the collector saw.
/// </summary>
public sealed record CollectorUpdate(
    CollectorUpdateKind         Kind,
    CollectorState              State,
    LiveMetricSample?           Sample = null,
    IReadOnlyList<AlertChange>? Alerts = null);

/// <summary>
/// Collects live metrics for one connection on its own schedule, without a window (#171): one
/// loop on a <see cref="PeriodicTimer"/> that wakes every <see cref="Tick"/> and collects when a sample is
/// due, at the connection's interval, later after failures (<see cref="HealthRules.NextDelay"/>).
/// A collection never overlaps another: a tick while one runs is skipped, and
/// <see cref="CollectNowAsync"/> joins it. Each sample is graded, scored, checked for alerts and
/// queued to the history, all off the caller's thread. With a <see cref="IServerChecksRepository"/>,
/// the slow checks (#174: disk, backups, Agent jobs) are read every <see cref="ChecksInterval"/>
/// after a sample, in the background, and the last read is graded with each sample
/// (<see cref="ServerChecks"/>).
///
/// Thread-safe. <see cref="Updated"/> is raised under the collector's lock, in order, from
/// whichever thread made the change (the loop's, or the caller of <see cref="Pause"/> and
/// <see cref="Dispose"/>): a listener must not block, and may hand the update on to its own
/// thread. Desktop's <c>MonitoringSession</c> does, to the window's.
/// </summary>
public sealed class Collector : IDisposable
{
    /// <summary>The rolling window a listener keeps per connection (and Live Metrics plots).</summary>
    public const int MaxPoints = 60;

    public const int DefaultIntervalSeconds = 10;

    /// <summary>How often the loop wakes to see whether a sample is due.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    // A sample is due at a tick up to half a tick before its time. The next one is due an
    // interval after the last finished, which on a real clock lands just after a tick: without
    // this, every interval would wait a whole extra tick (10 s became 11 s before #171).
    private static readonly TimeSpan Early = Tick / 2;

    private readonly object               _gate = new();
    private readonly TimeProvider         _time;
    private readonly Func<string, string> _redact;
    private readonly HistoryRecorder?     _history;
    private readonly AlertTracker?        _alerts;
    private readonly IServerChecksRepository? _checksRepository;
    private readonly ServerChecks?        _checks;

    private LiveMetricSnapshot? _previous;
    private LiveMetricSample?   _lastSample;
    private Task?               _inFlight;
    private int                 _consecutiveFailures;
    private DateTime            _nextDueUtc;
    private int                 _intervalSeconds;
    private HealthThresholds    _thresholds = HealthThresholds.Default;
    private DateTime?           _lastSuccess;
    private DateTime?           _failingSince;
    private string?             _gradedText;
    private bool                _running;
    private bool                _disposed;
    private bool                _checksInFlight;
    private BackupRpo           _backupRpo = BackupRpo.Default;

    private HealthLevel  _health     = HealthLevel.Unknown;
    private string       _healthText = "Waiting for the first sample…";
    private HealthScore? _score;
    private HealthScore? _lastGoodScore;
    private string?      _lastError;

    // Cancelled by Pause and Dispose (#167): a sample in flight stops on the server too.
    private CancellationTokenSource  _collecting = new();
    // The loop's own, cancelled by Pause and Dispose.
    private CancellationTokenSource? _loop;

    /// <param name="history">
    /// Saves the samples, scores and alerts to the local history; null (Live Metrics' ad-hoc
    /// collector) keeps nothing and raises no alerts.
    /// </param>
    /// <param name="time">The clock for the schedule and the times it reports; the system clock when null.</param>
    /// <param name="redact">Removes secrets from an error before it's kept or shown; none when null.</param>
    /// <param name="checks">
    /// Reads the slow checks (#174); null (Live Metrics' ad-hoc collector) grades only the six read
    /// with every sample.
    /// </param>
    public Collector(Guid? connectionId, ILiveMetricsRepository repository, int intervalSeconds = DefaultIntervalSeconds,
                     HistoryRecorder? history = null, TimeProvider? time = null, Func<string, string>? redact = null,
                     IServerChecksRepository? checks = null)
    {
        ConnectionId     = connectionId;
        Repository       = repository;
        _history         = history;
        _time            = time ?? TimeProvider.System;
        _redact          = redact ?? (text => text);
        _intervalSeconds = Math.Max(1, intervalSeconds);

        if (connectionId is { } id && history is not null)
            _alerts = new AlertTracker(id);
        if (checks is not null)
        {
            _checksRepository = checks;
            _checks           = new ServerChecks { SampleInterval = TimeSpan.FromSeconds(_intervalSeconds) };
        }
    }

    /// <summary>The saved connection it collects for; null for an ad-hoc collector.</summary>
    public Guid? ConnectionId { get; }

    /// <summary>Where samples are read: the connection's live metrics, nothing else.</summary>
    public ILiveMetricsRepository Repository { get; }

    /// <summary>Saves the samples to the local history; null for an ad-hoc collector.</summary>
    public HistoryRecorder? History => _history;

    /// <summary>
    /// Every change, in order (see the class remarks). Raised under the lock: don't block, and
    /// don't wait for another thread that may be calling into this collector.
    /// </summary>
    public event Action<CollectorUpdate>? Updated;

    /// <summary>What it knows now.</summary>
    public CollectorState State
    {
        get { lock (_gate) return Snapshot(); }
    }

    public int IntervalSeconds
    {
        get { lock (_gate) return _intervalSeconds; }
        set
        {
            lock (_gate)
            {
                var seconds = Math.Max(1, value);
                if (seconds == _intervalSeconds) return;
                _intervalSeconds = seconds;
                _checks?.SampleInterval = TimeSpan.FromSeconds(seconds);
                _nextDueUtc = UtcNow + CurrentDelay();
                Raise(CollectorUpdateKind.State);
            }
        }
    }

    /// <summary>
    /// What the health is graded on. A change re-grades the latest sample straight away, rather
    /// than at the next collection.
    /// </summary>
    public HealthThresholds Thresholds
    {
        get { lock (_gate) return _thresholds; }
        set
        {
            lock (_gate)
            {
                if (value == _thresholds) return;
                _thresholds = value;
                if (_consecutiveFailures == 0 && _lastSample is { } sample)
                {
                    GradeHealth(Grade(sample), sample);
                    Raise(CollectorUpdateKind.State);
                }
            }
        }
    }

    /// <summary>
    /// Time between reads of the slow checks (#174): the history's detail interval. A change
    /// applies to the next check. Nothing to set without a <see cref="IServerChecksRepository"/>.
    /// </summary>
    public TimeSpan ChecksInterval
    {
        get { lock (_gate) return _checks?.Interval ?? ServerChecks.DefaultInterval; }
        set { lock (_gate) _checks?.Interval = value; }
    }

    /// <summary>A read of the slow checks is running (#174); tests wait for it to end.</summary>
    internal bool ReadingChecks
    {
        get { lock (_gate) return _checksInFlight; }
    }

    /// <summary>The RPO the backups check counts databases against (#174), from the next read.</summary>
    public BackupRpo BackupRpo
    {
        get { lock (_gate) return _backupRpo; }
        set { lock (_gate) _backupRpo = value; }
    }

    /// <summary>
    /// Samples in a row an indicator must stay past a threshold for an alert to start, and back
    /// to normal for it to end. Applies from the next sample.
    /// </summary>
    public int AlertSamples
    {
        get { lock (_gate) return _alerts?.Samples ?? AlertSettings.DefaultSamples; }
        set
        {
            lock (_gate)
            {
                _alerts?.Samples = Math.Max(1, value);
                _checks?.AlertSamples = Math.Max(1, value);
            }
        }
    }

    /// <summary>Time left before the next scheduled collection; zero when paused.</summary>
    public TimeSpan TimeUntilNextSample
    {
        get
        {
            lock (_gate)
            {
                if (!_running) return TimeSpan.Zero;
                var left = _nextDueUtc - UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Starts (or resumes) the loop, on the thread pool. The first sample is due straight away: taken at the first tick.</summary>
    public void Start()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _running) return;
            _running    = true;
            _nextDueUtc = UtcNow;
            _loop       = new CancellationTokenSource();
            token       = _loop.Token;
            Raise(CollectorUpdateKind.State);
        }
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>
    /// Stops the loop, and the sample being read. Active alerts end, since nothing is watching
    /// any more; their ends are raised before this returns.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            StopLoop();
            StopCollecting();
            StopAlerts();
            Raise(CollectorUpdateKind.State);
        }
    }

    /// <summary>
    /// Collects a sample now and resets the schedule. Joins a collection already in flight
    /// rather than starting a second one. Fails when the collection fails, so callers can report
    /// it; cancelled when <see cref="Pause"/> or <see cref="Dispose"/> stops it.
    /// </summary>
    public Task CollectNowAsync()
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            if (_inFlight is { } running)
                return running;
            if (_disposed)
                return Task.CompletedTask;
            // Claimed before it starts, so a collection finishing on another thread can never
            // leave a finished task here that every later collection would join.
            done      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = done.Task;
        }
        // On the thread pool, so even the repository's synchronous start (opening a connection)
        // never runs on the caller's thread: the window's, for Refresh.
        _ = Task.Run(() => RunCollectionAsync(done), CancellationToken.None);
        return done.Task;
    }

    private async Task RunCollectionAsync(TaskCompletionSource done)
    {
        try
        {
            await CollectAsync().ConfigureAwait(false);
            done.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            done.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    // The loop: wakes every Tick and collects when a sample is due and none is running. Ends
    // when Pause or Dispose cancels it.
    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            // The first check is a tick after Start, as the window's timer did before #171.
            using var timer = new PeriodicTimer(Tick, _time);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                bool due;
                lock (_gate)
                    due = _running && _inFlight is null && UtcNow + Early >= _nextDueUtc;
                if (!due)
                    continue;
                try
                {
                    await CollectNowAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A failure is in the state and retried with backoff; a sample stopped by
                    // Pause or Dispose (#167) records nothing.
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Paused or disposed.
        }
    }

    private async Task CollectAsync()
    {
        CancellationToken cancel;
        lock (_gate)
        {
            if (_disposed) { _inFlight = null; return; }
            cancel = _collecting.Token;
        }

        try
        {
            var snapshot = await Repository.GetLiveMetricsAsync(cancel).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return;

                var isFirst = _previous is null;
                var sample  = LiveMetricSample.From(_previous, snapshot);
                _previous   = snapshot;
                _lastSample = sample;

                _consecutiveFailures = 0;
                _failingSince        = null;
                _lastError           = null;
                _lastSuccess         = LocalNow;
                var readings = Grade(sample);
                GradeHealth(readings, sample);
                _history?.RecordScore(_score!);
                // Every collected sample counts towards an alert, the first one too: the health
                // gauges don't need a previous sample. A failed collection counts neither way, and
                // neither does one finishing after Pause, which has already ended the alerts.
                if (_running)
                    RecordAlerts(_alerts?.Evaluate(readings, UtcNow));

                // Neither call waits on the disk. The first sample's rates are all zero (nothing
                // to diff against), so it isn't history.
                if (_history is not null && !isFirst)
                    _history.RecordSample(sample);

                Raise(CollectorUpdateKind.Sample, sample);
            }
            // Details and the slow checks are only read from a server that just answered, never
            // from one that is failing.
            if (_history is not null)
                _ = _history.CollectDetailIfDueAsync(cancel);
            ReadChecksIfDue(cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Paused or disposed while reading: not a failure, and the health stays as it was.
            throw;
        }
        catch (WaitStatsException ex) when (ex.Failure.Kind == SqlFailureKind.LockTimeout && !Volatile.Read(ref _disposed))
        {
            // Monitoring waits SqlTarget.MonitoringLockTimeoutMilliseconds for a lock, then skips
            // the sample rather than queue behind a schema change (#167). Not a failure: the next
            // sample is due as usual, and the health stays as the last one graded.
            lock (_gate)
            {
                if (!_disposed)
                    _healthText = $"{_gradedText ?? "No sample yet"} · the {LocalNow:HH:mm:ss} sample was skipped: " +
                                  $"it waited {SqlTarget.MonitoringLockTimeoutMilliseconds / 1000} s for a lock another session holds";
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _consecutiveFailures++;
                    _failingSince ??= LocalNow;
                    _health     = HealthLevel.Unavailable;
                    var detail  = ex is WaitStatsException { InnerException: { } inner } ? inner.Message : ex.Message;
                    _lastError  = _redact(detail);
                    _healthText = $"Monitoring failed at {LocalNow:HH:mm:ss}: {_lastError} Retrying in {FormatDelay(CurrentDelay())}.";
                    _score      = HealthScore.Unavailable(_healthText, LocalNow);
                    _history?.RecordScore(_score);
                }
            }
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight   = null;
                _nextDueUtc = UtcNow + CurrentDelay();
                if (!_disposed)
                    Raise(CollectorUpdateKind.State);
            }
        }
    }

    // Under the lock: the sample's six checks, and the slow checks' last read (#174).
    private IReadOnlyList<HealthReading> Grade(LiveMetricSample sample)
    {
        var readings = HealthRules.Grade(sample, _thresholds);
        return _checks is null ? readings : [.. readings, .. _checks.Readings(_thresholds, UtcNow)];
    }

    // Reads the slow checks in the background when due and none is running (#174). A read that
    // fails keeps the last one, which turns Unknown once it's too old to trust; the next is due
    // an interval after this one started either way.
    private void ReadChecksIfDue(CancellationToken cancel)
    {
        ServerChecksRequest request;
        lock (_gate)
        {
            if (_checks is null || _disposed || _checksInFlight || !_checks.IsDue(UtcNow))
                return;
            _checksInFlight = true;
            request         = _checks.Start(UtcNow, _backupRpo);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var figures = await _checksRepository!.GetServerChecksAsync(request, cancel).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || cancel.IsCancellationRequested)
                        return;
                    _checks.Apply(figures, UtcNow);
                    // Shown straight away rather than at the next sample; alerts wait for it.
                    if (_consecutiveFailures == 0 && _lastSample is { } sample)
                    {
                        GradeHealth(Grade(sample), sample);
                        Raise(CollectorUpdateKind.State);
                    }
                }
            }
            catch
            {
                // Cancelled by Pause or Dispose, or the server failed: the last read stands
                // until it's too old to trust.
            }
            finally
            {
                lock (_gate)
                    _checksInFlight = false;
            }
        }, CancellationToken.None);
    }

    // Under the lock.
    private void GradeHealth(IReadOnlyList<HealthReading> readings, LiveMetricSample sample)
    {
        var (level, reasons) = HealthRules.Summarize(readings);
        _health        = level;
        _score         = HealthScore.From(readings, _lastSuccess ?? default, _thresholds.Weights);
        _lastGoodScore = _score;
        _healthText    = (reasons.Count == 0
                             ? $"Healthy · CPU {sample.SqlCpuPct:0}%"
                             : string.Join(" · ", reasons))
                         + $" · updated {_lastSuccess:HH:mm:ss}";
        _gradedText    = _healthText;
    }

    // Under the lock: saves the changes and tells the listeners. Neither waits on the disk.
    private void RecordAlerts(IReadOnlyList<AlertChange>? changes)
    {
        if (changes is not { Count: > 0 })
            return;
        _history?.RecordAlerts(changes);
        Raise(CollectorUpdateKind.Alerts, alerts: changes);
    }

    private void StopAlerts() => RecordAlerts(_alerts?.Stop());

    // Under the lock: whatever is being read stops; the next collection gets a new token.
    private void StopCollecting()
    {
        var stopped = _collecting;
        _collecting = new CancellationTokenSource();
        stopped.Cancel();
        stopped.Dispose();
    }

    private void StopLoop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }

    // Under the lock, so updates are raised in the order they happen.
    private void Raise(CollectorUpdateKind kind, LiveMetricSample? sample = null, IReadOnlyList<AlertChange>? alerts = null)
    {
        if (Updated is not { } updated)
            return;
        try { updated(new CollectorUpdate(kind, Snapshot(), sample, alerts)); }
        catch
        {
            // A listener's bug mustn't fail the collection, or show the server as unreachable.
        }
    }

    private CollectorState Snapshot() => new(
        _running, _health, _healthText, _score, _lastGoodScore, _lastSuccess, _failingSince,
        _consecutiveFailures, _lastError, _alerts?.Active.ToList() ?? [], _intervalSeconds, _nextDueUtc);

    private DateTime UtcNow   => _time.GetUtcNow().UtcDateTime;
    private DateTime LocalNow => _time.GetLocalNow().DateTime;

    private TimeSpan CurrentDelay() =>
        HealthRules.NextDelay(TimeSpan.FromSeconds(_intervalSeconds), _consecutiveFailures);

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalSeconds < 60 ? $"{delay.TotalSeconds:0}s" : $"{delay.TotalMinutes:0.#} min";

    /// <summary>Stops collecting for good. Active alerts end, queued to the history and raised before this returns.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _running = false;
            StopLoop();
            _collecting.Cancel();
            StopAlerts();
            _disposed = true;
            _collecting.Dispose();
        }
        Updated = null;
    }
}
