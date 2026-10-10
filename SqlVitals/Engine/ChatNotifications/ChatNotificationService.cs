using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// Posts to the Slack and Microsoft Teams channels saved in Settings (#108). Lives with the app,
/// not the window, and hears alerts from the app's <c>MonitoringManager.AlertsRaised</c>, so it
/// carries on while the window is hidden in the notification area. In Engine since #172, so the
/// collector service posts the same messages, from its own settings.
///
/// Each channel has a <see cref="ChannelRouter"/> (what to post), run where alerts are raised, and
/// a <see cref="ChannelQueue"/> (sending, in order, on a worker of its own). Deciding takes no
/// time and sending never blocks the caller, so monitoring and the UI never wait on a webhook.
///
/// #120: the channels and Send test message. #121: posting new and critical alerts. #122: pacing,
/// retries, and the Failing status with one Windows notification. #123: Back to normal, stopped
/// monitoring, and a few seconds at Exit to send what's waiting (<see cref="BeginExit"/>,
/// <see cref="Shutdown"/>). #170: "stopped watching" whenever servers stop being watched (Stop,
/// a setting, Exit, Windows ending, the PC going to sleep) and "watching again", with the gap;
/// and the heartbeat, a "still watching" message on each channel's schedule, which can also call
/// a dead man's switch URL (<see cref="CheckHeartbeats"/>).
///
/// Used on one thread at a time: the app's UI thread, or the collector service's own (#172). The
/// queues report back to it through that thread's synchronization context.
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
    private WatchStop _exitWhy = WatchStop.AppExit;

    // The connections being watched now, by name (#170), from MonitoringManager.WatchingChanged.
    private readonly Dictionary<Guid, string> _watching = new();

    // When the PC went to sleep, until it wakes (#170).
    private DateTime? _asleepSinceUtc;

    /// <summary>How often the heartbeats are checked for one that's due (#170).</summary>
    public static readonly TimeSpan HeartbeatCheckEvery = TimeSpan.FromSeconds(30);

    // Each channel with a heartbeat: its schedule as applied, and when the next one is due.
    private readonly Dictionary<Guid, (HeartbeatSchedule Schedule, DateTimeOffset Next)> _heartbeats = new();
    private ITimer? _heartbeatTimer;

    // Cancels the pings still running at Exit.
    private readonly CancellationTokenSource _pings = new();

    // Replaced as a whole by Apply, so the queues' workers can read it at any time.
    private volatile IReadOnlyList<ChatChannel> _channels = [];
    private IReadOnlyDictionary<Guid, string> _connectionNames = new Dictionary<Guid, string>();

    private readonly Dictionary<Guid, ChannelRouter> _routers = new();
    private readonly Dictionary<Guid, ChannelQueue>  _queues  = new();
    // Written by MarkPosted, on the UI thread (or a queue's worker when there's no UI, in tests).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ChannelStatus> _status = new();

    /// <param name="log">Where posts and failures are written: the app's log, the service's, or a list in tests; nowhere when null. Never given a URL.</param>
    /// <param name="ui">The UI thread's context, where status changes are raised; null raises them on the queue's worker (tests).</param>
    /// <param name="time">The clock for pacing, retries and the status times; the system clock when null.</param>
    public ChatNotificationService(IWebhookSender sender, string? pcName = null, Action<string>? log = null,
                                   SynchronizationContext? ui = null, TimeProvider? time = null)
    {
        _sender = sender;
        _pcName = pcName ?? Environment.MachineName;
        _log    = log ?? (_ => { });
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
    /// waiting is dropped, and it forgets the alerts it was told about. Only reads, so it's safe
    /// inside a save's Changed event.
    /// </summary>
    public void Apply(IReadOnlyList<ChatChannel> savedChannels, IReadOnlyDictionary<Guid, string> connectionNames)
    {
        _connectionNames = new Dictionary<Guid, string>(connectionNames);
        var channels = savedChannels.Select(c => c.Clone()).ToList();
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

        ScheduleHeartbeats(channels);
    }

    // A channel's first heartbeat is its next time from now, and again whenever its schedule
    // changes; a channel turned off or without a heartbeat is forgotten. The check timer runs
    // only while some channel has a heartbeat.
    private void ScheduleHeartbeats(IReadOnlyList<ChatChannel> channels)
    {
        var now  = _time.GetUtcNow();
        var with = channels.Where(c => c.Enabled && c.Heartbeat.IsOn).ToDictionary(c => c.Id, c => c.Heartbeat);

        foreach (var id in _heartbeats.Keys.Where(id => !with.ContainsKey(id)).ToList())
            _heartbeats.Remove(id);
        foreach (var (id, schedule) in with)
            if (!_heartbeats.TryGetValue(id, out var known) || known.Schedule != schedule)
                _heartbeats[id] = (schedule, schedule.Next(now, _time.LocalTimeZone)!.Value);

        if (_heartbeats.Count == 0)
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }
        else
        {
            _heartbeatTimer ??= _time.CreateTimer(_ => OnUi(CheckHeartbeats), null, HeartbeatCheckEvery, HeartbeatCheckEvery);
        }
    }

    /// <summary>When a channel's next heartbeat is due; null when it has none. For Settings and tests.</summary>
    public DateTimeOffset? NextHeartbeat(Guid channelId) =>
        _heartbeats.TryGetValue(channelId, out var heartbeat) ? heartbeat.Next : null;

    /// <summary>
    /// Posts each heartbeat that's due (#170): which of the channel's servers SqlVitals is
    /// watching, or that it's watching none, and when the next one is due; and, while it's
    /// watching any, calls the channel's ping URL, when it has one. A heartbeat missed while the PC slept is sent once,
    /// late, when it wakes, then the schedule carries on. Run by a timer on the UI thread.
    /// </summary>
    internal void CheckHeartbeats()
    {
        if (_exiting)
            return;
        var now = _time.GetUtcNow();
        foreach (var (id, (schedule, next)) in _heartbeats.ToList())
        {
            if (now < next)
                continue;
            var following = schedule.Next(now, _time.LocalTimeZone)!.Value;
            _heartbeats[id] = (schedule, following);

            if (_channels.FirstOrDefault(c => c.Id == id) is not { Enabled: true } channel)
                continue;
            try
            {
                var servers = _watching.Where(w => channel.Includes(w.Key)).Select(w => w.Value).ToList();
                Queue(id).Enqueue(ChatMessageBuilder.Heartbeat(servers, _pcName, following.UtcDateTime, schedule));
                // Running but watching nothing isn't "fine": the dead man's switch should go off.
                if (servers.Count > 0)
                    Ping(channel);
            }
            catch (Exception ex)
            {
                _log($"Chat \"{channel.Name}\": couldn't send the heartbeat ({ex.GetType().Name})");
            }
        }
    }

    // Calls the dead man's switch on the thread pool, once: a missed ping is what it's there to
    // notice. Logged without the URL.
    private void Ping(ChatChannel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.HeartbeatPingUrl) ||
            !Uri.TryCreate(channel.HeartbeatPingUrl.Trim(), UriKind.Absolute, out var url))
            return;
        var name = channel.Name;
        var cancel = _pings.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _sender.PingAsync(url, cancel).ConfigureAwait(false);
                _log(result.IsSuccess
                    ? $"Chat \"{name}\": heartbeat ping answered ({result.StatusCode})"
                    : $"Chat \"{name}\": heartbeat ping failed: {result.LogText()}");
            }
            catch (Exception ex)
            {
                _log($"Chat \"{name}\": heartbeat ping failed ({ex.GetType().Name})");
            }
        }, CancellationToken.None);
    }

    public ChannelStatus Status(Guid channelId) =>
        _status.TryGetValue(channelId, out var status) ? status : ChannelStatus.NeverUsed;

    /// <summary>
    /// A connection's alerts changed (<c>MonitoringManager.AlertsRaised</c>): queues what
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

    /// <summary>Completes once every queued message has been sent or dropped: before the PC sleeps (#170), and in tests.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_queues.Values.Select(q => q.WhenIdleAsync()).ToList());

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
            _routers[channelId] = router = new ChannelRouter();
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
    /// SqlVitals started or stopped watching connections (<c>MonitoringManager.WatchingChanged</c>,
    /// #170): queues each channel's one stopped watching message for the connections it takes,
    /// with the alerts it was told about that were still active, and one watching again message
    /// with how long each wasn't watched. A connection restarted with new details posts nothing:
    /// its alerts are raised again by its new tracker if they're still happening.
    /// </summary>
    public void OnWatchingChanged(WatchChange change)
    {
        foreach (var id in change.Restarted)
            foreach (var router in _routers.Values)
                router.TakeStops(id);

        foreach (var stop in change.Stopped)
            _watching.Remove(stop.Id);
        foreach (var start in change.Started)
            _watching[start.Id] = start.Name;

        PostStopped(change.Stopped, _time.GetUtcNow().UtcDateTime);
        PostWatching(change.Started);
    }

    /// <summary>
    /// The PC is going to sleep (#170): each channel is told SqlVitals stopped watching, since
    /// nothing is collected while it sleeps. Call on the UI thread, then give the queues a moment
    /// (<see cref="WhenIdleAsync"/>) before Windows suspends. What isn't sent by then goes when
    /// the PC wakes, with the time it stopped.
    /// </summary>
    public void OnGoingToSleep()
    {
        if (_exiting || _asleepSinceUtc is not null)
            return;
        var now = _time.GetUtcNow().UtcDateTime;
        _asleepSinceUtc = now;
        PostStopped(_watching.Select(w => new StoppedWatching(w.Key, w.Value, WatchStop.Sleep)).ToList(), now);
    }

    /// <summary>
    /// The PC woke up (#170): each channel is told SqlVitals is watching again, after how long.
    /// Nothing when SqlVitals didn't hear the PC go to sleep. Call on the UI thread.
    /// </summary>
    public void OnWokeUp()
    {
        if (_exiting || _asleepSinceUtc is not { } since)
            return;
        _asleepSinceUtc = null;
        PostWatching(_watching.Select(w => new WatchedConnection(w.Key, w.Value, since)).ToList());
    }

    // One stopped watching message per channel that takes any of these connections, or was told
    // about one of their alerts (ends follow starts, even after the channel's filters changed).
    private void PostStopped(IReadOnlyList<StoppedWatching> stopped, DateTime nowUtc)
    {
        if (stopped.Count == 0)
            return;
        foreach (var channel in _channels.Where(c => c.Enabled))
        {
            try
            {
                var router  = Router(channel.Id);
                var servers = stopped
                    .Select(s => new StoppedServer(s, router.TakeStops(s.Id)))
                    .Where(s => channel.Includes(s.Stop.Id) || s.ActiveAlerts.Count > 0)
                    .ToList();
                if (servers.Count > 0)
                    Queue(channel.Id).Enqueue(ChatMessageBuilder.StoppedWatching(servers, _pcName, nowUtc));
            }
            catch (Exception ex)
            {
                _log($"Chat \"{channel.Name}\": couldn't write the stopped watching message ({ex.GetType().Name})");
            }
        }
    }

    // One watching again message per channel that takes any of these connections.
    private void PostWatching(IReadOnlyList<WatchedConnection> started)
    {
        if (started.Count == 0)
            return;
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var channel in _channels.Where(c => c.Enabled))
        {
            try
            {
                var servers = started.Where(s => channel.Includes(s.Id)).ToList();
                if (servers.Count > 0)
                    Queue(channel.Id).Enqueue(ChatMessageBuilder.WatchingAgain(servers, _pcName, now));
            }
            catch (Exception ex)
            {
                _log($"Chat \"{channel.Name}\": couldn't write the watching again message ({ex.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// SqlVitals is exiting for real (#123): Exit, or Windows shutting down or signing out (#170).
    /// Call before <c>MonitoringManager.Dispose</c> ends the active alerts; from then on,
    /// nothing more reaches the status or the tray, which are closing.
    /// </summary>
    public void BeginExit(WatchStop why = WatchStop.AppExit)
    {
        if (!_exiting)
            _exitWhy = why;
        _exiting = true;
    }

    /// <summary>
    /// Every connection has stopped (<c>MonitoringManager.AllStopped</c>, at Exit): queues
    /// each channel's one stopped watching message, saying why, with the alerts it was told about
    /// that were still active (F5, #170). Then hurries every queue: a message waiting to retry is
    /// tried once straight away, and nothing is retried any more. Runs once; after
    /// <see cref="BeginExit"/> only.
    /// </summary>
    public void OnAllStopped()
    {
        if (!_exiting || _stopsQueued)
            return;
        _stopsQueued = true;

        // Asleep, the channels were told already; a message about the exit would say it twice.
        if (_asleepSinceUtc is null)
            PostStopped(_watching.Select(w => new StoppedWatching(w.Key, w.Value, _exitWhy)).ToList(),
                        _time.GetUtcNow().UtcDateTime);
        _watching.Clear();

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

    private bool _disposed;

    public void Dispose()
    {
        // MainWindow disposes it once; a test may too, after Shutdown.
        if (_disposed)
            return;
        _disposed = true;
        _heartbeatTimer?.Dispose();
        _pings.Cancel();
        _pings.Dispose();

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
