using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// Decides what one chat channel is told about one connection's alert changes (#121), and
/// remembers, in memory only, which alerts it was told about.
/// <list type="bullet">
/// <item>An alert <b>starting</b> is posted when the channel takes that connection, and it's
/// critical or the channel takes warnings.</item>
/// <item>An alert <b>turning critical</b> is posted as "Now critical" when the channel was told
/// about it. When it wasn't (a Critical only channel skipped the warning, or the alert started
/// while the channel was off or before it was added), it's posted as a new critical alert.</item>
/// <item>An alert <b>getting worse</b> posts nothing.</item>
/// <item>An alert <b>ending</b> is forgotten. Posting the end comes in #123, and only for alerts
/// the channel was told about.</item>
/// </list>
/// Nothing is remembered across restarts, so an alert still happening after one is posted again
/// when a new tracker raises it.
///
/// Not thread-safe: the chat service calls it on the UI thread, where alerts are raised.
/// </summary>
public sealed class ChannelRouter
{
    // The alerts this channel was told about, until they end.
    private readonly HashSet<Guid> _told = new();

    /// <summary>How many alerts the channel was told about that haven't ended.</summary>
    public int ToldCount => _told.Count;

    /// <summary>The messages to post for one sample's changes of one connection: none, or one.</summary>
    public IReadOnlyList<ChatMessage> Route(ChatChannel channel, Guid connectionId, string connectionName,
                                            IReadOnlyList<AlertChange> changes, string pcName)
    {
        var raised = new List<RaisedAlert>();
        foreach (var (alert, kind) in changes)
        {
            switch (kind)
            {
                case AlertChangeKind.Started when Takes(channel, connectionId, alert.Severity):
                    _told.Add(alert.Id);
                    raised.Add(new RaisedAlert(alert));
                    break;

                case AlertChangeKind.Escalated when _told.Contains(alert.Id):
                    raised.Add(new RaisedAlert(alert, NowCritical: true));
                    break;

                case AlertChangeKind.Escalated when Takes(channel, connectionId, HealthLevel.Critical):
                    _told.Add(alert.Id);
                    raised.Add(new RaisedAlert(alert));
                    break;

                case AlertChangeKind.Ended:
                    _told.Remove(alert.Id);
                    break;
            }
        }

        return raised.Count == 0 ? [] : [ChatMessageBuilder.Raised(connectionName, raised, pcName)];
    }

    /// <summary>Forgets every alert, when the channel is turned off.</summary>
    public void Forget() => _told.Clear();

    private static bool Takes(ChatChannel channel, Guid connectionId, HealthLevel severity) =>
        channel.Includes(connectionId) && (severity == HealthLevel.Critical || !channel.CriticalOnly);
}
