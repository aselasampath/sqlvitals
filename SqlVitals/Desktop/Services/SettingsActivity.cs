using System.Globalization;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// The settings file as the activity log names it (#115): every setting and connection field,
/// as text, under the name Settings shows it by. <see cref="SettingsDiff"/> compares two of these;
/// a secret is listed under a name in <see cref="SettingsDiff.SecretNames"/>, so its value is never
/// logged. A setting added to <see cref="ConnectionStore"/> must be added here to be logged.
/// </summary>
internal static class SettingsActivity
{
    public static SettingsSnapshot Snapshot(ConnectionStore store)
    {
        var culture = CultureInfo.InvariantCulture;
        var settings = new List<SettingValue>
        {
            new("Start monitoring automatically", OnOff(store.StartMonitoringAutomatically)),
            new("Command timeout", $"{store.CommandTimeoutSeconds} s"),
            new("History snapshot interval", $"{store.HistoryIntervalMinutes} min"),
            new("History retention", $"{store.HistoryRetentionDays} days"),
            new("Alert samples", store.AlertSamples.ToString(culture)),
            new("Keep running in the notification area", OnOff(store.KeepRunningInTray)),
            new("Query regression threshold", string.Format(culture, "{0:0.##}%", store.RegressionThresholdPct)),
            new("Query regression minimum executions", store.RegressionMinExecutions.ToString(culture)),
            new("Backup RPO (full)", Minutes(store.BackupRpo.FullMaxAge)),
            new("Backup RPO (log)", Minutes(store.BackupRpo.LogMaxAge)),
            new("File latency threshold (data)", string.Format(culture, "{0:0.##} ms", store.FileLatencyThresholds.DataMs)),
            new("File latency threshold (log)", string.Format(culture, "{0:0.##} ms", store.FileLatencyThresholds.LogMs)),
            new("File I/O refresh", $"{store.FileIoIntervalSeconds} s"),
            new("Processes refresh", $"{store.ProcessesRefreshSeconds} s"),
            new("Processes auto-refresh", OnOff(store.ProcessesAutoRefresh)),
            new("Query Store charts", string.Join(", ", store.QueryStoreChartMetrics)),
        };
        settings.AddRange(Thresholds("Health threshold", store.HealthThresholds));

        var connections = store.Connections.Select(c =>
        {
            var fields = new List<SettingValue>
            {
                new("Name", c.DisplayName),
                new("Server", c.Server.Trim()),
                new("Authentication", c.AuthenticationLabel),
                new("User name", c.UserName.Trim()),
                // Secret: compared, never logged (see SettingsDiff.SecretNames).
                new("Password", c.Password),
                new("Remember password", OnOff(c.SavePassword)),
                new("Database", c.DatabaseLabel),
                new("Encrypt", c.Encrypt.ToString()),
                new("Trust server certificate", OnOff(c.TrustServerCertificate)),
                new("Connection timeout", $"{c.ConnectTimeoutSeconds} s"),
                // Secret: a piece of connection string, which may carry anything.
                new("Additional parameters", c.AdditionalParameters.Trim()),
                new("Monitor in background", OnOff(c.MonitorInBackground)),
                new("Windows notifications", store.NotifiesFor(c.Id) ? "On" : "Muted"),
                new("Own health thresholds", OnOff(c.HealthThresholds is not null)),
            };
            if (c.HealthThresholds is { } own)
                fields.AddRange(Thresholds("Own health threshold", own));
            return new ConnectionSnapshot(c.Id, c.DisplayName, fields);
        }).ToList();

        return new SettingsSnapshot(settings, connections);
    }

    /// <summary>A connection's one-line description for Connection added, without credentials.</summary>
    public static Func<ConnectionSnapshot, string?> Describe(ConnectionStore store) =>
        snapshot => store.Connections.FirstOrDefault(c => c.Id == snapshot.Id)?.Summary;

    private static IEnumerable<SettingValue> Thresholds(string prefix, HealthThresholds thresholds) =>
        HealthThresholds.Indicators.Select(info =>
        {
            var t = thresholds.Get(info.Indicator);
            return new SettingValue($"{prefix}: {info.Name}",
                                    $"warning {Level(t.Warning, info.Unit)}, critical {Level(t.Critical, info.Unit)}");
        });

    private static string Level(double? value, string unit) =>
        value is { } v ? string.Format(CultureInfo.InvariantCulture, "{0:0.##}{1}", v, unit) : "off";

    private static string Minutes(TimeSpan span) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0} min", span.TotalMinutes);

    private static string OnOff(bool value) => value ? "On" : "Off";
}
