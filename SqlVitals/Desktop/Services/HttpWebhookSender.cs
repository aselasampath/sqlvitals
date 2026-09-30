using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Posts to Slack and Teams webhooks over HTTPS (#120), through the Windows proxy settings so
/// corporate networks work. One <see cref="HttpClient"/> for the life of the app.
///
/// Never throws for a network or HTTP failure, and never keeps an exception's text: the result is
/// built from the status code and the exception's type, so the URL can't leak through a message.
/// </summary>
public sealed class HttpWebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _http;

    public HttpWebhookSender()
        : this(new SocketsHttpHandler
        {
            // The system proxy (including a PAC script), signed in as the Windows user.
            UseProxy                 = true,
            DefaultProxyCredentials  = CredentialCache.DefaultCredentials,
            // A redirect could post the alert to a host the user never chose; it's reported instead.
            AllowAutoRedirect        = false,
            // DNS changes are picked up by new connections.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout           = WebhookResult.Timeout,
        })
    {
    }

    internal HttpWebhookSender(HttpMessageHandler handler)
    {
        // Timed per request below, so a timeout can be told apart from SqlVitals cancelling.
        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var version = typeof(HttpWebhookSender).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SqlVitals/{version?.Major}.{version?.Minor}.{version?.Build}");
    }

    /// <summary>How long a request may take; shorter in tests.</summary>
    internal TimeSpan Timeout { get; init; } = WebhookResult.Timeout;

    public async Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                                            .ConfigureAwait(false);
            var body = await ReadStartAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var retryAfter = WebhookResult.ParseRetryAfter(response.Headers.RetryAfter?.ToString(), DateTimeOffset.UtcNow);

            return WebhookResult.FromResponse(service, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                                              body, Stopwatch.GetElapsedTime(started), retryAfter);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return WebhookResult.FromException(new OperationCanceledException(), Stopwatch.GetElapsedTime(started), cancelled: true);
        }
        catch (OperationCanceledException)
        {
            return WebhookResult.FromException(new TimeoutException(), Stopwatch.GetElapsedTime(started), timedOut: true);
        }
        catch (Exception ex)
        {
            return WebhookResult.FromException(ex, Stopwatch.GetElapsedTime(started));
        }
    }

    // The first KB of the answer: enough for any error either service sends.
    private static async Task<string?> ReadStartAsync(HttpContent content, CancellationToken cancel)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var buffer = new byte[WebhookResult.MaxBodyBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }
        return read == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }

    public void Dispose() => _http.Dispose();
}
