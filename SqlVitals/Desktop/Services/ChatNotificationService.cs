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
/// #120: the channels and Send test message. #121: posting new and critical alerts. #122: pacing,
/// retries, and the Failing status with one Windows notification. #123: Back to normal, stopped
/// monitoring, and a few seconds at Exit to send what's waiting (<see cref="BeginExit"/>,
/// <see cref="Shutdown"/>).
///
/// Used on the UI thread; the queues report back to it through its synchronization context.
/// </summary>
public sealed class ChatNotificationService : IDisposable
{
    private readonly IWebhookSender          _sender;
    private readonly string                  _pcName;
    private readonly Action<string>          _log;
    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider            _time;
    private readonly NetworkGrace            _grace;

    /// <summary>
    /// How long, from the start of Exit, the channels have to send what's waiting (#123). It
    /// runs alongside the history's own wait, so exit isn't this much slower.
    /// </summary>
    public static readonly TimeSpan ExitTime = TimeSpan.FromSeconds(5);

    // Set by BeginExit; read by the queues' workers.
    private volatile bool _exiting;
    private bool _stopsQueued;

    // Replaced as a whole by Apply, so the queues' workers can read it at any time.
    private volatile IReadOnlyList<ChatChannel> _channels = [];
    private IReadOnlyDictionary<Guid, string> _connectionNames = new Dictionary<Guid, string>();

    private readonly Dictionary<Guid, ChannelRouter> _routers = new();
    private readonly Dictionary<Guid, ChannelQueue>  _queues  = new();
    // Written by MarkPosted, on the UI thread (or a queue's worker when there's no UI, in tests).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ChannelStatus> _status = new();

    /// <param name="log">Where posts and failures are written: the app log, or a list in tests. Never given a URL.</param>
    /// <param name="ui">The UI thread's context, where status changes are raised; null raises them on the queue's worker (tests).</param>
    /// <param name="time">The clock for pacing, retries and the status times; the system clock when null.</param>
    public ChatNotificationService(IWebhookSender sender, string? pcName = null, Action<string>? log = null,
                                   SynchronizationContext? ui = null, TimeProvider? time = null)
    {
        _sender = sender;
        _pcName = pcName ?? Environment.MachineName;
        _log    = log ?? AppLog.Info;
        _ui     = ui;
        _time   = time ?? TimeProvider.System;
        _grace  = new NetworkGrace(_time);
    }

    /// <summary>A channel's status changed, e.g. a message reached it. Raised on the UI thread.</summary>
    public event Action<Guid>? StatusChanged;

    /// <summary>
    /// A channel started failing: a message was given up after its retries, and none has got
    /// through since. Raised once until the channel posts again, with the Windows notification's
    /// text. Raised on the UI thread.
    /// </summary>
    public event Action<Guid, string>? StartedFailing;

    /// <summary>
    /// The PC woke from sleep: for a minute, a webhook that can't be reached doesn't count toward
    /// giving up, while the network reconnects (F18). Safe from any thread.
    /// </summary>
    public void OnResumed() => _grace.Resumed();

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
        // A channel turned off isn't expected to receive anything, so it isn't "failing"; turned
        // back on, it notifies again if it still fails.
        foreach (var id in _status.Keys.Where(id => !on.Contains(id)).ToList())
            if (_status[id].IsFailing)
                _status[id] = new ChannelStatus(_status[id].LastPostedUtc);

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
        // Null for a removed connection: its name is forgotten before its session ends its
        // alerts, so the routers use the name they were told with (F8).
        var name = _connectionNames.TryGetValue(connectionId, out var n) ? n : null;
        foreach (var channel in _channels.Where(c => c.Enabled))
        {
            try
            {
                foreach (var message in Router(channel.Id).Route(channel, connectionId, name, changes, _pcName))
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
    /// Works while the channel is off, so it can be checked before it's turned on. Success clears
    /// Failing; a failure leaves the status alone, because the result is shown by the button.
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
            _queues[channelId] = queue = new ChannelQueue(() => Target(channelId), _sender, report =>
            {
                // On the queue's worker.
                _log($"Chat \"{report.Target.Name}\" ({ChatServices.Name(report.Target.Service)}): {report.LogText()}");
                if (report.Result.IsSuccess)
                    OnUi(() => MarkPosted(channelId));
                else if (report.GaveUp)
                    OnUi(() => MarkFailed(channelId, report.Target, report.Result));
            }, _time, _grace);
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

    private ChannelRouter Router(Guid channelId)
    {
        if (!_routers.TryGetValue(channelId, out var router))
            _routers[channelId] = router = new ChannelRouter { CollectingStops = _exiting };
        return router;
    }

    private void OnUi(Action action)
    {
        // Exiting: the window is closing, and the UI thread is waiting in Shutdown.
        if (_exiting)
            return;
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private void MarkPosted(Guid channelId)
    {
        if (_exiting || _channels.All(c => c.Id != channelId))
            return;
        _status[channelId] = Status(channelId).Posted(_time.GetUtcNow().UtcDateTime);
        StatusChanged?.Invoke(channelId);
    }

    // A message was given up. Notifies only on the change into Failing; a channel that keeps
    // failing says so in its status, not in more notifications. New messages are still tried, so
    // a channel that comes back recovers by itself (MarkPosted).
    private void MarkFailed(Guid channelId, ChannelTarget target, WebhookResult result)
    {
        if (_exiting || _channels.FirstOrDefault(c => c.Id == channelId) is not { Enabled: true })
            return;
        var before = Status(channelId);
        _status[channelId] = before.Failed(_time.GetUtcNow().UtcDateTime, result.LogText());
        StatusChanged?.Invoke(channelId);
        if (!before.IsFailing)
            StartedFailing?.Invoke(channelId, ChannelStatus.FailingNotice(target.Name, target.Service, result));
    }

    /// <summary>
    /// SqlVitals is exiting for real (#123). Call before <see cref="MonitoringManager.Dispose"/>
    /// ends the active alerts: their stopped monitoring ends then collect into one message per
    /// channel (F5), and nothing more reaches the status or the tray, which are closing.
    /// </summary>
    public void BeginExit()
    {
        _exiting = true;
        foreach (var router in _routers.Values)
            router.CollectingStops = true;
    }

    /// <summary>
    /// Every connection has stopped (<see cref="MonitoringManager.AllStopped"/>, at Exit): queues
    /// each channel's one stopped monitoring message, and hurries every queue: a message waiting
    /// to retry is tried once straight away, and nothing is retried any more. Runs once; after
    /// <see cref="BeginExit"/> only.
    /// </summary>
    public void OnAllStopped()
    {
        if (!_exiting || _stopsQueued)
            return;
        _stopsQueued = true;

        foreach (var channel in _channels.Where(c => c.Enabled))
        {
            try
            {
                if (_routers.TryGetValue(channel.Id, out var router) && router.TakeStops(_pcName) is { } stopped)
                    Queue(channel.Id).Enqueue(stopped);
            }
            catch (Exception ex)
            {
                _log($"Chat \"{channel.Name}\": couldn't write the stopped monitoring message ({ex.GetType().Name})");
            }
        }

        foreach (var queue in _queues.Values)
            queue.Hurry();
    }

    /// <summary>
    /// Gives what's waiting until <paramref name="timeLeft"/> to send (after
    /// <see cref="OnAllStopped"/>, which it runs if it hasn't). Blocks the calling thread, which
    /// the queues never need. Then call <see cref="Dispose"/>, which drops and logs what's left.
    /// </summary>
    public void Shutdown(TimeSpan timeLeft)
    {
        BeginExit();
        OnAllStopped();

        var queues = _queues.Values.ToList();
        if (timeLeft <= TimeSpan.Zero || queues.Count == 0)
            return;
        try { Task.WhenAll(queues.Select(q => q.WhenIdleAsync())).Wait(timeLeft); }
        catch { /* whatever is left is dropped and logged by Dispose */ }
    }

    public void Dispose()
    {
        // Exiting: what's still waiting after Shutdown's few seconds isn't sent. Every queue is
        // stopped first, cancelling their requests together, so each Dispose's wait is short and
        // several channels stuck on a request don't add a second each to the exit.
        var dropped = _queues.ToDictionary(q => q.Key, q => q.Value.Stop());
        foreach (var (id, queue) in _queues)
        {
            queue.Dispose();
            if (dropped[id] > 0)
                _log($"Chat \"{Name(id)}\": {dropped[id]} message(s) not sent, because SqlVitals exited");
        }
        _queues.Clear();
        (_sender as IDisposable)?.Dispose();
    }
}
