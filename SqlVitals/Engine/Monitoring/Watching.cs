namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// Why SqlVitals stopped watching a connection (#170): the user, a setting, the app closing, or
/// Windows. Said in the chat channels' stopped watching message.
/// </summary>
public enum WatchStop
{
    /// <summary>Someone pressed Stop: on Live Metrics, the sidebar, Overview or the tray menu.</summary>
    StoppedByUser,

    /// <summary>Start monitoring automatically was turned off.</summary>
    AutomaticOff,

    /// <summary>Another connection was made active, and Monitor in background is off for this one.</summary>
    BackgroundOff,

    /// <summary>Its saved password or Microsoft sign-in is missing now.</summary>
    NeedsSetup,

    /// <summary>The connection was removed from SqlVitals.</summary>
    ConnectionRemoved,

    /// <summary>SqlVitals exited: Exit in the tray menu, or the window closed with Keep running in the tray off.</summary>
    AppExit,

    /// <summary>Windows is shutting down or restarting.</summary>
    WindowsShutdown,

    /// <summary>The Windows user is signing out.</summary>
    WindowsSignOut,

    /// <summary>The PC is going to sleep or hibernating. SqlVitals keeps running, but collects nothing.</summary>
    Sleep,
}

/// <summary>A connection SqlVitals started watching (#170).</summary>
/// <param name="LastWatchedUtc">
/// When it was last watched before: when it stopped earlier in this run, else its last sample in
/// the history. Null when it was never watched, or its history was cleared.
/// </param>
public sealed record WatchedConnection(Guid Id, string Name, DateTime? LastWatchedUtc = null);

/// <summary>A connection SqlVitals stopped watching, and why (#170).</summary>
public sealed record StoppedWatching(Guid Id, string Name, WatchStop Why);

/// <summary>
/// What changed in what SqlVitals watches, in one go (#170): connections started and stopped.
/// A connection restarted with new details (it was edited) is in neither list, but in
/// <see cref="Restarted"/>: it was never unwatched.
/// </summary>
public sealed record WatchChange(
    IReadOnlyList<WatchedConnection> Started,
    IReadOnlyList<StoppedWatching>   Stopped,
    IReadOnlyList<Guid>              Restarted)
{
    public bool IsEmpty => Started.Count == 0 && Stopped.Count == 0 && Restarted.Count == 0;

    public static WatchStop StopFor(MonitoringReason reason) => reason switch
    {
        MonitoringReason.StoppedByUser => WatchStop.StoppedByUser,
        MonitoringReason.AutomaticOff  => WatchStop.AutomaticOff,
        MonitoringReason.BackgroundOff => WatchStop.BackgroundOff,
        _                              => WatchStop.NeedsSetup,
    };
}
