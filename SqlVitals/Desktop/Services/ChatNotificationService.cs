using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Posts to the Slack and Microsoft Teams channels saved in Settings (#108). Lives with the app,
/// not the window, and hears alerts from <see cref="MonitoringManager.AlertsRaised"/>, so it carries
/// on while the window is hidden in the notification area.
///
/// Each channel has a <see cref="ChannelRouter"/> (what to post), run where alerts are raised, and
/// a <see cref="ChannelQueue"/> (sending, in order, on a worker of its own). Deciding takes no
/// time and sending never blocks the caller, so monitoring and the UI never wait on a webhook.
///
/// #120: the channels and Send test message. #121: posting new and critical alerts. Retries and
/// the Failing status come in #122, and the end of alerts in #123.
///
/// Used on the UI thread; the queues report back to it through its synchronization context.
/// </summary>
public sealed class ChatNotificationService : IDisposable
{
    private readonly IWebhookSender          _sender;
    private readonly string                  _pcName;
    private readonly Action<string>          _log;
    private readonly SynchronizationContext? _ui;

    // Replaced as a whole by Apply, so the queues' workers can read it at any time.
    private volatile IReadOnlyList<ChatChannel> _channels = [];
    private IReadOnlyDictionary<Guid, string> _connectionNames = new Dictionary<Guid, string>();

    private readonly Dictionary<Guid, ChannelRouter> _routers = new();
    private readonly Dictionary<Guid, ChannelQueue>  _queues  = new();
    // Written by MarkPosted, on the UI thread (or a queue's worker when there's no UI, in tests).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ChannelStatus> _status = new();

    /// <param name="log">Where posts and failures are written: the app log, or a list in tests. Never given a URL.</param>
    /// <param name="ui">The UI thread's context, where status changes are raised; null raises them on the queue's worker (tests).</param>
    public ChatNotificationService(IWebhookSender sender, string? pcName = null, Action<string>? log = null,
                                   SynchronizationContext? ui = null)
    {
        _sender = sender;
        _pcName = pcName ?? Environment.MachineName;
        _log    = log ?? AppLog.Info;
        _ui     = ui;
    }

    /// <summary>A channel's status changed, e.g. a message reached it. Raised on the UI thread.</summary>
    public event Action<Guid>? StatusChanged;

    /// <summary>The channels as last applied, in the order they were saved.</summary>
    public IReadOnlyList<ChatChannel> Channels => _channels;

    /// <summary>
    /// Takes the saved channels and connection names, at startup and after every save. Applies
    /// from the next alert. A channel turned off or removed stops straight away: what it had
    /// waiting is dropped, and it forgets the alerts it was told about. A store that couldn't be
    /// read (another program had settings.dat open) is ignored: it would look like "no channels".
    /// Only reads, so it's safe inside a save's Changed event.
    /// </summary>
    public void Apply(ConnectionStore store)
    {
        if (store.ReadFailed)
            return;

        _connectionNames = store.Connections.ToDictionary(c => c.Id, c => c.DisplayName);
        var channels = store.ChatChannels.Select(c => c.Clone()).ToList();
        _channels = channels;

        var on = channels.Where(c => c.Enabled).Select(c => c.Id).ToHashSet();
        foreach (var id in _queues.Keys.Where(id => !on.Contains(id)).ToList())
        {
            var dropped = _queues[id].Stop();
            _queues[id].Dispose();
            _queues.Remove(id);
            if (dropped > 0)
                _log($"Chat \"{Name(id)}\": {dropped} message(s) not sent, because the channel was turned off or removed");
        }
        foreach (var id in _routers.Keys.Where(id => !on.Contains(id)).ToList())
            _routers.Remove(id);

        var ids = channels.Select(c => c.Id).ToHashSet();
        foreach (var gone in _status.Keys.Where(id => !ids.Contains(id)).ToList())
            _status.TryRemove(gone, out _);
    }

    public ChannelStatus Status(Guid channelId) =>
        _status.TryGetValue(channelId, out var status) ? status : ChannelStatus.NeverUsed;

    /// <summary>
    /// A connection's alerts changed (<see cref="MonitoringManager.AlertsRaised"/>): queues what
    /// each channel that's on should be told. Never throws and never waits.
    /// </summary>
    public void OnAlertsRaised(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        var name = _connectionNames.TryGetValue(connectionId, out var n) ? n : "a deleted connection";
        foreach (var channel in _channels.Where(c => c.Enabled))
        {
            try
            {
                if (!_routers.TryGetValue(channel.Id, out var router))
                    _routers[channel.Id] = router = new ChannelRouter();

                foreach (var message in router.Route(channel, connectionId, name, changes, _pcName))
                    Queue(channel.Id).Enqueue(message);
            }
            catch (Exception ex)
            {
                // One channel's bug mustn't stop another, or reach monitoring. Logged by type only.
                _log($"Chat \"{channel.Name}\": couldn't handle an alert change ({ex.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// Posts the test message to a saved channel, once, without retrying, and says how it went.
    /// Works while the channel is off, so it can be checked before it's turned on.
    /// </summary>
    public async Task<WebhookResult> SendTestAsync(Guid channelId, CancellationToken cancel = default)
    {
        var channel = _channels.FirstOrDefault(c => c.Id == channelId)
                      ?? throw new InvalidOperationException("That chat channel isn't saved any more.");
        var target = Target(channel);
        var message = ChatMessageBuilder.Test(_pcName, ConnectionsText(channel));

        WebhookResult result;
        if (target is null)
        {
            result = new WebhookResult(WebhookOutcome.Rejected, null, "the saved webhook URL isn't valid", TimeSpan.Zero);
        }
        else
        {
            try
            {
                result = await _sender.SendAsync(target.Url, ChatFormatter.Format(message, target.Service), target.Service, cancel);
            }
            catch (Exception ex)
            {
                // The sender doesn't throw for a failed post; this is a bug, logged by type only.
                result = new WebhookResult(WebhookOutcome.Unreachable, null, $"the request failed ({ex.GetType().Name})", TimeSpan.Zero);
            }
        }

        Log(channel.Name, channel.Service, message, result);
        if (result.IsSuccess)
            MarkPosted(channel.Id);
        return result;
    }

    /// <summary>
    /// Which connections a channel posts for, as the test message says it: "all monitored
    /// connections", "Sales-Prod and HR-DB".
    /// </summary>
    public string ConnectionsText(ChatChannel channel)
    {
        if (channel.AllConnections)
            return "all monitored connections";

        var names = channel.ConnectionIds
            .Select(id => _connectionNames.TryGetValue(id, out var n) ? n : null)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return names.Count == 0 ? "no connections (none are chosen)" : ChatMessageBuilder.List(names);
    }

    /// <summary>Completes once every queued message has been sent or dropped. For tests.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_queues.Values.Select(q => q.WhenIdleAsync()).ToList());

    private ChannelQueue Queue(Guid channelId)
    {
        if (!_queues.TryGetValue(channelId, out var queue))
            _queues[channelId] = queue = new ChannelQueue(() => Target(channelId), _sender, (target, message, result) =>
            {
                // On the queue's worker.
                Log(target.Name, target.Service, message, result);
                if (result.IsSuccess)
                    OnUi(() => MarkPosted(channelId));
            });
        return queue;
    }

    // Read by the queues' workers: the channel as saved now, or null when it's gone or off.
    private ChannelTarget? Target(Guid channelId) =>
        _channels.FirstOrDefault(c => c.Id == channelId) is { Enabled: true } channel ? Target(channel) : null;

    private static ChannelTarget? Target(ChatChannel channel) =>
        Uri.TryCreate(channel.WebhookUrl, UriKind.Absolute, out var url) ? new ChannelTarget(channel.Name, channel.Service, url) : null;

    private string Name(Guid channelId) => _channels.FirstOrDefault(c => c.Id == channelId)?.Name ?? "a removed channel";

    private void Log(string channel, ChatService service, ChatMessage message, WebhookResult result) =>
        _log($"Chat \"{channel}\" ({ChatServices.Name(service)}): {message.About} {result.LogText()}");

    private void OnUi(Action action)
    {
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private void MarkPosted(Guid channelId)
    {
        if (_channels.All(c => c.Id != channelId))
            return;
        _status[channelId] = new ChannelStatus(DateTime.UtcNow);
        StatusChanged?.Invoke(channelId);
    }

    public void Dispose()
    {
        // Exiting: what's still waiting isn't sent (#123 gives it a few seconds first).
        foreach (var (id, queue) in _queues)
        {
            var dropped = queue.Stop();
            queue.Dispose();
            if (dropped > 0)
                _log($"Chat \"{Name(id)}\": {dropped} message(s) not sent, because SqlVitals exited");
        }
        _queues.Clear();
        (_sender as IDisposable)?.Dispose();
    }
}
