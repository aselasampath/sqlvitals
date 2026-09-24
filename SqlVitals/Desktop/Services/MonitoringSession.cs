using System.Windows.Threading;
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
    private LiveMetricSnapshot?             _previous;
    private Task?                           _inFlight;
    private int                             _consecutiveFailures;
    private DateTime                        _nextDueUtc;
    private int                             _intervalSeconds;
    private bool                            _disposed;

    /// <param name="history">
    /// Saves this session's samples to the local history; null (the ad-hoc Live Metrics session)
    /// keeps nothing beyond the in-memory window.
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

    /// <summary>Starts (or resumes) scheduled collection. The first sample is taken right away.</summary>
    public void Start()
    {
        if (_disposed || IsRunning) return;
        IsRunning   = true;
        _nextDueUtc = DateTime.UtcNow;
        _timer.Start();
        StateChanged?.Invoke(this);
    }

    /// <summary>Stops scheduled collection. The history is kept.</summary>
    public void Pause()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _timer.Stop();
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
            var (level, reasons) = HealthRules.Evaluate(sample);
            Health     = level;
            HealthText = (reasons.Count == 0
                             ? $"Healthy · CPU {sample.SqlCpuPct:0}%"
                             : string.Join(" · ", reasons))
                         + $" · updated {DateTime.Now:HH:mm:ss}";

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

    private TimeSpan CurrentDelay() =>
        HealthRules.NextDelay(TimeSpan.FromSeconds(_intervalSeconds), _consecutiveFailures);

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalSeconds < 60 ? $"{delay.TotalSeconds:0}s" : $"{delay.TotalMinutes:0.#} min";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        SampleAdded  = null;
        StateChanged = null;
    }
}
