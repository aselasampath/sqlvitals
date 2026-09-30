using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>How a POST to a webhook went.</summary>
public enum WebhookOutcome
{
    /// <summary>Slack answered 200 "ok", or Teams 200: the message is in the channel.</summary>
    Posted,

    /// <summary>Teams answered 202: the workflow accepted it, which doesn't prove the card was posted.</summary>
    Accepted,

    /// <summary>404 or 410: the webhook doesn't exist or was removed.</summary>
    NotFound,

    /// <summary>Any other 4xx (not 429), or a redirect, which isn't followed.</summary>
    Rejected,

    /// <summary>429: too many messages.</summary>
    RateLimited,

    /// <summary>5xx.</summary>
    ServerError,

    /// <summary>A 2xx that isn't the service's own answer: a proxy block page or a Wi-Fi sign-in page.</summary>
    Unexpected,

    /// <summary>DNS, a refused connection, the proxy, TLS: the request never got an answer.</summary>
    Unreachable,

    /// <summary>No answer within <see cref="WebhookResult.Timeout"/>.</summary>
    TimedOut,

    /// <summary>Given up by SqlVitals itself, e.g. when it exits.</summary>
    Cancelled,
}

/// <summary>
/// The result of one POST to a webhook (#120). Built from the answer or the exception, never
/// from the URL, and never holding an exception's text, which could carry the URL.
/// </summary>
/// <param name="StatusCode">The HTTP status, when there was an answer.</param>
/// <param name="ServiceError">
/// What the service said, cleaned up and short, e.g. Slack's "invalid_token"; or, for
/// <see cref="WebhookOutcome.Unreachable"/>, why there was no answer.
/// </param>
/// <param name="RetryAfter">From a 429's Retry-After header, when it had one.</param>
public sealed record WebhookResult(
    WebhookOutcome Outcome,
    int?           StatusCode,
    string?        ServiceError,
    TimeSpan       Elapsed,
    TimeSpan?      RetryAfter = null)
{
    /// <summary>How long a request may take before it's given up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>How much of an answer's body is read: enough for any error the services send.</summary>
    public const int MaxBodyBytes = 1024;

    private const int MaxServiceErrorLength = 200;

    public bool IsSuccess => Outcome is WebhookOutcome.Posted or WebhookOutcome.Accepted;

    /// <summary>Maps an HTTP answer to an outcome.</summary>
    /// <param name="mediaType">The answer's Content-Type media type, e.g. "text/html"; null when none.</param>
    public static WebhookResult FromResponse(ChatService service, int status, string? mediaType, string? body,
                                             TimeSpan elapsed, TimeSpan? retryAfter = null)
    {
        var isHtml = mediaType is not null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
        // An HTML page is never what Slack or Teams says; it's a proxy's or a sign-in page's.
        var said = isHtml ? null : Clean(body);

        var outcome = status switch
        {
            >= 200 and < 300 when isHtml                                  => WebhookOutcome.Unexpected,
            200 when service == ChatService.Slack && said == "ok"          => WebhookOutcome.Posted,
            >= 200 and < 300 when service == ChatService.Slack             => WebhookOutcome.Unexpected,
            202                                                            => WebhookOutcome.Accepted,
            >= 200 and < 300                                               => WebhookOutcome.Posted,
            404 or 410                                                     => WebhookOutcome.NotFound,
            429                                                            => WebhookOutcome.RateLimited,
            407                                                            => WebhookOutcome.Unreachable,
            >= 500                                                         => WebhookOutcome.ServerError,
            _                                                              => WebhookOutcome.Rejected,
        };

        var error = outcome switch
        {
            WebhookOutcome.Posted or WebhookOutcome.Accepted => null,
            WebhookOutcome.Unreachable                       => "the proxy asked for a sign-in",
            WebhookOutcome.Unexpected                        => null,
            _ when status is >= 300 and < 400                => "it answered with a redirect, which SqlVitals doesn't follow",
            _                                                => said,
        };

        return new WebhookResult(outcome, status, error, elapsed, outcome == WebhookOutcome.RateLimited ? retryAfter : null);
    }

    /// <summary>
    /// Maps a failed request to an outcome, from the exception's type only: its message may name
    /// the host or the URL, so it's never kept.
    /// </summary>
    /// <param name="timedOut">The request ran past <see cref="Timeout"/>.</param>
    /// <param name="cancelled">SqlVitals gave it up, e.g. on exit.</param>
    public static WebhookResult FromException(Exception ex, TimeSpan elapsed, bool timedOut = false, bool cancelled = false)
    {
        if (cancelled)
            return new WebhookResult(WebhookOutcome.Cancelled, null, null, elapsed);
        if (timedOut)
            return new WebhookResult(WebhookOutcome.TimedOut, null, null, elapsed);

        var why = ex is HttpRequestException http
            ? http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError   => "its address couldn't be found (DNS)",
                HttpRequestError.ConnectionError       => "the connection failed (network or firewall)",
                HttpRequestError.SecureConnectionError => "the secure connection failed (a proxy or firewall may be inspecting HTTPS)",
                HttpRequestError.ProxyTunnelError      => "the proxy refused the connection (it may need a sign-in)",
                _                                      => "the request failed",
            }
            : ex switch
            {
                System.Security.Authentication.AuthenticationException => "the secure connection failed (a proxy or firewall may be inspecting HTTPS)",
                IOException                                             => "the connection was lost",
                _                                                       => "the request failed",
            };
        return new WebhookResult(WebhookOutcome.Unreachable, null, why, elapsed);
    }

    /// <summary>
    /// Retry-After as seconds ("30") or an HTTP date; null when missing or unreadable. Clamped to
    /// 1 second – 5 minutes, so a strange value can neither hammer the service nor stall a channel.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        TimeSpan wait;
        if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(seconds);
        else if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            wait = at - now;
        else
            return null;

        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
             : wait > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5)
             : wait;
    }

    /// <summary>
    /// The result in words, for the test message and the status: "Sent in 312 ms.", "The webhook
    /// doesn't exist or was removed (404).", "Rejected by Slack (403): invalid_token."
    /// </summary>
    public string Describe(ChatService service)
    {
        var name = ChatServices.Name(service);
        var code = StatusCode is { } c ? $" ({c})" : string.Empty;
        return Outcome switch
        {
            WebhookOutcome.Posted      => $"Sent in {Ms(Elapsed)}.",
            WebhookOutcome.Accepted    => $"Accepted by the workflow in {Ms(Elapsed)}. Check the channel: Teams only confirms the workflow took the message, not that it was posted.",
            WebhookOutcome.NotFound    => $"The webhook doesn't exist or was removed{code}. Create a new one in {name} and paste its URL here.",
            WebhookOutcome.Rejected    => $"Rejected by {name}{code}" + (ServiceError is null ? "." : $": {ServiceError}."),
            WebhookOutcome.RateLimited => $"{name} says too many messages{code}. Try again in {(RetryAfter is { } r ? $"{Math.Ceiling(r.TotalSeconds):0} s" : "a minute")}.",
            WebhookOutcome.ServerError => $"{name} had a server error{code}. Try again in a minute.",
            WebhookOutcome.Unexpected  => $"Unexpected answer{code}: it didn't come from {name}. A proxy block page or a Wi-Fi sign-in page may be in the way.",
            WebhookOutcome.Unreachable => $"Can't reach {name}: {ServiceError ?? "the request failed"}{code}. Check the network, the proxy and the firewall.",
            WebhookOutcome.TimedOut    => $"Timed out: {name} didn't answer within {Timeout.TotalSeconds:0} seconds.",
            WebhookOutcome.Cancelled   => "Cancelled.",
            _                          => Outcome.ToString(),
        };
    }

    /// <summary>Short, for the app log: "posted (200) in 312 ms", "not found (404)", "timed out".</summary>
    public string LogText()
    {
        var code = StatusCode is { } c ? $" ({c})" : string.Empty;
        var text = Outcome switch
        {
            WebhookOutcome.Posted      => $"posted{code} in {Ms(Elapsed)}",
            WebhookOutcome.Accepted    => $"accepted{code} in {Ms(Elapsed)}",
            WebhookOutcome.NotFound    => $"webhook not found{code}",
            WebhookOutcome.Rejected    => $"rejected{code}",
            WebhookOutcome.RateLimited => $"rate limited{code}",
            WebhookOutcome.ServerError => $"server error{code}",
            WebhookOutcome.Unexpected  => $"unexpected answer{code}",
            WebhookOutcome.Unreachable => $"unreachable{code}",
            WebhookOutcome.TimedOut    => "timed out",
            WebhookOutcome.Cancelled   => "cancelled",
            _                          => Outcome.ToString(),
        };
        return ServiceError is null || IsSuccess ? text : $"{text}: {ServiceError}";
    }

    private static string Ms(TimeSpan span) => $"{Math.Max(0, span.TotalMilliseconds):0} ms";

    private static readonly Regex Link = new(@"(?i)\b(?:https?|ftp)://\S+", RegexOptions.Compiled);

    // What the service said, safe to show and log: printable, one line, short, and without any
    // link (an error could echo the URL it was sent to).
    internal static string? Clean(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var text = new StringBuilder(Math.Min(body.Length, MaxServiceErrorLength + 1));
        var space = false;
        foreach (var ch in body)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = text.Length > 0;
                continue;
            }
            if (space)
                text.Append(' ');
            space = false;
            text.Append(ch);
            if (text.Length > MaxServiceErrorLength * 2)
                break;
        }

        var cleaned = Link.Replace(text.ToString(), "[link removed]");
        if (cleaned.Length > MaxServiceErrorLength)
            cleaned = cleaned[..(MaxServiceErrorLength - 1)] + "…";
        return cleaned.Length == 0 ? null : cleaned;
    }
}

/// <summary>
/// Posts JSON to a webhook (#120). The only way SqlVitals reaches Slack or Teams, so tests use a
/// fake and no test ever calls a real webhook.
/// </summary>
public interface IWebhookSender
{
    /// <summary>
    /// Posts <paramref name="json"/>, giving up after <see cref="WebhookResult.Timeout"/>. Never
    /// throws for an HTTP or network failure: it returns a <see cref="WebhookResult"/> instead.
    /// </summary>
    Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default);
}
