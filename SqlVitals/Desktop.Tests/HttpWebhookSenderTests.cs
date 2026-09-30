using System.Net;
using System.Net.Http;
using System.Text;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Tests;

/// <summary>The real sender over a fake handler: no test reaches a real webhook (#120).</summary>
public sealed class HttpWebhookSenderTests
{
    private static readonly Uri Url = new("https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa");

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string?             Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Request = request;
            Body    = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancel);
            return await answer(request, cancel);
        }
    }

    private static FakeHandler Answer(HttpStatusCode status, string? body = null, string mediaType = "text/plain",
                                      Action<HttpResponseMessage>? headers = null) =>
        new((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? string.Empty, Encoding.UTF8, mediaType),
            };
            headers?.Invoke(response);
            return Task.FromResult(response);
        });

    [Fact]
    public async Task PostsTheJson_AndMapsSlacksOk()
    {
        var handler = Answer(HttpStatusCode.OK, "ok");
        using var sender = new HttpWebhookSender(handler);

        var result = await sender.SendAsync(Url, """{"text":"hi"}""", ChatService.Slack);

        Assert.Equal(WebhookOutcome.Posted, result.Outcome);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("application/json", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"text":"hi"}""", handler.Body);
        Assert.StartsWith("SqlVitals/", handler.Request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Teams202_IsAccepted()
    {
        using var sender = new HttpWebhookSender(Answer(HttpStatusCode.Accepted));

        Assert.Equal(WebhookOutcome.Accepted, (await sender.SendAsync(Url, "{}", ChatService.Teams)).Outcome);
    }

    [Fact]
    public async Task ARejection_KeepsWhatSlackSaid()
    {
        using var sender = new HttpWebhookSender(Answer(HttpStatusCode.Forbidden, "invalid_token"));

        var result = await sender.SendAsync(Url, "{}", ChatService.Slack);

        Assert.Equal((WebhookOutcome.Rejected, 403, "invalid_token"), (result.Outcome, result.StatusCode, result.ServiceError));
    }

    [Fact]
    public async Task A429_ReadsRetryAfter()
    {
        using var sender = new HttpWebhookSender(Answer((HttpStatusCode)429, "rate_limited",
            headers: r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20))));

        var result = await sender.SendAsync(Url, "{}", ChatService.Slack);

        Assert.Equal((WebhookOutcome.RateLimited, TimeSpan.FromSeconds(20)), (result.Outcome, result.RetryAfter));
    }

    [Fact]
    public async Task AnHtmlPageAnswering200_IsntCountedAsPosted()
    {
        using var sender = new HttpWebhookSender(Answer(HttpStatusCode.OK, "<html>Sign in</html>", "text/html"));

        Assert.Equal(WebhookOutcome.Unexpected, (await sender.SendAsync(Url, "{}", ChatService.Teams)).Outcome);
    }

    [Fact]
    public async Task ANetworkFailure_IsAResult_AndNeverCarriesTheUrl()
    {
        using var sender = new HttpWebhookSender(new FakeHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host is known. ({Url})")));

        var result = await sender.SendAsync(Url, "{}", ChatService.Slack);

        Assert.Equal(WebhookOutcome.Unreachable, result.Outcome);
        foreach (var text in new[] { result.Describe(ChatService.Slack), result.LogText(), result.ToString() })
            Assert.DoesNotContain("hooks.slack.com", text);
    }

    [Fact]
    public async Task NoAnswerInTime_TimesOut()
    {
        using var sender = new HttpWebhookSender(new FakeHandler(async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        Assert.Equal(WebhookOutcome.TimedOut, (await sender.SendAsync(Url, "{}", ChatService.Slack)).Outcome);
    }

    [Fact]
    public async Task CancellingIt_IsCancelled_NotATimeout()
    {
        using var cancel = new CancellationTokenSource();
        using var sender = new HttpWebhookSender(new FakeHandler(async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        Assert.Equal(WebhookOutcome.Cancelled, (await sender.SendAsync(Url, "{}", ChatService.Slack, cancel.Token)).Outcome);
    }

    [Fact]
    public async Task OnlyTheStartOfALongAnswer_IsRead()
    {
        using var sender = new HttpWebhookSender(Answer(HttpStatusCode.BadRequest, new string('x', 100_000)));

        var result = await sender.SendAsync(Url, "{}", ChatService.Slack);

        Assert.True(result.ServiceError!.Length <= 200);
    }
}
