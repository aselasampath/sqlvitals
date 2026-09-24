using System.Windows.Threading;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Collects live metrics for one connection on its own schedule, independently of which page
/// (or which connection) is on screen, and keeps a rolling history of the samples. This is
/// what lets the user switch connections and see a continuous chart instead of an empty one.
///
/// Everything runs on the UI thread: the timer is a <see cref="DispatcherTimer"/> and the
/// queries are awaited asynchronously, so the history can be read by pages without locking.
/// </summary>
public sealed class MonitoringSession : IDisposable
{
    /// <summary>Rolling window kept per connection (and plotted by Live Metrics).</summary>
    public const int MaxPoints = 60;

    public const int DefaultIntervalSeconds = 10;

    private readonly DispatcherTimer        _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<LiveMetricSample> _samples = new();
    private readonly HistoryRecorder?       _history;
    private readonly AlertTracker?          _alerts;
    private LiveMetricSnapshot?             _previous;
    private Task?                           _inFlight;
    private int                             _consecutiveFailures;
    private DateTime                        _nextDueUtc;
    private int                             _intervalSeconds;
    private HealthThresholds                _thresholds = HealthThresholds.Default;
    private DateTime                        _lastSuccess;
    private bool                            _disposed;

    /// <param name="history">
    /// Saves this session's samples and alerts to the local history; null (the ad-hoc Live
    /// Metrics session) keeps nothing beyond the in-memory window and raises no alerts.
    /// </param>
    public MonitoringSession(Guid? connectionId, IWaitStatsRepository repository, string fingerprint,
                             int intervalSeconds = DefaultIntervalSeconds, HistoryRecorder? history = null)
    {
        ConnectionId     = connectionId;
        Repository       = repository;
        Fingerprint      = fingerprint;
        _history         = history;
        _intervalSeconds = Math.Max(1, intervalSeconds);
        _timer.Tick     += Timer_Tick;

        if (connectionId is { } id && history is not null)
            _alerts = new AlertTracker(id);
    }

    /// <summary>The saved connection this session monitors; null for an ad-hoc session.</summary>
    public Guid? ConnectionId { get; }

    public IWaitStatsRepository Repository { get; }

    /// <summary>Saves this session's samples to the local history; null for an ad-hoc session.</summary>
    public HistoryRecorder? History => _history;

    /// <summary>What the repository was built from; a change means the session must be recreated.</summary>
    public string Fingerprint { get; }

    public IReadOnlyList<LiveMetricSample> Samples => _samples;

    public bool IsRunning { get; private set; }

    public HealthLevel Health { get; private set; } = HealthLevel.Unknown;

    /// <summary>One-line, credential-free description of the latest result, for tooltips.</summary>
    public string HealthText { get; private set; } = "Waiting for the first sample…";

    public DateTime? LastSampleTime => _samples.Count > 0 ? _samples[^1].Time : null;

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            var seconds = Math.Max(1, value);
            if (seconds == _intervalSeconds) return;
            _intervalSeconds = seconds;
            _nextDueUtc = DateTime.UtcNow + CurrentDelay();
            StateChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// What the health dot is graded on. A change re-grades the latest sample straight away,
    /// rather than at the next collection.
    /// </summary>
    public HealthThresholds Thresholds
    {
        get => _thresholds;
        set
        {
            if (value == _thresholds) return;
            _thresholds = value;
            if (_consecutiveFailures == 0 && _samples.Count > 0)
            {
                GradeHealth(_samples[^1]);
                StateChanged?.Invoke(this);
            }
        }
    }

    /// <summary>
    /// Samples in a row an indicator must stay past a threshold for an alert to start, and back
    /// to normal for it to end. Applies from the next sample.
    /// </summary>
    public int AlertSamples
    {
        get => _alerts?.Samples ?? AlertSettings.DefaultSamples;
        set
        {
            if (_alerts is not null)
                _alerts.Samples = Math.Max(1, value);
        }
    }

    /// <summary>This connection's alerts that have started and not ended; none for an ad-hoc session.</summary>
    public IReadOnlyList<Alert> ActiveAlerts => _alerts?.Active ?? [];

    /// <summary>Time left before the next scheduled collection; zero when paused.</summary>
    public TimeSpan TimeUntilNextSample
    {
        get
        {
            if (!IsRunning) return TimeSpan.Zero;
            var left = _nextDueUtc - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Raised on the UI thread after each successful collection.</summary>
    public event Action<MonitoringSession, LiveMetricSample>? SampleAdded;

    /// <summary>Raised on the UI thread when health, running state or interval changes.</summary>
    public event Action<MonitoringSession>? StateChanged;

    /// <summary>Raised on the UI thread when alerts start, escalate, worsen or end.</summary>
    public event Action<MonitoringSession, IReadOnlyList<AlertChange>>? AlertsChanged;

    /// <summary>Starts (or resumes) scheduled collection. The first sample is taken right away.</summary>
    public void Start()
    {
        if (_disposed || IsRunning) return;
        IsRunning   = true;
        _nextDueUtc = DateTime.UtcNow;
        _timer.Start();
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Stops scheduled collection. The history is kept; active alerts end, since nothing is
    /// watching any more.
    /// </summary>
    public void Pause()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _timer.Stop();
        StopAlerts();
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Collects a sample now and resets the schedule. Joins a collection already in flight rather
    /// than starting a second one. Throws when the collection fails, so callers can report it.
    /// </summary>
    public Task CollectNowAsync()
    {
        if (_inFlight is not null)
            return _inFlight;

        var task = CollectAsync();
        // A collection that failed synchronously has already cleared _inFlight in its finally
        // block; storing the finished task would block every later collection.
        if (!task.IsCompleted)
            _inFlight = task;
        return task;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_inFlight is not null || DateTime.UtcNow < _nextDueUtc)
            return;

        try
        {
            await CollectNowAsync();
        }
        catch
        {
            // Recorded in Health/HealthText and retried with backoff.
        }
    }

    private async Task CollectAsync()
    {
        try
        {
            var snapshot = await Repository.GetLiveMetricsAsync();
            if (_disposed) return;

            var isFirst = _previous is null;
            var sample  = LiveMetricSample.From(_previous, snapshot);
            _previous = snapshot;

            _samples.Add(sample);
            if (_samples.Count > MaxPoints)
                _samples.RemoveRange(0, _samples.Count - MaxPoints);

            _consecutiveFailures = 0;
            _lastSuccess         = DateTime.Now;
            var readings = HealthRules.Grade(sample, _thresholds);
            GradeHealth(readings, sample);
            // Every collected sample counts towards an alert, the first one too: the health
            // gauges don't need a previous sample. A failed collection counts neither way, and
            // neither does one finishing after Pause, which has already ended the alerts.
            if (IsRunning)
                RecordAlerts(_alerts?.Evaluate(readings, DateTime.UtcNow));

            // Neither call throws or waits on the disk. The first sample's rates are all zero
            // (nothing to diff against), so it isn't history. Details are only read from a
            // server that just answered, never from one that is failing.
            if (_history is not null)
            {
                if (!isFirst)
                    _history.RecordSample(sample);
                _ = _history.CollectDetailIfDueAsync();
            }

            SampleAdded?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            if (_disposed) throw;

            _consecutiveFailures++;
            Health     = HealthLevel.Unavailable;
            var detail = ex is WaitStatsException { InnerException: { } inner } ? inner.Message : ex.Message;
            HealthText = $"Monitoring failed at {DateTime.Now:HH:mm:ss}: " +
                         ConnectionSettingsService.RedactSecrets(detail) +
                         $" Retrying in {FormatDelay(CurrentDelay())}.";
            throw;
        }
        finally
        {
            _inFlight   = null;
            _nextDueUtc = DateTime.UtcNow + CurrentDelay();
            if (!_disposed)
                StateChanged?.Invoke(this);
        }
    }

    private void GradeHealth(LiveMetricSample sample) => GradeHealth(HealthRules.Grade(sample, _thresholds), sample);

    private void GradeHealth(IReadOnlyList<HealthReading> readings, LiveMetricSample sample)
    {
        var (level, reasons) = HealthRules.Summarize(readings);
        Health     = level;
        HealthText = (reasons.Count == 0
                         ? $"Healthy · CPU {sample.SqlCpuPct:0}%"
                         : string.Join(" · ", reasons))
                     + $" · updated {_lastSuccess:HH:mm:ss}";
    }

    // Saves the changes and tells the app. Neither waits on the disk.
    private void RecordAlerts(IReadOnlyList<AlertChange>? changes)
    {
        if (changes is not { Count: > 0 })
            return;
        _history?.RecordAlerts(changes);
        AlertsChanged?.Invoke(this, changes);
    }

    private void StopAlerts() => RecordAlerts(_alerts?.Stop());

    private TimeSpan CurrentDelay() =>
        HealthRules.NextDelay(TimeSpan.FromSeconds(_intervalSeconds), _consecutiveFailures);

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalSeconds < 60 ? $"{delay.TotalSeconds:0}s" : $"{delay.TotalMinutes:0.#} min";

    /// <summary>Stops collecting for good. Active alerts end, and are queued to the history first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        StopAlerts();
        SampleAdded   = null;
        StateChanged  = null;
        AlertsChanged = null;
    }
}
