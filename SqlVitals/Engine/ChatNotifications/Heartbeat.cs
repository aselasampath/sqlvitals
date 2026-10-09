using System.Globalization;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>How often a channel gets SqlVitals' heartbeat (#170).</summary>
public enum HeartbeatEvery
{
    /// <summary>No heartbeat: the default.</summary>
    Off,

    /// <summary>Every <see cref="HeartbeatSchedule.Hours"/> hours, on the hour from midnight.</summary>
    Hours,

    /// <summary>Once a day at <see cref="HeartbeatSchedule.At"/>.</summary>
    Daily,
}

/// <summary>
/// When a channel gets a "still watching" message (#170), in this PC's local time, so a team can
/// set up an alert for when one doesn't arrive. Hours count from midnight: every 6 hours is
/// 00:00, 06:00, 12:00 and 18:00, whenever SqlVitals started, so the times are known in advance.
/// </summary>
/// <param name="Hours">For <see cref="HeartbeatEvery.Hours"/>: one of <see cref="AllowedHours"/>, which divide a day.</param>
/// <param name="At">For <see cref="HeartbeatEvery.Daily"/>: the local time of day.</param>
public sealed record HeartbeatSchedule(HeartbeatEvery Every, int Hours, TimeSpan At)
{
    /// <summary>The hour counts offered: each divides 24, so every day has the same times.</summary>
    public static readonly IReadOnlyList<int> AllowedHours = [1, 2, 3, 4, 6, 8, 12];

    public static readonly TimeSpan DefaultAt = TimeSpan.FromHours(9);

    public static readonly HeartbeatSchedule Off = new(HeartbeatEvery.Off, 0, DefaultAt);

    public static HeartbeatSchedule EveryHours(int hours) => new(HeartbeatEvery.Hours, hours, DefaultAt);

    public static HeartbeatSchedule DailyAt(TimeSpan at) => new(HeartbeatEvery.Daily, 0, at);

    /// <summary>Off, or a schedule that can't be kept (an hour count that doesn't divide a day, a time outside one day).</summary>
    public bool IsOn => Every switch
    {
        HeartbeatEvery.Hours => AllowedHours.Contains(Hours),
        HeartbeatEvery.Daily => At >= TimeSpan.Zero && At < TimeSpan.FromDays(1),
        _                    => false,
    };

    /// <summary>How long between two heartbeats: what a check for a missing one should allow, plus some grace.</summary>
    public TimeSpan Interval => Every == HeartbeatEvery.Hours ? TimeSpan.FromHours(Hours) : TimeSpan.FromDays(1);

    /// <summary>
    /// The first heartbeat strictly after <paramref name="after"/>, in <paramref name="zone"/>'s
    /// wall clock; null when off. A time skipped by a daylight saving change is sent an hour
    /// later; a time that happens twice is sent once.
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        if (!IsOn)
            return null;

        var today = TimeZoneInfo.ConvertTime(after, zone).DateTime.Date;
        for (var day = 0; day <= 2; day++)
        {
            foreach (var time in TimesOfDay())
            {
                var due = Instant(today.AddDays(day) + time, zone);
                if (due > after)
                    return due;
            }
        }
        return null; // unreachable: every schedule has a time each day
    }

    /// <summary>"Off", "Every hour", "Every 6 hours (00:00, 06:00, 12:00, 18:00)", "Daily at 09:00".</summary>
    public string Describe()
    {
        if (!IsOn)
            return "Off";
        if (Every == HeartbeatEvery.Daily)
            return $"Daily at {Clock(At)}";
        if (Hours == 1)
            return "Every hour";
        var times = TimesOfDay().ToList();
        return times.Count <= 4
            ? $"Every {Hours} hours ({string.Join(", ", times.Select(Clock))})"
            : $"Every {Hours} hours, from 00:00";
    }

    /// <summary>"09:00".</summary>
    public static string Clock(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private IEnumerable<TimeSpan> TimesOfDay() =>
        Every == HeartbeatEvery.Daily
            ? [At]
            : Enumerable.Range(0, 24 / Hours).Select(i => TimeSpan.FromHours(i * Hours));

    private static DateTimeOffset Instant(DateTime wall, TimeZoneInfo zone)
    {
        // Clocks went forward past it: the same moment an hour on.
        if (zone.IsInvalidTime(wall))
            wall = wall.AddHours(1);
        return new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone.GetUtcOffset(wall));
    }
}

/// <summary>
/// The optional URL a heartbeat also calls (#170): a "dead man's switch" service such as
/// Healthchecks.io, Cronitor or Better Stack, which alerts Slack, Teams, email or a phone when
/// the calls stop. Slack can't notice a missing message by itself. Stored encrypted with the
/// channel, like the webhook, because anyone with it can report SqlVitals as alive.
/// </summary>
public static class HeartbeatPing
{
    /// <summary>Why the URL can't be saved; null when it can, or is empty (no ping).</summary>
    public static string? Check(string? url, string? webhookUrl = null)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return null;
        if (text.Length > WebhookUrl.MaxLength)
            return $"That's too long for a ping URL (over {WebhookUrl.MaxLength} characters).";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return "That isn't a whole URL. Paste the ping URL from your monitoring service, starting with https://.";
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return "The ping URL must start with https://.";
        if (!string.IsNullOrWhiteSpace(webhookUrl) && Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var webhook) &&
            WebhookUrl.Normalize(webhook) == WebhookUrl.Normalize(uri))
            return "The ping URL is the channel's webhook. Use the URL your monitoring service gives you for this check.";
        return null;
    }
}

/// <summary>
/// The setup steps the channel form copies (#170): how to get an alert when a heartbeat doesn't
/// arrive, for the channel's service and schedule. Slack can't notice a missing message by
/// itself, so its steps are a dead man's switch service; Teams gets a scheduled Power Automate
/// flow too. Plain text, to paste into a ticket, a wiki or a message.
/// </summary>
public static class HeartbeatSetup
{
    /// <summary>How long after a heartbeat is due a check for it runs, and a service waits before alerting.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    public static string Steps(ChatService service, HeartbeatSchedule schedule, string pcName)
    {
        if (!schedule.IsOn)
            return "Choose a heartbeat schedule first: SqlVitals then posts \"" + ChatMessageBuilder.HeartbeatTitle +
                   "\" to the channel, and these steps show how to get an alert when one doesn't arrive.";

        var every = Every(schedule.Interval);
        var when  = schedule.Describe();
        when = char.ToLowerInvariant(when[0]) + when[1..];
        var lines = new List<string>
        {
            $"SqlVitals on {pcName} posts \"{ChatMessageBuilder.HeartbeatTitle}\" to this channel {when}, in {pcName}'s time zone.",
            "",
            service == ChatService.Slack
                ? "Slack can't alert when a message doesn't arrive, so use a dead man's switch service:"
                : "Option 1, any service: a dead man's switch",
            "1. Create a check at Healthchecks.io (it has a free plan), Cronitor or Better Stack.",
            $"2. Expect a ping {every}, with a grace time of {(int)Grace.TotalMinutes} minutes.",
            $"3. Connect the service to {ChatServices.Name(service)}, so it posts to this channel when the check goes down.",
            "4. Paste the check's ping URL into Heartbeat ping URL in SqlVitals, and save the channel.",
            "SqlVitals calls the URL with each heartbeat while it's watching at least one server, so the check also goes down when nothing is watched.",
        };

        if (service == ChatService.Teams)
        {
            var window = (int)Math.Ceiling(schedule.Interval.TotalHours);
            lines.AddRange(
            [
                "",
                "Option 2, Teams only: a scheduled Power Automate flow (in Teams, Workflows, or make.powerautomate.com)",
                $"1. Recurrence: {every}, starting {HeartbeatSchedule.Clock(FirstCheck(schedule))}, in {pcName}'s time zone.",
                "2. Microsoft Teams, Get messages: this team and channel.",
                "3. Data operation, Filter array: From is the value of Get messages; in advanced mode:",
                // The card is an attachment: its text isn't in the message body.
                $"   @and(contains(string(item()?['attachments']), 'SqlVitals heartbeat'), greater(ticks(item()?['createdDateTime']), ticks(addHours(utcNow(), -{window}))))",
                "4. Condition: length(body('Filter_array')) is equal to 0.",
                $"5. If yes: Microsoft Teams, Post message in a chat or channel: \"⚠ No heartbeat from SqlVitals on {pcName}. Check that it's running and the PC is awake.\"",
                "Get messages is a preview action: if the flow stops finding heartbeats, check its output in the flow's run history.",
            ]);
        }
        return string.Join(Environment.NewLine, lines);
    }

    // The first heartbeat of the day plus the grace: when a check should first look.
    private static TimeSpan FirstCheck(HeartbeatSchedule schedule)
    {
        var first = schedule.Every == HeartbeatEvery.Daily ? schedule.At : TimeSpan.Zero;
        return TimeSpan.FromTicks((first + Grace).Ticks % TimeSpan.TicksPerDay);
    }

    private static string Every(TimeSpan interval) =>
        interval >= TimeSpan.FromDays(1) ? "every day"
        : interval == TimeSpan.FromHours(1) ? "every hour"
        : $"every {(int)interval.TotalHours} hours";
}
