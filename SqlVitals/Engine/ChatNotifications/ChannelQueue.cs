using System.Diagnostics.CodeAnalysis;

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

/// <summary>How one try at sending a message went, and what happens next (#122).</summary>
/// <param name="Attempt">1 for the first try, 2 for the first retry, and so on.</param>
/// <param name="Next">When the message will be tried again; null when it won't: it was sent, or it was given up.</param>
public sealed record ChannelReport(ChannelTarget Target, ChatMessage Message, WebhookResult Result, int Attempt, RetryDecision? Next)
{
    /// <summary>The message was given up after its retries (or straight away, for a 404): the channel is failing.</summary>
    public bool GaveUp => !Result.IsSuccess && Next is null && Result.Outcome != WebhookOutcome.Cancelled;

    /// <summary>
    /// For the app log, after the channel's name: "alert on Sales-Prod server error (503); retry 1
    /// of 3 in 2 s", "alert on Sales-Prod posted (200) in 120 ms, on try 2". Never the URL.
    /// </summary>
    public string LogText()
    {
        var text = $"{Message.About} {Result.LogText()}";
        if (Result.IsSuccess)
            return Attempt > 1 ? $"{text}, on try {Attempt} (a retry)" : text;
        if (Next is { } next)
            return $"{text}; {next.LogText()}";
        if (Result.Outcome == WebhookOutcome.Cancelled)
            return text;
        return Attempt > 1 ? $"{text}; not sent after {Attempt} tries" : $"{text}; not sent";
    }
}

/// <summary>
/// One chat channel's messages, sent on a worker of its own (#121, #122):
/// <list type="bullet">
/// <item>one at a time, in the order they were queued, and at most one a second (Slack's limit),
/// measured start to start;</item>
/// <item>retried as <see cref="RetryPolicy"/> says, the message holding its place meanwhile, so a
/// later one never overtakes it;</item>
/// <item>when more than <see cref="MaxWaiting"/> are waiting, the rest are folded into one summary
/// message (<see cref="ChatMessageBuilder.Summary"/>), built when it's sent; a stopped monitoring
/// message never is (#123);</item>
/// <item>at Exit, <see cref="Hurry"/>: a message waiting to retry is tried once straight away,
/// and nothing is retried any more, so what's waiting fits in the few seconds left (#123).</item>
/// </list>
/// <see cref="Enqueue"/> never waits, so neither the UI nor monitoring does, and a channel waiting
/// to retry never holds up another. Waits run on a <see cref="TimeProvider"/>, so tests don't wait
/// in real time, and are measured on its timestamps, which the PC's clock changing doesn't move.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "It is a queue: one channel's messages, sent in order")]
public sealed class ChannelQueue : IDisposable
{
    /// <summary>Individual messages that may wait; the next ones are folded into a summary.</summary>
    public const int MaxWaiting = 20;

    /// <summary>The least time between the starts of two sends.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    private readonly Func<ChannelTarget?>  _target;
    private readonly IWebhookSender        _sender;
    private readonly Action<ChannelReport> _report;
    private readonly TimeProvider          _time;
    private readonly NetworkGrace?         _grace;

    private readonly CancellationTokenSource _stop   = new();
    // Taken once: the source's Token throws after Dispose, and a send can outlive Dispose's wait.
    private readonly CancellationToken       _cancel;
    // Hurry cancels it, to cut a wait to retry short. Its token is taken once, like _cancel.
    private readonly CancellationTokenSource _hurry  = new();
    private readonly CancellationToken       _hurried;
    private volatile bool                    _draining;
    private readonly SemaphoreSlim           _signal = new(0);
    private readonly Task                    _worker;

    // What's waiting, in order: messages, and at most one summary, at the tail while it's
    // still taking messages. Under _gate, with the counts.
    private readonly object      _gate  = new();
    private readonly Queue<Item> _items = new();
    private Item? _summary;
    private int   _individual;   // individual messages waiting or being sent
    private int   _pending;      // every message waiting or being sent, the folded ones included
    private bool  _stopped;
    private TaskCompletionSource _idle = Completed();

    // When the last send started, as a _time timestamp. Only the worker uses it.
    private long? _lastStart;

    private sealed class Item(ChatMessage? message, List<ChatMessage>? folded)
    {
        public ChatMessage?       Message { get; } = message;
        public List<ChatMessage>? Folded  { get; } = folded;
        public int Count => Folded?.Count ?? 1;
    }

    /// <param name="target">Where to send, or null when the channel is gone or off: the message is dropped.</param>
    /// <param name="report">
    /// Called on the worker after each try with how it went. It mustn't throw; if it does, the
    /// queue carries on.
    /// </param>
    /// <param name="time">The clock for pacing and retries; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="grace">Tells when the PC has just woken up (F18); none when null.</param>
    public ChannelQueue(Func<ChannelTarget?> target, IWebhookSender sender, Action<ChannelReport> report,
                        TimeProvider? time = null, NetworkGrace? grace = null)
    {
        _target = target;
        _sender = sender;
        _report = report;
        _time   = time ?? TimeProvider.System;
        _grace  = grace;
        _cancel  = _stop.Token;
        _hurried = _hurry.Token;
        // Off the caller's thread and its synchronization context, so nothing here waits on the UI.
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Queues a message. Never waits and never throws; after <see cref="Stop"/> the message is dropped.</summary>
    public void Enqueue(ChatMessage message)
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            if (_pending++ == 0)
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!message.CanFold)
            {
                // Stopped monitoring (#123): never a count in a summary. It goes after the summary,
                // and what comes after it starts a new one.
                _individual++;
                _items.Enqueue(new Item(message, null));
                _summary = null;
            }
            else if (_summary is { } summary)
            {
                summary.Folded!.Add(message);
                return;
            }
            else if (_individual >= MaxWaiting)
            {
                _summary = new Item(null, [message]);
                _items.Enqueue(_summary);
            }
            else
            {
                _individual++;
                _items.Enqueue(new Item(message, null));
            }
        }
        _signal.Release();
    }

    /// <summary>How many messages are waiting or being sent, those folded into a summary included.</summary>
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
            while (true)
            {
                await _signal.WaitAsync(_cancel).ConfigureAwait(false);

                Item? item;
                lock (_gate)
                {
                    if (!_items.TryDequeue(out item))
                        continue;
                    // Being sent now: what comes next starts a new summary, if it has to.
                    if (ReferenceEquals(item, _summary))
                        _summary = null;
                }

                try
                {
                    await DeliverAsync(item.Message ?? ChatMessageBuilder.Summary(item.Folded!)).ConfigureAwait(false);
                }
                catch (Exception) when (!_cancel.IsCancellationRequested)
                {
                    // A bug building the message: drop it rather than stop the channel for good.
                }
                finally
                {
                    Done(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: the channel was removed or turned off, or SqlVitals is exiting.
        }
    }

    // Sends one message, trying again as the policy says. Throws OperationCanceledException when stopped.
    private async Task DeliverAsync(ChatMessage message)
    {
        int retries = 0, rateLimited = 0, attempt = 0;
        while (true)
        {
            await PaceAsync().ConfigureAwait(false);
            if (_cancel.IsCancellationRequested || _target() is not { } target)
                return;

            attempt++;
            _lastStart = _time.GetTimestamp();
            var result = await SendAsync(target, message).ConfigureAwait(false);
            // Stopped while it was in flight: whoever stopped the queue counts it as not sent.
            _cancel.ThrowIfCancellationRequested();

            // Exiting: no time left to wait for a retry.
            var next = result.IsSuccess || _draining ? null : RetryPolicy.Decide(result, retries, rateLimited, _grace?.IsSettling == true);
            try { _report(new ChannelReport(target, message, result, attempt, next)); }
            catch { /* reporting must never stop the channel */ }

            if (next is not { } retry)
                return;
            if (retry.Number > 0)
                retries = retry.Number;
            rateLimited = result.Outcome == WebhookOutcome.RateLimited ? rateLimited + 1 : 0;
            await WaitToRetryAsync(retry.Wait).ConfigureAwait(false);
        }
    }

    // Waits to retry, unless Hurry cuts it short: then the message is tried once straight away.
    private async Task WaitToRetryAsync(TimeSpan wait)
    {
        if (_draining)
            return;
        using var either = CancellationTokenSource.CreateLinkedTokenSource(_cancel, _hurried);
        try
        {
            await Task.Delay(wait, _time, either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
        {
            // Hurried.
        }
    }

    /// <summary>
    /// SqlVitals is exiting (#123): a message waiting to retry is tried once straight away, and
    /// nothing is retried after this, so what's waiting fits in the seconds left. The pacing stays
    /// (Slack's limit still applies). Safe from any thread.
    /// </summary>
    public void Hurry()
    {
        _draining = true;
        _hurry.Cancel();
    }

    private async Task PaceAsync()
    {
        if (_lastStart is not { } last)
            return;
        var wait = Pace - _time.GetElapsedTime(last);
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _time, _cancel).ConfigureAwait(false);
    }

    private async Task<WebhookResult> SendAsync(ChannelTarget target, ChatMessage message)
    {
        try
        {
            return await _sender.SendAsync(target.Url, ChatFormatter.Format(message, target.Service), target.Service, _cancel)
                                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Stopped (the exception is the cancellation): DeliverAsync sees that next.
            // The sender doesn't throw for a failed post; this is a bug. Its message may hold the URL.
            return new WebhookResult(WebhookOutcome.Unreachable, null, $"the request failed ({ex.GetType().Name})", TimeSpan.Zero);
        }
    }

    private void Done(Item item)
    {
        lock (_gate)
        {
            if (_stopped)
                return; // already counted as dropped
            if (item.Message is not null)
                _individual--;
            _pending -= item.Count;
            if (_pending == 0)
                _idle.TrySetResult();
        }
    }

    /// <summary>
    /// Stops the worker. Messages still waiting are dropped, and a request in flight or a wait to
    /// retry is cancelled. Returns how many messages were dropped, for the log.
    /// </summary>
    public int Stop()
    {
        int dropped;
        lock (_gate)
        {
            if (_stopped)
                return 0;
            _stopped    = true;
            dropped     = _pending;
            _pending    = 0;
            _individual = 0;
            _items.Clear();
            _summary = null;
            _idle.TrySetResult();
        }
        // Outside the lock: cancelling can run the worker's continuation on this thread.
        _stop.Cancel();
        return dropped;
    }

    public void Dispose()
    {
        Stop();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { /* cancelled */ }
        _stop.Dispose();
        // _hurry isn't disposed: a worker that outlived the wait may still link to its token.
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }
}
