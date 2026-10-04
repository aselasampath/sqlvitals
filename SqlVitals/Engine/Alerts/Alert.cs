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

    /// <summary>What the health dot said at the worst figure ("Log 93% full (Sales)"), or the figure itself ("CPU 97 %").</summary>
    public static string Describe(Alert alert) =>
        string.IsNullOrWhiteSpace(alert.Detail)
            ? $"{HealthThresholds.Info(alert.Indicator).Name} {Value(alert.Indicator, alert.Value)}"
            : alert.Detail;

    /// <summary>
    /// The worst figure against its threshold, as the Alerts page's columns show them:
    /// "97 % (threshold ≥ 90 %)", or "97 %" when the threshold isn't known.
    /// </summary>
    public static string Figure(Alert alert)
    {
        var threshold = Threshold(alert.Indicator, alert.Threshold);
        var value     = Value(alert.Indicator, alert.Value);
        return threshold.Length == 0 ? value : $"{value} (threshold {threshold})";
    }

    /// <summary>
    /// What the health dot said, when it adds to the figure: how many sessions are blocked, or
    /// which database's log is full. Null for the indicators whose detail only repeats the figure.
    /// </summary>
    public static string? Context(Alert alert) =>
        alert.Indicator is HealthIndicator.Blocking or HealthIndicator.LogSpace && !string.IsNullOrWhiteSpace(alert.Detail)
            ? alert.Detail
            : null;

    /// <summary>
    /// What is happening, in plain words, for the Overview's active alerts (#143): "SQL Server's
    /// CPU reached 97 % (critical at ≥ 90 %)." The figure is the worst seen during the alert.
    /// </summary>
    public static string Happening(Alert alert)
    {
        var value     = Value(alert.Indicator, alert.Value);
        var threshold = Threshold(alert.Indicator, alert.Threshold);
        var limit     = threshold.Length == 0 ? "" : $" ({Severity(alert.Severity).ToLowerInvariant()} at {threshold})";
        return alert.Indicator switch
        {
            HealthIndicator.Cpu                 => $"SQL Server's CPU reached {value}{limit}.",
            HealthIndicator.Blocking            => $"Requests are blocked: the longest has waited {value}{limit}." + Also(alert.Detail),
            HealthIndicator.PageLifeExpectancy  => $"Pages leave the buffer pool after only {value}{limit}: memory pressure.",
            HealthIndicator.MemoryGrantsPending => alert.Value == 1
                                                       ? $"1 query is waiting for memory to start running{limit}."
                                                       : $"{value} queries are waiting for memory to start running{limit}.",
            HealthIndicator.LogSpace            => $"The fullest transaction log is {value} full{limit}." + Also(alert.Detail),
            HealthIndicator.TempDbSpace         => $"TempDB's data files are {value} full{limit}.",
            _                                   => Describe(alert),
        };

        // What the health dot said, when it names more: "Blocking 3.5 min (3 blocked)".
        static string Also(string detail) => string.IsNullOrWhiteSpace(detail) ? "" : $" {detail}.";
    }

    public static string EndReason(AlertEndReason? reason) => reason switch
    {
        AlertEndReason.Recovered         => "Back to normal",
        AlertEndReason.MonitoringStopped => "Monitoring stopped",
        _                                => string.Empty,
    };
}
