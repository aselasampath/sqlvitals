using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Alerts;

/// <summary>
/// The Windows notification for what one sample did to one connection's alerts (#35). Only an
/// alert starting, or a warning alert becoming critical, is worth interrupting the user for; an
/// alert getting worse or ending is left to the Alerts page.
/// </summary>
/// <param name="Severity">The worst severity among the alerts it reports.</param>
public sealed record AlertNotification(Guid ConnectionId, HealthLevel Severity, string Title, string Text)
{
    /// <summary>The longest title and text a Windows notification-area balloon takes.</summary>
    public const int MaxTitleLength = 63;
    public const int MaxTextLength  = 255;

    public const string ClickHint = "Click to open the Alerts page.";

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

        if (raised.Count == 1)
        {
            var (alert, kind) = (raised[0].Alert, raised[0].Kind);
            var name = HealthThresholds.Info(alert.Indicator).Name;
            title = kind == AlertChangeKind.Escalated
                ? $"Now critical: {name} on {connectionName}"
                : $"{AlertText.Severity(alert.Severity)}: {name} on {connectionName}";

            var threshold = AlertText.Threshold(alert.Indicator, alert.Threshold);
            text = threshold.Length == 0
                ? $"{Describe(alert)}.\n{ClickHint}"
                : $"{Describe(alert)} (threshold {threshold}).\n{ClickHint}";
        }
        else
        {
            var critical = raised.Count(c => c.Alert.Severity == HealthLevel.Critical);
            title = critical == 0
                ? $"{raised.Count} alerts on {connectionName}"
                : $"{raised.Count} alerts on {connectionName} ({critical} critical)";

            // As many as fit, then how many more; the hint always stays.
            var lines = raised.Select(c => $"{AlertText.Severity(c.Alert.Severity)}: {Describe(c.Alert)}").ToList();
            var tail  = "\n" + ClickHint;
            var shown = lines.Count;
            text = string.Join("\n", lines) + tail;
            while (text.Length > MaxTextLength && shown > 1)
            {
                shown--;
                text = string.Join("\n", lines.Take(shown)) + $"\n…and {lines.Count - shown} more" + tail;
            }
        }

        return new AlertNotification(connectionId, severity, Fit(title, MaxTitleLength), Fit(text, MaxTextLength));
    }

    /// <summary>The notification-area icon's tooltip: "SqlVitals · 2 active alerts (1 critical)".</summary>
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

    // What the health dot said at the worst figure ("Log 93% full (Sales)"), or the figure itself.
    private static string Describe(Alert alert) =>
        string.IsNullOrWhiteSpace(alert.Detail)
            ? $"{HealthThresholds.Info(alert.Indicator).Name} {AlertText.Value(alert.Indicator, alert.Value)}"
            : alert.Detail;

    private static string Fit(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
