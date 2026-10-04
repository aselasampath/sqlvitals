using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Alerts;

/// <summary>
/// The Windows notification for what one sample did to one connection's alerts (#35). Only an
/// alert starting, or a warning alert becoming critical, is worth interrupting the user for; an
/// alert getting worse or ending is left to the Alerts page.
/// </summary>
/// <param name="Severity">The worst severity among the alerts it reports.</param>
/// <param name="Alert">The one alert it reports, whose evidence a click opens (#145); null when it
/// reports several, and a click opens the server's Overview, which lists them.</param>
public sealed record AlertNotification(Guid ConnectionId, HealthLevel Severity, string Title, string Text, Alert? Alert = null)
{
    /// <summary>The longest title and text a Windows notification-area balloon takes.</summary>
    public const int MaxTitleLength = 63;
    public const int MaxTextLength  = 255;

    /// <summary>What a click on a notification about one alert opens: its evidence (#145).</summary>
    public const string EvidenceHint = "Click to see the evidence.";

    /// <summary>What a click on a notification about several alerts opens.</summary>
    public const string OverviewHint = "Click to open the server's Overview.";

    /// <summary>
    /// One notification for every alert that started or escalated on the sample, critical first;
    /// null when there is none.
    /// </summary>
    public static AlertNotification? From(Guid connectionId, string connectionName, IReadOnlyList<AlertChange> changes)
    {
        var raised = changes
            .Where(c => c.Kind is AlertChangeKind.Started or AlertChangeKind.Escalated)
            .OrderByDescending(c => c.Alert.Severity)
            .ThenBy(c => c.Alert.Indicator)
            .ToList();
        if (raised.Count == 0)
            return null;

        var severity = raised.Max(c => c.Alert.Severity);
        string title, text;
        Alert? single = null;

        if (raised.Count == 1)
        {
            var (alert, kind) = (raised[0].Alert, raised[0].Kind);
            single = alert;
            var name = HealthThresholds.Info(alert.Indicator).Name;
            title = kind == AlertChangeKind.Escalated
                ? $"Now critical: {name} on {connectionName}"
                : $"{AlertText.Severity(alert.Severity)}: {name} on {connectionName}";

            var threshold = AlertText.Threshold(alert.Indicator, alert.Threshold);
            text = threshold.Length == 0
                ? $"{Describe(alert)}.\n{EvidenceHint}"
                : $"{Describe(alert)} (threshold {threshold}).\n{EvidenceHint}";
        }
        else
        {
            var critical = raised.Count(c => c.Alert.Severity == HealthLevel.Critical);
            title = critical == 0
                ? $"{raised.Count} alerts on {connectionName}"
                : $"{raised.Count} alerts on {connectionName} ({critical} critical)";

            // As many as fit, then how many more; the hint always stays.
            var lines = raised.Select(c => $"{AlertText.Severity(c.Alert.Severity)}: {Describe(c.Alert)}").ToList();
            var tail  = "\n" + OverviewHint;
            var shown = lines.Count;
            text = string.Join("\n", lines) + tail;
            while (text.Length > MaxTextLength && shown > 1)
            {
                shown--;
                text = string.Join("\n", lines.Take(shown)) + $"\n…and {lines.Count - shown} more" + tail;
            }
        }

        return new AlertNotification(connectionId, severity, Fit(title, MaxTitleLength), Fit(text, MaxTextLength), single);
    }

    /// <summary>The notification-area icon's tooltip: "SqlVitals · 2 active alerts (1 critical)".</summary>
    /// <summary>
    /// The tray icon's tooltip: that monitoring is off when no connection is monitored (#115), so
    /// "no active alerts" can't be read as "all is well"; otherwise the active alerts.
    /// </summary>
    public static string TrayToolTip(IReadOnlyCollection<Alert> active, int monitoredConnections) =>
        monitoredConnections == 0 && active.Count == 0
            ? "SqlVitals · monitoring is off, no alerts are raised"
            : TrayToolTip(active);

    public static string TrayToolTip(IReadOnlyCollection<Alert> active)
    {
        var critical = active.Count(a => a.Severity == HealthLevel.Critical);
        return active.Count switch
        {
            0     => "SqlVitals · no active alerts",
            1     => $"SqlVitals · 1 active alert ({(critical == 1 ? "critical" : "warning")})",
            var n => critical == 0 ? $"SqlVitals · {n} active alerts" : $"SqlVitals · {n} active alerts ({critical} critical)",
        };
    }

    private static string Describe(Alert alert) => AlertText.Describe(alert);

    private static string Fit(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
