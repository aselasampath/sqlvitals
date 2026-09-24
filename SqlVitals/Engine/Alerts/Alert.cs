using System.Globalization;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Alerts;

/// <summary>Why an alert ended.</summary>
public enum AlertEndReason
{
    /// <summary>The indicator stayed back within its thresholds for the set number of samples.</summary>
    Recovered,

    /// <summary>
    /// Monitoring of the connection stopped (paused, the connection was edited or removed, the
    /// app was closed), so nobody was watching any more. The end is the last sample taken.
    /// </summary>
    MonitoringStopped,
}

/// <summary>
/// One health indicator of one connection staying past a threshold (#34). Times are UTC on this
/// PC's clock, like the rest of the monitoring history.
/// </summary>
/// <param name="StartedUtc">When the first sample of the breach was taken, not when the alert fired.</param>
/// <param name="EndedUtc">When the first sample back to normal was taken; null while the alert is active.</param>
/// <param name="Severity"><see cref="HealthLevel.Warning"/> or <see cref="HealthLevel.Critical"/>: the worst level it held for long enough to alert.</param>
/// <param name="Value">The worst figure seen during the alert (the lowest for page life expectancy).</param>
/// <param name="Threshold">The threshold of <paramref name="Severity"/> the figure crossed.</param>
/// <param name="Detail">What the health dot said at the worst figure, e.g. "Log 93% full (Sales)".</param>
public sealed record Alert(
    Guid            Id,
    Guid            ConnectionId,
    HealthIndicator Indicator,
    HealthLevel     Severity,
    DateTime        StartedUtc,
    DateTime?       EndedUtc,
    double          Value,
    double?         Threshold,
    string          Detail,
    AlertEndReason? EndReason = null)
{
    public bool IsActive => EndedUtc is null;

    /// <summary>How long it lasted, or has lasted so far.</summary>
    public TimeSpan Duration(DateTime nowUtc)
    {
        var span = (EndedUtc ?? nowUtc) - StartedUtc;
        return span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }
}

/// <summary>What happened to an alert on one sample.</summary>
public enum AlertChangeKind
{
    /// <summary>The breach lasted long enough: a new alert.</summary>
    Started,

    /// <summary>A warning alert stayed critical long enough to become critical.</summary>
    Escalated,

    /// <summary>The figure got worse; <see cref="Alert.Value"/> and <see cref="Alert.Detail"/> changed.</summary>
    Worsened,

    Ended,
}

/// <param name="Alert">The alert as it stands after the change.</param>
public sealed record AlertChange(Alert Alert, AlertChangeKind Kind);

/// <summary>How the Alerts page words an alert's figures.</summary>
public static class AlertText
{
    /// <summary>"93 %", "3.5 min" (blocking), "120 s" (page life expectancy), "3" (memory grants).</summary>
    public static string Value(HealthIndicator indicator, double value)
    {
        var culture = CultureInfo.CurrentCulture;
        return indicator switch
        {
            // Like the health dot's tooltip: "Blocking 3.5 min".
            HealthIndicator.Blocking => value switch
            {
                < 60    => string.Format(culture, "{0:0} s", value),
                < 3_600 => string.Format(culture, "{0:0.#} min", value / 60),
                _       => string.Format(culture, "{0:0.#} h", value / 3_600),
            },
            _ => HealthThresholds.Info(indicator).Unit switch
            {
                "%" => string.Format(culture, "{0:0} %", value),
                "s" => string.Format(culture, "{0:N0} s", value),
                _   => string.Format(culture, "{0:N0}", value),
            },
        };
    }

    /// <summary>"≥ 90 %", "&lt; 300 s": the threshold as the check reads it. Empty when unknown.</summary>
    public static string Threshold(HealthIndicator indicator, double? threshold) =>
        threshold is { } t
            ? (HealthThresholds.Info(indicator).LowerIsWorse ? "< " : "≥ ") + Value(indicator, t)
            : string.Empty;

    /// <summary>"45 s", "12 min", "2 h 05 min", "3 d 4 h".</summary>
    public static string Duration(TimeSpan span)
    {
        var culture = CultureInfo.CurrentCulture;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span switch
        {
            _ when span < TimeSpan.FromMinutes(1) => string.Format(culture, "{0:0} s", Math.Floor(span.TotalSeconds)),
            _ when span < TimeSpan.FromHours(1)   => string.Format(culture, "{0:0} min", Math.Floor(span.TotalMinutes)),
            _ when span < TimeSpan.FromDays(1)    => string.Format(culture, "{0} h {1:00} min", (int)span.TotalHours, span.Minutes),
            _                                     => string.Format(culture, "{0} d {1} h", (int)span.TotalDays, span.Hours),
        };
    }

    public static string Severity(HealthLevel severity) => severity == HealthLevel.Critical ? "Critical" : "Warning";

    public static string EndReason(AlertEndReason? reason) => reason switch
    {
        AlertEndReason.Recovered         => "Back to normal",
        AlertEndReason.MonitoringStopped => "Monitoring stopped",
        _                                => string.Empty,
    };
}
