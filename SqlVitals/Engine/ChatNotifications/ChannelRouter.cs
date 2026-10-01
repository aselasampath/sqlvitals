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
/// channel has <see cref="ChatChannel.PostEnds"/> on. Monitoring stopped is posted either way,
/// as one message for the connection's alerts, because it says no more updates are coming.</item>
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

    // Stopped ends kept at Exit for one message (see CollectingStops).
    private readonly List<StoppedConnection> _stops = new();

    /// <summary>How many alerts the channel was told about that haven't ended.</summary>
    public int ToldCount => _told.Count;

    /// <summary>
    /// SqlVitals is exiting (#123, F5): the stopped monitoring ends of every connection are kept
    /// for one message, <see cref="TakeStops"/>, instead of one per connection, so a channel's
    /// last word fits in the few seconds SqlVitals has left to send.
    /// </summary>
    public bool CollectingStops { get; set; }

    /// <summary>The messages to post for one sample's changes of one connection: none, or one of each kind.</summary>
    /// <param name="connectionName">The connection's name now; null when it's no longer saved.</param>
    public IReadOnlyList<ChatMessage> Route(ChatChannel channel, Guid connectionId, string? connectionName,
                                            IReadOnlyList<AlertChange> changes, string pcName)
    {
        var raised    = new List<RaisedAlert>();
        var recovered = new List<Alert>();
        var stopped   = new List<Alert>();
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
                        stopped.Add(alert);
                    else if (channel.PostEnds)
                        recovered.Add(alert);
                    break;
            }
        }

        var messages = new List<ChatMessage>();
        if (raised.Count > 0)
            messages.Add(ChatMessageBuilder.Raised(connectionName ?? DeletedConnection, raised, pcName));

        var endedName = connectionName ?? toldName ?? DeletedConnection;
        if (recovered.Count > 0)
            messages.Add(ChatMessageBuilder.Recovered(endedName, recovered, pcName));
        if (stopped.Count > 0)
        {
            var connection = new StoppedConnection(endedName, stopped);
            if (CollectingStops)
                _stops.Add(connection);
            else
                messages.Add(ChatMessageBuilder.Stopped([connection], pcName));
        }
        return messages;
    }

    /// <summary>
    /// The one stopped monitoring message for the connections collected at Exit, or null when the
    /// channel wasn't told about any alert that was still active.
    /// </summary>
    public ChatMessage? TakeStops(string pcName)
    {
        if (_stops.Count == 0)
            return null;
        var message = ChatMessageBuilder.Stopped(_stops.ToList(), pcName);
        _stops.Clear();
        return message;
    }

    /// <summary>Forgets every alert, when the channel is turned off.</summary>
    public void Forget()
    {
        _told.Clear();
        _stops.Clear();
    }

    private static bool Takes(ChatChannel channel, Guid connectionId, HealthLevel severity) =>
        channel.Includes(connectionId) && (severity == HealthLevel.Critical || !channel.CriticalOnly);
}
