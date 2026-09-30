using System.Threading.Channels;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// Where a channel's messages go, read afresh for each message, so an edit to the channel (its
/// URL, or Slack to Teams) applies to messages still waiting. A class, not a record: a record's
/// generated <see cref="object.ToString"/> would print the URL.
/// </summary>
public sealed class ChannelTarget(string name, ChatService service, Uri url)
{
    public string      Name    { get; } = name;
    public ChatService Service { get; } = service;
    public Uri         Url     { get; } = url;

    public override string ToString() => $"{Name} ({ChatServices.Name(Service)}, {WebhookUrl.Mask(Url.OriginalString)})";
}

/// <summary>
/// One chat channel's messages, sent one at a time and in the order they were queued (#121), on a
/// worker of its own. <see cref="Enqueue"/> never waits, so neither the UI nor monitoring does,
/// and a slow or failing channel never holds up another.
///
/// #121: a failed message is reported and dropped. Pacing, retries and the Failing status come
/// in #122.
/// </summary>
public sealed class ChannelQueue : IDisposable
{
    private readonly Func<ChannelTarget?> _target;
    private readonly IWebhookSender       _sender;
    private readonly Action<ChannelTarget, ChatMessage, WebhookResult> _sent;

    private readonly Channel<ChatMessage>    _queue = Channel.CreateUnbounded<ChatMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop  = new();
    private readonly Task                    _worker;

    // Messages queued and not yet sent or dropped, and a task that completes when that's none.
    private readonly object _gate = new();
    private int _pending;
    private TaskCompletionSource _idle = Completed();

    /// <param name="target">Where to send, or null when the channel is gone or off: the message is dropped.</param>
    /// <param name="sent">
    /// Called on the worker after each message with how it went. It mustn't throw; if it does, the
    /// queue carries on.
    /// </param>
    public ChannelQueue(Func<ChannelTarget?> target, IWebhookSender sender, Action<ChannelTarget, ChatMessage, WebhookResult> sent)
    {
        _target = target;
        _sender = sender;
        _sent   = sent;
        // Off the caller's thread and its synchronization context, so nothing here waits on the UI.
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Queues a message. Never waits and never throws; after <see cref="Dispose"/> the message is dropped.</summary>
    public void Enqueue(ChatMessage message)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
                return;
            if (_pending++ == 0)
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _queue.Writer.TryWrite(message);
    }

    /// <summary>How many messages are waiting or being sent.</summary>
    public int Pending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>Completes once every message queued so far has been sent or dropped.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _idle.Task;
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var message))
                {
                    await SendAsync(message).ConfigureAwait(false);
                    Done();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: the channel was removed or turned off, or SqlVitals is exiting.
        }
    }

    private async Task SendAsync(ChatMessage message)
    {
        if (_stop.IsCancellationRequested || _target() is not { } target)
            return;

        WebhookResult result;
        try
        {
            result = await _sender.SendAsync(target.Url, ChatFormatter.Format(message, target.Service), target.Service, _stop.Token)
                                  .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The sender doesn't throw for a failed post; this is a bug. Its message may hold the URL.
            result = new WebhookResult(WebhookOutcome.Unreachable, null, $"the request failed ({ex.GetType().Name})", TimeSpan.Zero);
        }

        try { _sent(target, message, result); }
        catch { /* reporting must never stop the channel */ }
    }

    private void Done()
    {
        lock (_gate)
        {
            if (_pending > 0 && --_pending == 0)
                _idle.TrySetResult();
        }
    }

    /// <summary>
    /// Stops the worker. Messages still waiting are dropped, and a request in flight is
    /// cancelled. Returns how many were dropped, for the log.
    /// </summary>
    public int Stop()
    {
        int dropped;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
                return 0;
            _stop.Cancel();
            dropped  = _pending;
            _pending = 0;
            _idle.TrySetResult();
        }
        _queue.Writer.TryComplete();
        return dropped;
    }

    public void Dispose()
    {
        Stop();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { /* cancelled */ }
        _stop.Dispose();
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }
}
