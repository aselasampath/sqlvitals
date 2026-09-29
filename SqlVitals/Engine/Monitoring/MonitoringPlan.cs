using System.Globalization;

namespace SqlVitals.Engine.Monitoring;

/// <summary>What <see cref="MonitoringPlan"/> needs to know about one saved connection (#115).</summary>
/// <param name="IsActive">The connection the pages use.</param>
/// <param name="MonitorInBackground">Its "Monitor in background" box.</param>
/// <param name="NeedsPassword">A SQL login without a password saved or typed this run.</param>
/// <param name="NeedsSignIn">An Entra MFA connection the user hasn't signed in to yet this run.</param>
public sealed record MonitoringCandidate(Guid Id, bool IsActive, bool MonitorInBackground, bool NeedsPassword, bool NeedsSignIn);

/// <summary>Why a connection is, or isn't, monitored.</summary>
public enum MonitoringReason
{
    /// <summary>Monitored: it is active, or has Monitor in background, and monitoring starts automatically.</summary>
    Automatic,

    /// <summary>Monitored: the user pressed Start.</summary>
    StartedByUser,

    /// <summary>Not monitored: the user pressed Stop.</summary>
    StoppedByUser,

    /// <summary>Not monitored: Start monitoring automatically is off and nobody pressed Start.</summary>
    AutomaticOff,

    /// <summary>Not monitored: another connection is active and this one's Monitor in background is off.</summary>
    BackgroundOff,

    /// <summary>Can't be monitored: the password isn't saved.</summary>
    NeedsPassword,

    /// <summary>Can't be monitored yet: the Microsoft sign-in hasn't been done this run.</summary>
    NeedsSignIn,
}

/// <summary>Whether one connection is monitored, and why, in words for its tooltip.</summary>
public sealed record MonitoringDecision(MonitoringReason Reason, string Text)
{
    public bool Monitor => Reason is MonitoringReason.Automatic or MonitoringReason.StartedByUser;

    /// <summary>Start can be pressed for it: nothing it needs is missing.</summary>
    public bool CanStart => Reason is not (MonitoringReason.NeedsPassword or MonitoringReason.NeedsSignIn);
}

/// <summary>
/// The Start and Stop the user pressed this run, per connection (#115). They last until SqlVitals
/// exits; the next start follows Start monitoring automatically again. Only the latest press of
/// a connection counts.
/// </summary>
public sealed class MonitoringOverrides
{
    private readonly HashSet<Guid>              _started = new();
    private readonly Dictionary<Guid, DateTime> _stopped = new();

    public void Start(Guid id)
    {
        _stopped.Remove(id);
        _started.Add(id);
    }

    /// <param name="atLocal">When it was stopped, on this PC's clock, for the tooltip.</param>
    public void Stop(Guid id, DateTime atLocal)
    {
        _started.Remove(id);
        _stopped[id] = atLocal;
    }

    public bool IsStarted(Guid id) => _started.Contains(id);

    public DateTime? StoppedAt(Guid id) => _stopped.TryGetValue(id, out var at) ? at : null;

    /// <summary>Forgets every press; Start monitoring automatically decides again.</summary>
    public void Clear()
    {
        _started.Clear();
        _stopped.Clear();
    }

    /// <summary>Forgets the presses of connections that no longer exist.</summary>
    public void KeepOnly(IReadOnlySet<Guid> ids)
    {
        _started.RemoveWhere(id => !ids.Contains(id));
        foreach (var id in _stopped.Keys.Where(id => !ids.Contains(id)).ToList())
            _stopped.Remove(id);
    }
}

/// <summary>
/// Decides which saved connections are monitored (#115). Monitoring means live metrics every few
/// seconds, history snapshots and alerts, so a connection nobody asked to monitor gets none of it.
/// </summary>
public static class MonitoringPlan
{
    public const string AutomaticOffText =
        "Monitoring is off. Start it here or on Live Metrics, or turn on Start monitoring automatically in Settings.";

    public const string BackgroundOffText =
        "Not monitored: Monitor in background is off for this connection. Press ▶ to monitor it until SqlVitals exits.";

    public const string NeedsPasswordText =
        "Not monitored: the password isn't saved. Connect to it once to monitor it.";

    public const string NeedsSignInText =
        "Not monitored yet: switch to it once to complete the Microsoft sign-in.";

    /// <summary>
    /// One decision per connection. What the connection needs comes first, then the user's
    /// Start or Stop this run, then Start monitoring automatically with the active connection and
    /// Monitor in background.
    /// </summary>
    public static IReadOnlyDictionary<Guid, MonitoringDecision> Decide(
        bool startAutomatically, IEnumerable<MonitoringCandidate> connections, MonitoringOverrides overrides)
    {
        var decisions = new Dictionary<Guid, MonitoringDecision>();
        foreach (var conn in connections)
            decisions[conn.Id] = Decide(startAutomatically, conn, overrides);
        return decisions;
    }

    public static MonitoringDecision Decide(bool startAutomatically, MonitoringCandidate conn, MonitoringOverrides overrides)
    {
        if (conn.NeedsPassword)
            return new(MonitoringReason.NeedsPassword, NeedsPasswordText);
        if (conn.NeedsSignIn)
            return new(MonitoringReason.NeedsSignIn, NeedsSignInText);

        if (overrides.StoppedAt(conn.Id) is { } stopped)
            return new(MonitoringReason.StoppedByUser,
                       string.Format(CultureInfo.CurrentCulture, "Stopped by you at {0:HH:mm}. Press ▶ to start it again.", stopped));
        if (overrides.IsStarted(conn.Id))
            return new(MonitoringReason.StartedByUser, "Monitoring: started by you.");

        if (!startAutomatically)
            return new(MonitoringReason.AutomaticOff, AutomaticOffText);
        if (!conn.IsActive && !conn.MonitorInBackground)
            return new(MonitoringReason.BackgroundOff, BackgroundOffText);
        return new(MonitoringReason.Automatic, "Monitoring.");
    }

    /// <summary>
    /// The upgrade rule for Start monitoring automatically: a saved value wins; with none saved,
    /// an installation that already has connections was monitoring them and keeps doing so, and a
    /// new one starts with it off.
    /// </summary>
    public static bool StartAutomatically(bool? saved, bool hasConnections) => saved ?? hasConnections;
}
