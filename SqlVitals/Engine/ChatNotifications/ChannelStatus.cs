using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// What a chat channel's row in Settings says about it: "Never used", "Last posted 10:42" (#120)
/// or "⚠ Failing since 10:42: webhook not found (404)" (#122). Kept in memory, so it starts again
/// at "Never used" each time SqlVitals starts.
/// </summary>
/// <param name="LastPostedUtc">When a message last reached the channel (or its Teams workflow).</param>
/// <param name="FailingSinceUtc">When a message was first given up since the last one that got through; null when not failing.</param>
/// <param name="Problem">Why the latest message was given up, e.g. "webhook not found (404)".</param>
public sealed record ChannelStatus(DateTime? LastPostedUtc, DateTime? FailingSinceUtc = null, string? Problem = null)
{
    public static ChannelStatus NeverUsed { get; } = new((DateTime?)null);

    /// <summary>The title of the Windows notification when a channel starts failing.</summary>
    public const string FailingTitle = "Chat channel failing";

    private const int MaxNameInNotice = 80;

    public bool IsFailing => FailingSinceUtc is not null;

    /// <summary>A message (or a test message) reached the channel: no longer failing.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "A transition from this status, read like Failed: status.Posted(now)")]
    public ChannelStatus Posted(DateTime utc) => new(utc);

    /// <summary>
    /// A message was given up after its retries. "Failing since" keeps the first failure's time;
    /// the problem is the latest one.
    /// </summary>
    public ChannelStatus Failed(DateTime utc, string problem) => new(LastPostedUtc, FailingSinceUtc ?? utc, problem);

    /// <param name="nowLocal">This PC's time now, so a time on another day shows its date.</param>
    public string Text(DateTime nowLocal)
    {
        if (FailingSinceUtc is { } failing)
            return $"⚠ Failing since {Time(failing, nowLocal)}" + (Problem is null ? "" : $": {Problem}");
        if (LastPostedUtc is { } posted)
            return $"Last posted {Time(posted, nowLocal)}";
        return "Never used";
    }

    /// <summary>
    /// The Windows notification's text when a channel starts failing: "Alerts aren't reaching DBA
    /// team (Teams): webhook not found. Check Settings → Alerts & chat."
    /// </summary>
    public static string FailingNotice(string channelName, ChatService service, WebhookResult result)
    {
        var name = channelName.Length > MaxNameInNotice ? channelName[..(MaxNameInNotice - 1)] + "…" : channelName;
        // "DBA team (Microsoft Teams)", unless the name already says which it is: "DBA team (Teams)".
        if (!name.Contains(service == ChatService.Teams ? "Teams" : "Slack", StringComparison.OrdinalIgnoreCase))
            name += $" ({ChatServices.Name(service)})";
        return $"Alerts aren't reaching {name}: {result.Problem(service)}. Check Settings → Alerts & chat.";
    }

    private static string Time(DateTime utc, DateTime nowLocal)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.Date == nowLocal.Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}
