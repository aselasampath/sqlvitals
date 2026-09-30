using System.Collections.Concurrent;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>One channel's messages, in order, on a worker of its own (#121).</summary>
public sealed class ChannelQueueTests
{
    private static readonly ChannelTarget Target = new("DBA team", ChatService.Slack, new Uri(WebhookUrlTests.Slack));

    private static readonly WebhookResult Ok   = new(WebhookOutcome.Posted, 200, null, TimeSpan.Zero);
    private static readonly WebhookResult Fail = new(WebhookOutcome.ServerError, 503, null, TimeSpan.Zero);

    private sealed class FakeSender(Func<string, Task<WebhookResult>> answer) : IWebhookSender
    {
        public readonly ConcurrentQueue<string> Sent = new();

        public async Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            var title = (string)System.Text.Json.Nodes.JsonNode.Parse(json)!["blocks"]![0]!["text"]!["text"]!;
            Sent.Enqueue(title);
            return await answer(title);
        }
    }

    private static ChatMessage Message(string title) => new(ChatTone.Neutral, title, "", [], null);

    private readonly ConcurrentQueue<(string Title, WebhookOutcome Outcome)> _reported = new();

    private ChannelQueue Create(FakeSender sender, Func<ChannelTarget?>? target = null) =>
        new(target ?? (() => Target), sender, (_, m, r) => _reported.Enqueue((m.Title, r.Outcome)));

    [Fact]
    public async Task Messages_AreSentOneAtATime_InTheOrderQueued()
    {
        var inFlight = 0;
        var overlap  = false;
        var sender = new FakeSender(async _ =>
        {
            overlap |= Interlocked.Increment(ref inFlight) > 1;
            await Task.Delay(5);
            Interlocked.Decrement(ref inFlight);
            return Ok;
        });
        using var queue = Create(sender);

        foreach (var i in Enumerable.Range(1, 20))
            queue.Enqueue(Message($"m{i}"));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"m{i}"), sender.Sent);
        Assert.False(overlap);
        Assert.All(_reported, r => Assert.Equal(WebhookOutcome.Posted, r.Outcome));
    }

    [Fact]
    public async Task Enqueue_NeverWaitsForTheSend()
    {
        var gate = new TaskCompletionSource<WebhookResult>();
        using var queue = Create(new FakeSender(_ => gate.Task));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        queue.Enqueue(Message("slow"));
        queue.Enqueue(Message("next"));

        Assert.True(watch.ElapsedMilliseconds < 500);
        Assert.Equal(2, queue.Pending);
        gate.SetResult(Ok);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AFailedMessage_IsReportedAndDropped_AndTheNextOneStillGoes()
    {
        using var queue = Create(new FakeSender(title => Task.FromResult(title == "bad" ? Fail : Ok)));

        queue.Enqueue(Message("bad"));
        queue.Enqueue(Message("good"));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([("bad", WebhookOutcome.ServerError), ("good", WebhookOutcome.Posted)], _reported);
    }

    [Fact]
    public async Task ASenderThatThrows_IsReportedAsAFailure_AndTheQueueCarriesOn()
    {
        using var queue = Create(new FakeSender(title =>
            title == "bug" ? throw new InvalidOperationException(WebhookUrlTests.Slack) : Task.FromResult(Ok)));

        queue.Enqueue(Message("bug"));
        queue.Enqueue(Message("fine"));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([("bug", WebhookOutcome.Unreachable), ("fine", WebhookOutcome.Posted)], _reported);
    }

    [Fact]
    public async Task EachMessage_GoesWhereTheChannelPointsWhenItsSent()
    {
        var target = Target;
        var urls = new ConcurrentQueue<Uri>();
        var sender = new UrlSender(urls);
        using var queue = new ChannelQueue(() => target, sender, (_, _, _) => { });

        queue.Enqueue(Message("one"));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        target = new ChannelTarget("DBA team", ChatService.Teams, new Uri(WebhookUrlTests.Teams));
        queue.Enqueue(Message("two"));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([new Uri(WebhookUrlTests.Slack), new Uri(WebhookUrlTests.Teams)], urls);
        Assert.Equal([ChatService.Slack, ChatService.Teams], sender.Services);
    }

    [Fact]
    public async Task AChannelThatsGoneOrOff_DropsItsMessages()
    {
        var sender = new FakeSender(_ => Task.FromResult(Ok));
        using var queue = Create(sender, () => null);

        queue.Enqueue(Message("nowhere"));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Stop_DropsWhatsWaiting_AndSaysHowMany()
    {
        var gate = new TaskCompletionSource<WebhookResult>();
        var sender = new FakeSender(_ => gate.Task);
        var queue = Create(sender);
        queue.Enqueue(Message("in flight"));
        queue.Enqueue(Message("waiting 1"));
        queue.Enqueue(Message("waiting 2"));

        Assert.Equal(3, queue.Stop());
        queue.Enqueue(Message("after"));
        gate.SetResult(Ok);
        queue.Dispose();

        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("after", sender.Sent);
        Assert.DoesNotContain("waiting 2", sender.Sent);
    }

    [Fact]
    public void ATarget_NeverShowsItsUrl()
    {
        Assert.DoesNotContain("services/T0001", Target.ToString());
    }

    private sealed class UrlSender(ConcurrentQueue<Uri> urls) : IWebhookSender
    {
        public readonly List<ChatService> Services = new();

        public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            urls.Enqueue(url);
            Services.Add(service);
            return Task.FromResult(Ok);
        }
    }
}
