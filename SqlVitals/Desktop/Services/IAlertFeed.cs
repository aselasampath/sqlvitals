using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// What the Alerts page reads of the monitoring (#187): the alerts saved and those active now,
/// which connections raise them, and when either changes. <see cref="MonitoringManager"/> is the
/// app's; a test gives its own.
/// </summary>
public interface IAlertFeed
{
    /// <summary>The local history, where alerts are saved.</summary>
    IHistoryReader HistoryReader { get; }

    /// <summary>The alerts active now in the running sessions, saved or not.</summary>
    IReadOnlyList<Alert> ActiveAlerts { get; }

    /// <summary>How many connections are monitored now, by the app or a collector.</summary>
    int MonitoredCount { get; }

    /// <summary>Whether a saved connection is monitored now.</summary>
    bool IsMonitoring(Guid? connectionId);

    /// <summary>Why a connection is, or isn't, monitored, in words.</summary>
    string NotMonitoredReason(Guid connectionId);

    /// <summary>Saved connections that raise no alerts because they aren't monitored, by name.</summary>
    IReadOnlyList<string> NotMonitoredNames();

    /// <summary>A connection's alerts started, escalated, worsened or ended; on the UI thread.</summary>
    event Action<Guid, IReadOnlyList<AlertChange>>? AlertsChanged;

    /// <summary>Monitoring started or stopped for a connection; on the UI thread.</summary>
    event Action? MonitoringChanged;
}
