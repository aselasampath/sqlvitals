using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>
/// One channel's messages on a worker of its own: in order (#121), paced, retried and capped
/// (#122). On a fake clock, so nothing waits in real time.
/// </summary>
public sealed class ChannelQueueTests
{
    private static readonly ChannelTarget Target = new("DBA team", ChatService.Slack, new Uri(WebhookUrlTests.Slack));

    private static readonly WebhookResult Ok       = new(WebhookOutcome.Posted, 200, null, TimeSpan.Zero);
    private static readonly WebhookResult Busy     = new(WebhookOutcome.ServerError, 503, null, TimeSpan.Zero);
    private static readonly WebhookResult NotFound = new(WebhookOutcome.NotFound, 404, "no_service", TimeSpan.Zero);
    private static readonly WebhookResult TimedOut = new(WebhookOutcome.TimedOut, null, null, TimeSpan.Zero);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeSender(TimeProvider time, Func<string, Task<WebhookResult>> answer) : IWebhookSender
    {
        public FakeSender(TimeProvider time, Func<string, WebhookResult> answer) : this(time, t => Task.FromResult(answer(t))) { }

        public readonly ConcurrentQueue<string> Sent = new();
        public readonly ConcurrentQueue<(string Title, DateTimeOffset At)> Tries = new();

        public async Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            var title = (string)System.Text.Json.Nodes.JsonNode.Parse(json)!["blocks"]![0]!["text"]!["text"]!;
            Sent.Enqueue(title);
            Tries.Enqueue((title, time.GetUtcNow()));
            return await answer(title);
        }

        /// <summary>When each try of a message started, in seconds after the clock's start.</summary>
        public double[] SecondsOf(string title) =>
            Tries.Where(t => t.Title == title).Select(t => (t.At - SteppingTime.Start).TotalSeconds).ToArray();
    }

    private static ChatMessage Message(string title) => new(ChatTone.Neutral, title, "", [], null) { About = title };

    private readonly SteppingTime _time = new();
    private readonly ConcurrentQueue<ChannelReport> _reports = new();

    private ChannelQueue Create(IWebhookSender sender, Func<ChannelTarget?>? target = null, TimeProvider? time = null, NetworkGrace? grace = null) =>
        new(target ?? (() => Target), sender, _reports.Enqueue, time ?? _time, grace);

    private FakeSender Answer(Func<string, WebhookResult> answer) => new(_time, answer);

    // ── Order and pacing ──────────────────────────────────────────────────────

    [Fact]
    public async Task Messages_AreSentOneAtATime_InTheOrderQueued()
    {
        var inFlight = 0;
        var overlap  = false;
        var sender = new FakeSender(_time, async _ =>
        {
            overlap |= Interlocked.Increment(ref inFlight) > 1;
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            return Ok;
        });
        using var queue = Create(sender);

        foreach (var i in Enumerable.Range(1, 20))
            queue.Enqueue(Message($"m{i}"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"m{i}"), sender.Sent);
        Assert.False(overlap);
        Assert.All(_reports, r => Assert.Equal(WebhookOutcome.Posted, r.Result.Outcome));
    }

    [Fact]
    public async Task Messages_AreSentAtMostOneASecond()
    {
        var sender = Answer(_ => Ok);
        using var queue = Create(sender);

        foreach (var i in Enumerable.Range(1, 4))
            queue.Enqueue(Message($"m{i}"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([0d, 1, 2, 3], sender.Tries.Select(t => (t.At - SteppingTime.Start).TotalSeconds));
    }

    [Fact]
    public async Task Pacing_IsMeasuredStartToStart_SoASlowAnswerDoesntAddAFullSecond()
    {
        var sender = new FakeSender(_time, _ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(900));
            return Task.FromResult(Ok);
        });
        using var queue = Create(sender);

        queue.Enqueue(Message("m1"));
        queue.Enqueue(Message("m2"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([0d, 1], sender.Tries.Select(t => (t.At - SteppingTime.Start).TotalSeconds));
    }

    [Fact]
    public async Task AMessageAfterAQuietSpell_IsSentStraightAway()
    {
        var sender = Answer(_ => Ok);
        using var queue = Create(sender);
        queue.Enqueue(Message("m1"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        _time.Advance(TimeSpan.FromSeconds(5));
        queue.Enqueue(Message("m2"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([5d], sender.SecondsOf("m2"));
    }

    [Fact]
    public async Task Enqueue_NeverWaitsForTheSend()
    {
        var gate = new TaskCompletionSource<WebhookResult>();
        using var queue = Create(new FakeSender(_time, _ => gate.Task));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        queue.Enqueue(Message("slow"));
        queue.Enqueue(Message("next"));

        Assert.True(watch.ElapsedMilliseconds < 500);
        Assert.Equal(2, queue.Pending);
        gate.SetResult(Ok);
        await queue.WhenIdleAsync().WaitAsync(Wait);
    }

    // ── Retries ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AServerError_IsRetriedThreeTimes_After2And10And30Seconds_ThenGivenUp()
    {
        var sender = Answer(title => title == "bad" ? Busy : Ok);
        using var queue = Create(sender);

        queue.Enqueue(Message("bad"));
        queue.Enqueue(Message("next"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([0d, 2, 12, 42], sender.SecondsOf("bad"));
        Assert.Equal([43d], sender.SecondsOf("next"));
        Assert.Equal(
            ["bad server error (503); retry 1 of 3 in 2 s",
             "bad server error (503); retry 2 of 3 in 10 s",
             "bad server error (503); retry 3 of 3 in 30 s",
             "bad server error (503); not sent after 4 tries",
             "next posted (200) in 0 ms"],
            _reports.Select(r => r.LogText()));
        Assert.Equal([false, false, false, true, false], _reports.Select(r => r.GaveUp));
    }

    [Fact]
    public async Task ATimeout_IsRetriedTheSameWay()
    {
        var sender = Answer(_ => TimedOut);
        using var queue = Create(sender);

        queue.Enqueue(Message("slow"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([0d, 2, 12, 42], sender.SecondsOf("slow"));
        Assert.True(_reports.Last().GaveUp);
    }

    [Fact]
    public async Task ARetryThatWorks_IsLoggedAsARetry_AndIsntAFailure()
    {
        var tries  = 0;
        var sender = Answer(_ => Interlocked.Increment(ref tries) == 1 ? Busy : Ok);
        using var queue = Create(sender);

        queue.Enqueue(Message("alert"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(["alert server error (503); retry 1 of 3 in 2 s", "alert posted (200) in 0 ms, on try 2 (a retry)"],
                     _reports.Select(r => r.LogText()));
        Assert.DoesNotContain(_reports, r => r.GaveUp);
    }

    [Fact]
    public async Task A404_IsNotRetried_AndTheNextMessageStillGoes()
    {
        var sender = Answer(title => title == "bad" ? NotFound : Ok);
        using var queue = Create(sender);

        queue.Enqueue(Message("bad"));
        queue.Enqueue(Message("good"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(["bad", "good"], sender.Sent);
        Assert.Equal(["bad webhook not found (404): no_service; not sent", "good posted (200) in 0 ms"],
                     _reports.Select(r => r.LogText()));
        Assert.True(_reports.First().GaveUp);
    }

    [Fact]
    public async Task RateLimited_WaitsForRetryAfter()
    {
        var tries  = 0;
        var sender = Answer(_ => Interlocked.Increment(ref tries) == 1
            ? new WebhookResult(WebhookOutcome.RateLimited, 429, null, TimeSpan.Zero, TimeSpan.FromSeconds(20))
            : Ok);
        using var queue = Create(sender);

        queue.Enqueue(Message("alert"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([0d, 20], sender.SecondsOf("alert"));
        Assert.Equal("alert rate limited (429); trying again in 20 s", _reports.First().LogText());
    }

    [Fact]
    public async Task RateLimited_WithoutRetryAfter_Waits30Seconds()
    {
        var tries  = 0;
        var sender = Answer(_ => Interlocked.Increment(ref tries) == 1 ? new WebhookResult(WebhookOutcome.RateLimited, 429, null, TimeSpan.Zero) : Ok);
        using var queue = Create(sender);

        queue.Enqueue(Message("alert"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([0d, 30], sender.SecondsOf("alert"));
    }

    [Fact]
    public async Task RateLimitedOverAndOver_IsGivenUp_SoTheChannelEndsUpFailing()
    {
        var sender = Answer(_ => new WebhookResult(WebhookOutcome.RateLimited, 429, null, TimeSpan.Zero));
        using var queue = Create(sender);

        queue.Enqueue(Message("alert"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(6, sender.SecondsOf("alert").Length);
        Assert.True(_reports.Last().GaveUp);
    }

    [Fact]
    public async Task ASenderThatThrows_CountsAsNoAnswer_AndTheQueueCarriesOn()
    {
        var sender = new FakeSender(_time, title =>
            title == "bug" ? throw new InvalidOperationException(WebhookUrlTests.Slack) : Task.FromResult(Ok));
        using var queue = Create(sender);

        queue.Enqueue(Message("bug"));
        queue.Enqueue(Message("fine"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(4, _reports.Count(r => r.Message.Title == "bug"));
        Assert.Equal(WebhookOutcome.Unreachable, _reports.First().Result.Outcome);
        Assert.Equal(WebhookOutcome.Posted, _reports.Last().Result.Outcome);
    }

    [Fact]
    public async Task AfterThePcWakes_NoAnswer_IsTriedForAMinuteWithoutUsingUpTheRetries()
    {
        var grace = new NetworkGrace(_time);
        grace.Resumed();
        var sender = Answer(_ => new WebhookResult(WebhookOutcome.Unreachable, null, "its address couldn't be found (DNS)", TimeSpan.Zero));
        using var queue = Create(sender, grace: grace);

        queue.Enqueue(Message("alert"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        // Every 10 s for the minute, then the usual 3 retries.
        Assert.Equal([0d, 10, 20, 30, 40, 50, 60, 62, 72, 102], sender.SecondsOf("alert"));
        Assert.True(_reports.Last().GaveUp);
    }

    [Fact]
    public async Task EachTry_GoesWhereTheChannelPointsThen_SoAFixedUrlIsUsedByTheRetry()
    {
        var target   = Target;
        var fixedUrl = new Uri(WebhookUrlTests.Slack + "Z");
        var urls     = new ConcurrentQueue<Uri>();
        var sender   = new UrlSender(urls, url => url == fixedUrl ? Ok : Busy);
        using var queue = new ChannelQueue(() => Volatile.Read(ref target), sender, report =>
        {
            _reports.Enqueue(report);
            // The user fixes the URL while the message waits to retry.
            Volatile.Write(ref target, new ChannelTarget("DBA team", ChatService.Slack, fixedUrl));
        }, _time);

        queue.Enqueue(Message("alert"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([new Uri(WebhookUrlTests.Slack), fixedUrl], urls);
        Assert.True(_reports.Last().Result.IsSuccess);
    }

    [Fact]
    public async Task EachMessage_IsWrittenForTheServiceTheChannelHasWhenItsSent()
    {
        var target = Target;
        var urls   = new ConcurrentQueue<Uri>();
        var sender = new UrlSender(urls);
        using var queue = new ChannelQueue(() => target, sender, _ => { }, _time);

        queue.Enqueue(Message("one"));
        await queue.WhenIdleAsync().WaitAsync(Wait);
        target = new ChannelTarget("DBA team", ChatService.Teams, new Uri(WebhookUrlTests.Teams));
        queue.Enqueue(Message("two"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal([new Uri(WebhookUrlTests.Slack), new Uri(WebhookUrlTests.Teams)], urls);
        Assert.Equal([ChatService.Slack, ChatService.Teams], sender.Services);
    }

    // ── One channel doesn't hold up another ───────────────────────────────────

    [Fact]
    public async Task AChannelWaitingToRetry_DoesntHoldUpAnother()
    {
        // A clock that doesn't move: the failing channel waits for its retry for as long as the test runs.
        var still = new FakeTimeProvider();
        var failing = new FakeSender(still, _ => Busy);
        var working = new FakeSender(still, _ => Ok);
        using var slow = Create(failing, time: still);
        using var fine = Create(working, time: still);

        slow.Enqueue(Message("stuck"));
        await WaitUntil(() => _reports.Any(r => r.Message.Title == "stuck"));
        fine.Enqueue(Message("gets through"));
        await fine.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(["gets through"], working.Sent);
        Assert.Equal(1, slow.Pending);
        Assert.Single(failing.Sent);
    }

    // ── The 20-message cap ────────────────────────────────────────────────────

    [Fact]
    public async Task MoreThan20Waiting_TheRestAreCombinedIntoOneSummary()
    {
        var gate   = new TaskCompletionSource<WebhookResult>();
        var sender = new FakeSender(_time, title => title == "m1" ? gate.Task : Task.FromResult(Ok));
        using var queue = Create(sender);

        foreach (var i in Enumerable.Range(1, 31))
            queue.Enqueue(Message($"m{i}"));
        Assert.Equal(31, queue.Pending);
        gate.SetResult(Ok);
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"m{i}").Append("📋 11 more messages combined"), sender.Sent);
        Assert.Equal("summary of 11 messages posted (200) in 0 ms", _reports.Last().LogText());
    }

    [Fact]
    public async Task OnceTheSummaryIsSent_MessagesGoOneByOneAgain()
    {
        var gate   = new TaskCompletionSource<WebhookResult>();
        var sender = new FakeSender(_time, title => title == "m1" ? gate.Task : Task.FromResult(Ok));
        using var queue = Create(sender);
        foreach (var i in Enumerable.Range(1, 22))
            queue.Enqueue(Message($"m{i}"));
        gate.SetResult(Ok);
        await queue.WhenIdleAsync().WaitAsync(Wait);

        queue.Enqueue(Message("later"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(["📋 2 more messages combined", "later"], sender.Sent.TakeLast(2));
    }

    // ── Stopping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AChannelThatsGoneOrOff_DropsItsMessages()
    {
        var sender = Answer(_ => Ok);
        using var queue = Create(sender, () => null);

        queue.Enqueue(Message("nowhere"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Stop_DropsWhatsWaiting_FoldedMessagesIncluded_AndSaysHowMany()
    {
        var gate   = new TaskCompletionSource<WebhookResult>();
        var sender = new FakeSender(_time, _ => gate.Task);
        var queue  = Create(sender);
        foreach (var i in Enumerable.Range(1, 25))
            queue.Enqueue(Message($"m{i}"));
        await WaitUntil(() => !sender.Sent.IsEmpty);

        Assert.Equal(25, queue.Stop());
        queue.Enqueue(Message("after"));
        gate.SetResult(Ok);
        queue.Dispose();

        await queue.WhenIdleAsync().WaitAsync(Wait);
        Assert.Equal(["m1"], sender.Sent);
        Assert.Empty(_reports); // the one in flight is counted as dropped, not reported
    }

    [Fact]
    public async Task Stop_CancelsAWaitToRetry()
    {
        var still  = new FakeTimeProvider();
        var sender = new FakeSender(still, _ => Busy);
        var queue  = Create(sender, time: still);
        queue.Enqueue(Message("stuck"));
        await WaitUntil(() => !_reports.IsEmpty);

        Assert.Equal(1, queue.Stop());
        queue.Dispose();
        still.Advance(TimeSpan.FromMinutes(1));

        Assert.Single(sender.Sent);
    }

    // ── The URL stays secret ──────────────────────────────────────────────────

    [Fact]
    public async Task NoReport_ShowsTheUrl_OnTheRetryAndFailurePaths()
    {
        var sender = new FakeSender(_time, title => title switch
        {
            "throws"   => throw new HttpRequestException($"POST {WebhookUrlTests.Slack} failed"),
            "busy"     => Task.FromResult(Busy),
            _          => Task.FromResult(NotFound),
        });
        using var queue = Create(sender);

        queue.Enqueue(Message("throws"));
        queue.Enqueue(Message("busy"));
        queue.Enqueue(Message("missing"));
        await queue.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(9, _reports.Count);
        Assert.All(_reports, r =>
        {
            Assert.DoesNotContain("services/T0001", r.LogText());
            Assert.DoesNotContain("services/T0001", r.ToString());
            Assert.DoesNotContain("services/T0001", r.Result.Describe(ChatService.Slack));
        });
    }

    [Fact]
    public void ATarget_NeverShowsItsUrl()
    {
        Assert.DoesNotContain("services/T0001", Target.ToString());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Wait, "Timed out waiting for the queue.");
            await Task.Delay(5);
        }
    }

    private sealed class UrlSender(ConcurrentQueue<Uri> urls, Func<Uri, WebhookResult>? answer = null) : IWebhookSender
    {
        public readonly ConcurrentQueue<ChatService> Services = new();

        public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
        {
            urls.Enqueue(url);
            Services.Enqueue(service);
            return Task.FromResult(answer?.Invoke(url) ?? Ok);
        }
    }
}
