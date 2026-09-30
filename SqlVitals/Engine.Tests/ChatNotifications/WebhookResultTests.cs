using System.Net.Http;
using System.Net.Sockets;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Engine.Tests.ChatNotifications;

public sealed class WebhookResultTests
{
    private static readonly TimeSpan Elapsed = TimeSpan.FromMilliseconds(312);

    private static WebhookResult Answer(int status, string? body = null, ChatService service = ChatService.Slack,
                                        string? mediaType = "text/plain") =>
        WebhookResult.FromResponse(service, status, mediaType, body, Elapsed);

    [Fact]
    public void Slack200Ok_IsPosted_WithTheTime()
    {
        var result = Answer(200, "ok");

        Assert.Equal(WebhookOutcome.Posted, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal("Sent in 312 ms.", result.Describe(ChatService.Slack));
    }

    [Fact]
    public void Teams202_IsAccepted_AndSaysToCheckTheChannel()
    {
        var result = Answer(202, service: ChatService.Teams);

        Assert.Equal(WebhookOutcome.Accepted, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.StartsWith("Accepted by the workflow in 312 ms. Check the channel", result.Describe(ChatService.Teams));
    }

    [Fact]
    public void Teams200_IsPosted()
    {
        Assert.Equal(WebhookOutcome.Posted, Answer(200, service: ChatService.Teams).Outcome);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public void NotFoundOrGone_SaysTheWebhookWasRemoved(int status)
    {
        var result = Answer(status, "no_service");

        Assert.Equal(WebhookOutcome.NotFound, result.Outcome);
        Assert.Contains($"doesn't exist or was removed ({status})", result.Describe(ChatService.Slack));
    }

    [Theory]
    [InlineData(400, "invalid_payload")]
    [InlineData(403, "invalid_token")]
    [InlineData(403, "action_prohibited")]
    public void Rejected_SaysWhatTheServiceSaid(int status, string said)
    {
        var result = Answer(status, said + "\n");

        Assert.Equal(WebhookOutcome.Rejected, result.Outcome);
        Assert.Equal(said, result.ServiceError);
        Assert.Equal($"Rejected by Slack ({status}): {said}.", result.Describe(ChatService.Slack));
    }

    [Fact]
    public void Slack200WithoutOk_IsUnexpected_NotPosted()
    {
        Assert.Equal(WebhookOutcome.Unexpected, Answer(200, "something else").Outcome);
    }

    [Theory]
    [InlineData(ChatService.Slack)]
    [InlineData(ChatService.Teams)]
    public void A200HtmlPage_IsUnexpected_ForExampleAWiFiSignInPage(ChatService service)
    {
        var result = Answer(200, "<html><body>Sign in to the hotel Wi-Fi</body></html>", service, "text/html");

        Assert.Equal(WebhookOutcome.Unexpected, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Null(result.ServiceError);   // the page's text is never shown
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void ARedirect_IsRejected_NotFollowed(int status)
    {
        var result = Answer(status);

        Assert.Equal(WebhookOutcome.Rejected, result.Outcome);
        Assert.Contains("redirect", result.ServiceError);
    }

    [Fact]
    public void A407_SaysTheProxyWantsASignIn()
    {
        var result = Answer(407);

        Assert.Equal(WebhookOutcome.Unreachable, result.Outcome);
        Assert.Contains("proxy asked for a sign-in", result.Describe(ChatService.Teams));
    }

    [Fact]
    public void A429_KeepsItsRetryAfter()
    {
        var result = WebhookResult.FromResponse(ChatService.Slack, 429, null, null, Elapsed, TimeSpan.FromSeconds(20));

        Assert.Equal(WebhookOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(20), result.RetryAfter);
        Assert.Contains("Try again in 20 s", result.Describe(ChatService.Slack));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void A5xx_IsAServerError(int status)
    {
        Assert.Equal(WebhookOutcome.ServerError, Answer(status).Outcome);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("0", 1)]
    [InlineData("100000", 300)]
    public void RetryAfter_InSeconds_IsClamped(string header, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WebhookResult.ParseRetryAfter(header, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RetryAfter_AsADate_IsTheTimeUntilThen()
    {
        var now = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(45), WebhookResult.ParseRetryAfter("Wed, 30 Sep 2026 10:00:45 GMT", now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public void RetryAfter_MissingOrUnreadable_IsNull(string? header)
    {
        Assert.Null(WebhookResult.ParseRetryAfter(header, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ADnsFailure_CantReachTheService()
    {
        var ex = new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (hooks.slack.com:443)",
                                          new SocketException((int)SocketError.HostNotFound));

        var result = WebhookResult.FromException(ex, Elapsed);

        Assert.Equal(WebhookOutcome.Unreachable, result.Outcome);
        Assert.Equal("Can't reach Slack: its address couldn't be found (DNS). Check the network, the proxy and the firewall.",
                     result.Describe(ChatService.Slack));
    }

    [Fact]
    public void ATimeout_SaysSo()
    {
        var result = WebhookResult.FromException(new TaskCanceledException(), Elapsed, timedOut: true);

        Assert.Equal(WebhookOutcome.TimedOut, result.Outcome);
        Assert.Equal("Timed out: Microsoft Teams didn't answer within 10 seconds.", result.Describe(ChatService.Teams));
    }

    [Fact]
    public void AnExceptionsMessage_IsNeverKept_EvenWhenItHoldsTheUrl()
    {
        var ex = new HttpRequestException(HttpRequestError.Unknown, $"Failed to POST {WebhookUrlTests.Slack}");

        var result = WebhookResult.FromException(ex, Elapsed);

        foreach (var text in new[] { result.Describe(ChatService.Slack), result.LogText(), result.ToString() })
            Assert.DoesNotContain("hooks.slack.com", text);
    }

    [Fact]
    public void WhatTheServiceSaid_LosesLinksAndControlCharacters_AndIsCutShort()
    {
        var said = WebhookResult.Clean($"bad\r\n\tthing at {WebhookUrlTests.Slack} " + new string('x', 500));

        Assert.NotNull(said);
        Assert.StartsWith("bad thing at [link removed] ", said);
        Assert.DoesNotContain("hooks.slack.com", said);
        Assert.True(said!.Length <= 200);
    }
}
