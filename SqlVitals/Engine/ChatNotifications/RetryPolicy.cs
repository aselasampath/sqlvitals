namespace SqlVitals.Engine.ChatNotifications;

/// <summary>Try a message again after <paramref name="Wait"/>.</summary>
/// <param name="Number">Which of the <see cref="RetryPolicy.MaxRetries"/> retries this is; 0 when it doesn't count as one (429, or the network settling after the PC wakes).</param>
public readonly record struct RetryDecision(TimeSpan Wait, int Number)
{
    /// <summary>For the log: "retry 2 of 3 in 10 s", "trying again in 30 s".</summary>
    public string LogText() => Number > 0
        ? $"retry {Number} of {RetryPolicy.MaxRetries} in {RetryPolicy.Seconds(Wait)}"
        : $"trying again in {RetryPolicy.Seconds(Wait)}";
}

/// <summary>
/// Whether a message that couldn't be sent is tried again, and when (#122):
/// <list type="bullet">
/// <item>429: after Retry-After, or 30 s without one. Doesn't use one of the 3 retries, but at most
/// 5 in a row, so a channel stuck on 429 still ends up Failing.</item>
/// <item>5xx, no answer, timed out: up to 3 retries, after 2, 10 and 30 s.</item>
/// <item>Anything else (404, 400, 403, a redirect, a proxy that wants a sign-in (407), a page that
/// isn't the service's): never, because trying again gets the same answer.</item>
/// </list>
/// A retry after a timeout or 5xx can post a message twice, because the service may have posted it
/// before the error. Neither Slack nor Teams webhooks can drop a repeat, so that's accepted.
/// </summary>
public static class RetryPolicy
{
    public const int MaxRetries = 3;

    /// <summary>Retries after 429s in a row; the next 429 gives up.</summary>
    public const int MaxRateLimited = 5;

    /// <summary>The waits before retries 1, 2 and 3.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>The wait after a 429 without Retry-After.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>The wait between tries while the network settles after the PC wakes (see <see cref="NetworkGrace"/>).</summary>
    public static readonly TimeSpan SettlingWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <param name="retries">Retries this message has used so far (the counted ones).</param>
    /// <param name="rateLimited">429s this message has had in a row so far, not counting this one.</param>
    /// <param name="networkSettling">The PC woke up less than a minute ago: no answer doesn't count toward giving up.</param>
    /// <returns>When to try again, or null to give up (or, for a success, nothing to do).</returns>
    public static RetryDecision? Decide(WebhookResult result, int retries, int rateLimited, bool networkSettling = false)
    {
        switch (result.Outcome)
        {
            case WebhookOutcome.RateLimited:
                if (rateLimited >= MaxRateLimited)
                    return null;
                var wait = result.RetryAfter ?? DefaultRetryAfter;
                // Also clamped when the header is read; a sender that skipped that can't stall the channel.
                wait = wait < MinRetryAfter ? MinRetryAfter : wait > MaxRetryAfter ? MaxRetryAfter : wait;
                return new RetryDecision(wait, 0);

            case WebhookOutcome.Unreachable when result.StatusCode == 407:
                // The proxy wants a sign-in SqlVitals can't give: asking again gets the same answer.
                return null;

            case WebhookOutcome.Unreachable when networkSettling:
                return new RetryDecision(SettlingWait, 0);

            case WebhookOutcome.ServerError or WebhookOutcome.Unreachable or WebhookOutcome.TimedOut:
                return retries < MaxRetries ? new RetryDecision(Backoff[retries], retries + 1) : null;

            default:
                return null;
        }
    }

    internal static string Seconds(TimeSpan span) => $"{Math.Ceiling(span.TotalSeconds):0} s";
}

/// <summary>
/// The minute after the PC wakes from sleep, while Wi-Fi or the VPN reconnects (#122, F18). In it,
/// a webhook that can't be reached is tried again without using up a retry, so a laptop opened
/// with messages waiting doesn't mark its channels Failing and raise a false "alerts aren't
/// reaching" notification. Shared by every channel; thread-safe.
/// </summary>
public sealed class NetworkGrace(TimeProvider time)
{
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(60);

    // A TimeProvider timestamp, which the clock being changed doesn't move (F19); 0 when never resumed.
    private long _resumedAt;

    /// <summary>The PC woke up.</summary>
    public void Resumed() => Interlocked.Exchange(ref _resumedAt, Math.Max(1, time.GetTimestamp()));

    public bool IsSettling
    {
        get
        {
            var at = Interlocked.Read(ref _resumedAt);
            return at != 0 && time.GetElapsedTime(at) < Length;
        }
    }
}
