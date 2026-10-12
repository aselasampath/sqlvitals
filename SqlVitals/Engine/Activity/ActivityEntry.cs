namespace SqlVitals.Engine.Activity;

/// <summary>
/// One row of the activity log (#115): monitoring started or stopped, the app started or exited,
/// a setting or connection changed, history cleared. Kept in history.db beside the alerts, so a gap
/// in the history, or an alert that never came, can be explained later. Never holds a secret:
/// see <see cref="SettingsDiff"/>.
/// </summary>
/// <param name="TimeUtc">When it happened, on this PC's clock.</param>
/// <param name="Event">What happened; one of <see cref="ActivityEvents"/>.</param>
/// <param name="ConnectionId">The connection it's about; null for an app-wide event.</param>
/// <param name="ConnectionName">
/// The connection's name at the time, copied in so the row still reads right after the connection
/// is renamed or removed.
/// </param>
/// <param name="Source">Who or what caused it; one of <see cref="ActivitySources"/>.</param>
/// <param name="Setting">For a settings change: which setting (or connection field).</param>
/// <param name="OldValue">For a settings change: its value before; null for a secret.</param>
/// <param name="NewValue">For a settings change: its value after; null for a secret.</param>
/// <param name="Detail">Anything else, in plain words.</param>
public sealed record ActivityEntry(
    DateTime TimeUtc,
    string   Event,
    Guid?    ConnectionId,
    string?  ConnectionName,
    string   Source,
    string?  Setting  = null,
    string?  OldValue = null,
    string?  NewValue = null,
    string?  Detail   = null)
{
    /// <summary>The Windows account SqlVitals ran as, so a copied history.db still says who did it.</summary>
    public string WindowsUser { get; init; } = string.Empty;

    /// <summary>The PC SqlVitals ran on.</summary>
    public string Machine { get; init; } = string.Empty;
}

/// <summary>An activity log row as read back, newest first, with its row id.</summary>
public sealed record StoredActivity(long EventId, ActivityEntry Entry);

/// <summary>The events the activity log records, as stored in its event column.</summary>
public static class ActivityEvents
{
    public const string AppStarted         = "App started";
    public const string AppExited          = "App exited";
    public const string MonitoringStarted  = "Monitoring started";
    public const string MonitoringStopped  = "Monitoring stopped";
    public const string SettingChanged     = "Setting changed";
    public const string ConnectionAdded    = "Connection added";
    public const string ConnectionEdited   = "Connection edited";
    public const string ConnectionRemoved  = "Connection removed";
    public const string HistoryCleared     = "History cleared";
    /// <summary>Clear stored query text (#175): the text of every query deleted, the rest of the history kept.</summary>
    public const string QueryTextCleared   = "Query text cleared";
    public const string ChatChannelAdded   = "Chat channel added";
    public const string ChatChannelEdited  = "Chat channel edited";
    public const string ChatChannelRemoved = "Chat channel removed";

    /// <summary>Every event, for the log's filter.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AppStarted, AppExited, MonitoringStarted, MonitoringStopped,
        SettingChanged, ConnectionAdded, ConnectionEdited, ConnectionRemoved, HistoryCleared, QueryTextCleared,
        ChatChannelAdded, ChatChannelEdited, ChatChannelRemoved,
    ];

    /// <summary>The detail of an App started row when the run before it has no App exited row.</summary>
    public const string PreviousRunDidNotExit = "The previous run didn't exit cleanly (killed, crashed, or the PC turned off).";
}

/// <summary>Who or what caused an activity log event, as stored in its source column.</summary>
public static class ActivitySources
{
    public const string LiveMetrics         = "user: Live Metrics";
    /// <summary>The sidebar's combo box, until 0.57.0 (#141): kept for the rows it wrote.</summary>
    public const string ConnectionSelector  = "user: connection selector";
    /// <summary>The open server's Start/Stop in the sidebar, or one of its pages opened there (#141).</summary>
    public const string Sidebar             = "user: sidebar";
    /// <summary>Another server picked from the breadcrumb's server menu (#141).</summary>
    public const string ServerMenu          = "user: server menu";
    public const string TrayMenu            = "user: tray menu";
    public const string Settings            = "user: Settings";
    public const string Home                = "user: Home";
    /// <summary>Start or Pause on a server's Overview, or a page opened from it (#143).</summary>
    public const string Overview            = "user: Overview";
    public const string BackOrForward       = "user: Back or Forward";
    /// <summary>A server's page opened from the Ctrl+K quick switcher (#144).</summary>
    public const string QuickSwitcher       = "user: quick switcher";
    /// <summary>A server's page opened as an alert's evidence from its Windows notification (#145).</summary>
    public const string Notification        = "user: notification";
    /// <summary>A server's page opened as an alert's evidence from the Alerts page (#145).</summary>
    public const string AlertsPage          = "user: Alerts";
    public const string AutomaticAtStartup  = "automatic at startup";
    public const string SettingChanged      = "setting changed";
    public const string ConnectionEdited    = "connection edited";
    public const string AppExit             = "app exit";
    /// <summary>The SqlVitals Collector service (#172): its start and stop, and the servers it watches.</summary>
    public const string CollectorService    = "collector service";
    /// <summary>The collector's settings file changed while it ran (#172): a server added, removed or edited.</summary>
    public const string CollectorSettings   = "collector settings changed";

    /// <summary>A setting saved on a page other than Settings, e.g. "user: Backups".</summary>
    public static string Page(string name) => $"user: {name}";
}

/// <summary>How long the activity log is kept, whatever the history retention.</summary>
public static class ActivityLog
{
    public const int RetentionDays = 365;

    public static TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
}
