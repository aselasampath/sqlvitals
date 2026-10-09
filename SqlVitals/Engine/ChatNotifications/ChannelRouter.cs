using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// Decides what one chat channel is told about one connection's alert changes (#121, #123), and
/// remembers, in memory only, which alerts it was told about.
/// <list type="bullet">
/// <item>An alert <b>starting</b> is posted when the channel takes that connection, and it's
/// critical or the channel takes warnings.</item>
/// <item>An alert <b>turning critical</b> is posted as "Now critical" when the channel was told
/// about it. When it wasn't (a Critical only channel skipped the warning, or the alert started
/// while the channel was off or before it was added), it's posted as a new critical alert.</item>
/// <item>An alert <b>getting worse</b> posts nothing.</item>
/// <item>An alert <b>ending</b> is posted only when the channel was told about it, even if the
/// channel's filters changed since: ends follow starts. Back to normal is posted when the
/// channel has <see cref="ChatChannel.PostEnds"/> on. An end because monitoring stopped is kept
/// instead (<see cref="TakeStops"/>), for the chat service's one stopped watching message, which
/// lists the alerts still active (#123, #170).</item>
/// </list>
/// Nothing is remembered across restarts, so an alert still happening after one is posted again
/// when a new tracker raises it.
///
/// Not thread-safe: the chat service calls it on the UI thread, where alerts are raised.
/// </summary>
public sealed class ChannelRouter
{
    /// <summary>The name used for a connection no longer saved.</summary>
    public const string DeletedConnection = "a deleted connection";

    // The alerts this channel was told about, until they end, with their connection's name then:
    // a removed connection's name is forgotten before its session stops and ends them (F8).
    private readonly Dictionary<Guid, string> _told = new();

    // Alerts the channel was told about that ended because monitoring stopped, by connection,
    // until the chat service takes them for its stopped watching message.
    private readonly Dictionary<Guid, List<Alert>> _stops = new();

    /// <summary>How many alerts the channel was told about that haven't ended.</summary>
    public int ToldCount => _told.Count;

    /// <summary>The messages to post for one sample's changes of one connection: none, or one of each kind.</summary>
    /// <param name="connectionName">The connection's name now; null when it's no longer saved.</param>
    public IReadOnlyList<ChatMessage> Route(ChatChannel channel, Guid connectionId, string? connectionName,
                                            IReadOnlyList<AlertChange> changes, string pcName)
    {
        var raised    = new List<RaisedAlert>();
        var recovered = new List<Alert>();
        string? toldName = null;

        foreach (var (alert, kind) in changes)
        {
            switch (kind)
            {
                case AlertChangeKind.Started when Takes(channel, connectionId, alert.Severity):
                    _told[alert.Id] = connectionName ?? DeletedConnection;
                    raised.Add(new RaisedAlert(alert));
                    break;

                case AlertChangeKind.Escalated when _told.ContainsKey(alert.Id):
                    raised.Add(new RaisedAlert(alert, NowCritical: true));
                    break;

                case AlertChangeKind.Escalated when Takes(channel, connectionId, HealthLevel.Critical):
                    _told[alert.Id] = connectionName ?? DeletedConnection;
                    raised.Add(new RaisedAlert(alert));
                    break;

                case AlertChangeKind.Ended when _told.Remove(alert.Id, out var name):
                    toldName ??= name;
                    if (alert.EndReason == AlertEndReason.MonitoringStopped)
                        (_stops.TryGetValue(connectionId, out var stops) ? stops : _stops[connectionId] = []).Add(alert);
                    else if (channel.PostEnds)
                        recovered.Add(alert);
                    break;
            }
        }

        var messages = new List<ChatMessage>();
        if (raised.Count > 0)
            messages.Add(ChatMessageBuilder.Raised(connectionName ?? DeletedConnection, raised, pcName));

        if (recovered.Count > 0)
            messages.Add(ChatMessageBuilder.Recovered(connectionName ?? toldName ?? DeletedConnection, recovered, pcName));
        return messages;
    }

    /// <summary>
    /// The alerts of a connection the channel was told about that ended because its monitoring
    /// stopped, since they were last taken; empty when there were none. Taken when SqlVitals
    /// stops watching it, or dropped when it only restarted with new details: a new tracker
    /// raises them again if they're still happening.
    /// </summary>
    public IReadOnlyList<Alert> TakeStops(Guid connectionId) =>
        _stops.Remove(connectionId, out var stops) ? stops : [];

    /// <summary>Forgets every alert, when the channel is turned off.</summary>
    public void Forget()
    {
        _told.Clear();
        _stops.Clear();
    }

    private static bool Takes(ChatChannel channel, Guid connectionId, HealthLevel severity) =>
        channel.Includes(connectionId) && (severity == HealthLevel.Critical || !channel.CriticalOnly);
}
