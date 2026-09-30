using Microsoft.Extensions.Time.Testing;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>When a message that couldn't be sent is tried again (#122, design section 07).</summary>
public sealed class RetryPolicyTests
{
    private static WebhookResult Result(WebhookOutcome outcome, int? status = null, TimeSpan? retryAfter = null) =>
        new(outcome, status, null, TimeSpan.Zero, retryAfter);

    [Theory]
    [InlineData(WebhookOutcome.ServerError)]
    [InlineData(WebhookOutcome.Unreachable)]
    [InlineData(WebhookOutcome.TimedOut)]
    public void ServerErrorsAndNoAnswer_AreRetriedThreeTimes_After2And10And30Seconds(WebhookOutcome outcome)
    {
        var result = Result(outcome);

        Assert.Equal(new RetryDecision(TimeSpan.FromSeconds(2), 1), RetryPolicy.Decide(result, retries: 0, rateLimited: 0));
        Assert.Equal(new RetryDecision(TimeSpan.FromSeconds(10), 2), RetryPolicy.Decide(result, retries: 1, rateLimited: 0));
        Assert.Equal(new RetryDecision(TimeSpan.FromSeconds(30), 3), RetryPolicy.Decide(result, retries: 2, rateLimited: 0));
        Assert.Null(RetryPolicy.Decide(result, retries: 3, rateLimited: 0));
    }

    [Theory]
    [InlineData(WebhookOutcome.NotFound)]
    [InlineData(WebhookOutcome.Rejected)]
    [InlineData(WebhookOutcome.Unexpected)]
    [InlineData(WebhookOutcome.Cancelled)]
    [InlineData(WebhookOutcome.Posted)]
    [InlineData(WebhookOutcome.Accepted)]
    public void OtherAnswers_AreNeverRetried(WebhookOutcome outcome)
    {
        Assert.Null(RetryPolicy.Decide(Result(outcome), retries: 0, rateLimited: 0));
    }

    [Fact]
    public void AProxySignIn_IsNotRetried()
    {
        // 407 maps to Unreachable with its own reason, but asking again gets the same answer.
        var result = WebhookResult.FromResponse(ChatService.Slack, 407, null, null, TimeSpan.Zero);

        Assert.Equal(WebhookOutcome.Unreachable, result.Outcome);
        Assert.Null(RetryPolicy.Decide(result, 0, 0));
        Assert.Null(RetryPolicy.Decide(result, 0, 0, networkSettling: true));
    }

    [Fact]
    public void RateLimited_WaitsForRetryAfter_WithoutUsingARetry()
    {
        var decision = RetryPolicy.Decide(Result(WebhookOutcome.RateLimited, 429, TimeSpan.FromSeconds(20)), retries: 3, rateLimited: 0);

        Assert.Equal(new RetryDecision(TimeSpan.FromSeconds(20), 0), decision);
    }

    [Fact]
    public void RateLimited_WithoutRetryAfter_Waits30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RetryPolicy.Decide(Result(WebhookOutcome.RateLimited, 429), 0, 0)?.Wait);
    }

    [Fact]
    public void RateLimited_WaitsNoMoreThan5Minutes_AndAtLeastASecond()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RetryPolicy.Decide(Result(WebhookOutcome.RateLimited, 429, TimeSpan.FromHours(2)), 0, 0)?.Wait);
        Assert.Equal(TimeSpan.FromSeconds(1), RetryPolicy.Decide(Result(WebhookOutcome.RateLimited, 429, TimeSpan.Zero), 0, 0)?.Wait);
    }

    [Fact]
    public void RateLimited_GivesUpAfter5RetriesInARow_SoTheChannelEndsUpFailing()
    {
        var result = Result(WebhookOutcome.RateLimited, 429);

        Assert.NotNull(RetryPolicy.Decide(result, 0, rateLimited: 4));
        Assert.Null(RetryPolicy.Decide(result, 0, rateLimited: 5));
    }

    [Fact]
    public void WhileTheNetworkSettles_NoAnswer_IsTriedAgainWithoutUsingARetry()
    {
        var decision = RetryPolicy.Decide(Result(WebhookOutcome.Unreachable), retries: 3, rateLimited: 0, networkSettling: true);

        Assert.Equal(new RetryDecision(TimeSpan.FromSeconds(10), 0), decision);
        // A server error is an answer: the network is back.
        Assert.Null(RetryPolicy.Decide(Result(WebhookOutcome.ServerError), retries: 3, rateLimited: 0, networkSettling: true));
    }

    [Fact]
    public void ADecision_SaysWhichRetryItIs_ForTheLog()
    {
        Assert.Equal("retry 2 of 3 in 10 s", new RetryDecision(TimeSpan.FromSeconds(10), 2).LogText());
        Assert.Equal("trying again in 30 s", new RetryDecision(TimeSpan.FromSeconds(30), 0).LogText());
    }

    [Fact]
    public void TheNetworkSettles_ForAMinuteAfterThePcWakes()
    {
        var time  = new FakeTimeProvider();
        var grace = new NetworkGrace(time);
        Assert.False(grace.IsSettling);

        grace.Resumed();
        Assert.True(grace.IsSettling);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(grace.IsSettling);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(grace.IsSettling);
    }
}
