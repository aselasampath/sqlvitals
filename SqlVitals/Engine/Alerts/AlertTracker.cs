using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Alerts;

/// <summary>
/// Turns one connection's graded samples into alerts (#34). Every collected sample is checked,
/// one indicator at a time:
/// <list type="bullet">
/// <item>An alert <b>starts</b> once an indicator has been past its warning (or critical)
/// threshold for <see cref="Samples"/> samples in a row. Its start is the first of those samples.</item>
/// <item>It becomes <b>critical</b> once the indicator has been critical for that many samples in a row.</item>
/// <item>It <b>ends</b> once the indicator has been back within its thresholds for that many samples
/// in a row. Its end is the first of those samples.</item>
/// </list>
/// Needing a run to end as well as to start is what keeps a figure hovering around a threshold
/// to one alert rather than a new one every few samples.
///
/// Not thread-safe; the session that owns it calls it on one thread.
/// </summary>
public sealed class AlertTracker(Guid connectionId, int samples = AlertSettings.DefaultSamples)
{
    private readonly Dictionary<HealthIndicator, IndicatorState> _states = new();
    private int       _samples = CheckSamples(samples);
    private DateTime? _lastSampleUtc;

    public Guid ConnectionId { get; } = connectionId;

    /// <summary>
    /// Samples in a row an indicator must stay past a threshold for an alert to start, and back
    /// to normal for it to end. A change applies from the next sample.
    /// </summary>
    public int Samples
    {
        get => _samples;
        set => _samples = CheckSamples(value);
    }

    /// <summary>The alerts that have started and not ended, in <see cref="HealthIndicator"/> order.</summary>
    public IReadOnlyList<Alert> Active =>
        _states.OrderBy(s => s.Key).Select(s => s.Value.Active).OfType<Alert>().ToList();

    /// <summary>
    /// Checks one sample's readings, taken at <paramref name="nowUtc"/> on this PC's clock.
    /// Returns what changed: alerts started, escalated, worsened or ended.
    /// </summary>
    public IReadOnlyList<AlertChange> Evaluate(IReadOnlyList<HealthReading> readings, DateTime nowUtc)
    {
        _lastSampleUtc = nowUtc;
        var changes = new List<AlertChange>();

        foreach (var reading in readings)
        {
            if (!_states.TryGetValue(reading.Indicator, out var state))
                _states[reading.Indicator] = state = new IndicatorState();

            var change = reading.Level is HealthLevel.Warning or HealthLevel.Critical
                ? Breached(state, reading, nowUtc)
                : Normal(state, nowUtc);
            if (change is not null)
                changes.Add(change);
        }

        return changes;
    }

    /// <summary>
    /// Ends every active alert because monitoring stopped (paused, the connection was edited or
    /// removed, the app closing), at the last sample taken, and forgets any run in progress.
    /// </summary>
    public IReadOnlyList<AlertChange> Stop()
    {
        var changes = new List<AlertChange>();
        foreach (var state in _states.Values)
        {
            if (state.Active is { } alert)
            {
                var end = _lastSampleUtc is { } last && last > alert.StartedUtc ? last : alert.StartedUtc;
                changes.Add(new AlertChange(
                    alert with { EndedUtc = end, EndReason = AlertEndReason.MonitoringStopped },
                    AlertChangeKind.Ended));
            }
        }

        _states.Clear();
        return changes.OrderBy(c => c.Alert.Indicator).ToList();
    }

    private AlertChange? Breached(IndicatorState state, HealthReading reading, DateTime nowUtc)
    {
        if (state.BreachRun == 0)
        {
            state.BreachStartUtc = nowUtc;
            state.Worst          = null;
        }
        state.BreachRun++;
        state.CriticalRun = reading.Level == HealthLevel.Critical ? state.CriticalRun + 1 : 0;
        state.NormalRun   = 0;

        if (state.Worst is null || IsWorse(reading.Indicator, reading.Value, state.Worst.Value))
            state.Worst = reading;

        if (state.Active is not { } alert)
        {
            if (state.BreachRun < _samples)
                return null;

            var severity = state.CriticalRun >= _samples ? HealthLevel.Critical : HealthLevel.Warning;
            state.Active = new Alert(
                Guid.NewGuid(), ConnectionId, reading.Indicator, severity,
                state.BreachStartUtc, EndedUtc: null,
                state.Worst.Value, Limit(reading.Limits, severity), state.Worst.Reason ?? string.Empty);
            return new AlertChange(state.Active, AlertChangeKind.Started);
        }

        AlertChangeKind? kind = null;
        if (alert.Severity == HealthLevel.Warning && state.CriticalRun >= _samples)
        {
            alert = alert with { Severity = HealthLevel.Critical, Threshold = Limit(reading.Limits, HealthLevel.Critical) };
            kind  = AlertChangeKind.Escalated;
        }
        if (IsWorse(reading.Indicator, reading.Value, alert.Value))
        {
            alert = alert with { Value = reading.Value, Detail = reading.Reason ?? alert.Detail };
            kind ??= AlertChangeKind.Worsened;
        }

        if (kind is null)
            return null;
        state.Active = alert;
        return new AlertChange(alert, kind.Value);
    }

    private AlertChange? Normal(IndicatorState state, DateTime nowUtc)
    {
        state.BreachRun   = 0;
        state.CriticalRun = 0;
        state.Worst       = null;

        if (state.Active is not { } alert)
            return null;

        if (state.NormalRun == 0)
            state.NormalStartUtc = nowUtc;
        state.NormalRun++;
        if (state.NormalRun < _samples)
            return null;

        state.Active    = null;
        state.NormalRun = 0;
        return new AlertChange(
            alert with { EndedUtc = state.NormalStartUtc, EndReason = AlertEndReason.Recovered },
            AlertChangeKind.Ended);
    }

    private static bool IsWorse(HealthIndicator indicator, double value, double than) =>
        HealthThresholds.Info(indicator).LowerIsWorse ? value < than : value > than;

    private static double? Limit(HealthThreshold limits, HealthLevel severity) =>
        severity == HealthLevel.Critical ? limits.Critical : limits.Warning;

    private static int CheckSamples(int samples) =>
        samples >= 1
            ? samples
            : throw new ArgumentOutOfRangeException(nameof(samples), samples, "At least one sample is needed.");

    private sealed class IndicatorState
    {
        public int            BreachRun;        // samples in a row past a threshold
        public int            CriticalRun;      // of those, critical in a row
        public int            NormalRun;        // samples in a row back to normal, while an alert is active
        public DateTime       BreachStartUtc;
        public DateTime       NormalStartUtc;
        public HealthReading? Worst;            // worst reading of the breach run, before the alert starts
        public Alert?         Active;
    }
}
