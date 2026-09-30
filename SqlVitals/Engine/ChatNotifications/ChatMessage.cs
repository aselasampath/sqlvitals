using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>How a message is coloured: the Teams title colour, and the emoji in front of a title or line.</summary>
public enum ChatTone
{
    Neutral,
    Warning,
    Critical,
    Good,
}

/// <summary>
/// A name and value shown side by side: a Slack section field, a Teams FactSet row. With
/// <paramref name="TimeUtc"/>, the value is that time, shown in each reader's own time zone.
/// </summary>
public sealed record ChatFact(string Title, string Value, DateTime? TimeUtc = null);

/// <summary>One alert in a message about several: "🔴 CPU 97 % (threshold ≥ 90 %) · since 10:42".</summary>
/// <param name="SinceUtc">When it started, shown in each reader's own time zone.</param>
public sealed record ChatLine(ChatTone Tone, string Label, string Text, DateTime? SinceUtc = null);

/// <summary>
/// One message to a chat channel, before it's written as Slack Block Kit or a Teams Adaptive
/// Card (see <see cref="ChatFormatter"/>). Built by <see cref="ChatMessageBuilder"/>; it never
/// holds a connection string, login, password, query text or webhook URL.
/// </summary>
/// <param name="Title">The first line, e.g. "🔴 Critical: CPU on Sales-Prod".</param>
/// <param name="Text">A paragraph under the title; may be empty.</param>
/// <param name="Fallback">The plain text Slack's own notifications show; empty uses <paramref name="Text"/>.</param>
/// <param name="SentFrom">"Sent from SqlVitals on PC01", or null when the text already says it.</param>
public sealed record ChatMessage(
    ChatTone                Tone,
    string                  Title,
    string                  Text,
    IReadOnlyList<ChatFact> Facts,
    string?                 SentFrom,
    string                  Fallback = "")
{
    public string FallbackText => string.IsNullOrEmpty(Fallback) ? Text : Fallback;

    /// <summary>One line per alert, after the facts.</summary>
    public IReadOnlyList<ChatLine> Lines { get; init; } = [];

    /// <summary>How many more alerts there were than <see cref="Lines"/> shows: "…and N more".</summary>
    public int More { get; init; }

    /// <summary>What the message is about, for the app log: "test message", "2 alerts on Sales-Prod".</summary>
    public string About { get; init; } = "message";
}

/// <summary>An alert to post because it started or turned critical (#121).</summary>
/// <param name="NowCritical">
/// A warning the channel was already told about turned critical: "Now critical". False for an
/// alert that's new to the channel, including a warning a Critical only channel skipped.
/// </param>
public sealed record RaisedAlert(Alert Alert, bool NowCritical = false);

/// <summary>Builds the messages SqlVitals posts, in the same words as the Alerts page and the Windows notifications.</summary>
public static class ChatMessageBuilder
{
    /// <summary>A message lists this many alerts, then "…and N more".</summary>
    public const int MaxLines = 10;

    /// <summary>"Sent from SqlVitals on PC01": the product and the PC, never the Windows user (#108, D9).</summary>
    public static string SentFrom(string pcName) => $"Sent from SqlVitals on {pcName}";

    /// <summary>The message Send test message posts (#120, #121).</summary>
    /// <param name="connections">"all monitored connections", "Sales-Prod and HR-DB"; null says only that alerts will come.</param>
    public static ChatMessage Test(string pcName, string? connections = null) => new(
        ChatTone.Neutral,
        "SqlVitals test message",
        connections is null
            ? $"Test message from SqlVitals on {pcName}. Alerts will be posted here."
            : $"Test message from SqlVitals on {pcName}. Alerts for {connections} will be posted here.",
        [],
        SentFrom: null)
    {
        About = "test message",
    };

    /// <summary>
    /// The message for the alerts of one connection that started or turned critical on one
    /// sample (#121): critical first, then in indicator order, as the Windows notification lists
    /// them. One alert gets its figure, detail and start as facts; several get a line each.
    /// </summary>
    public static ChatMessage Raised(string connectionName, IReadOnlyList<RaisedAlert> alerts, string pcName)
    {
        if (alerts.Count == 0)
            throw new ArgumentException("There's nothing to post.", nameof(alerts));

        var sorted   = alerts.OrderByDescending(a => a.Alert.Severity).ThenBy(a => a.Alert.Indicator).ToList();
        var critical = sorted.Count(a => a.Alert.Severity == HealthLevel.Critical);
        var tone     = critical > 0 ? ChatTone.Critical : ChatTone.Warning;

        if (sorted.Count == 1)
        {
            var (alert, nowCritical) = (sorted[0].Alert, sorted[0].NowCritical);
            var name  = HealthThresholds.Info(alert.Indicator).Name;
            var title = nowCritical
                ? $"{ChatFormatter.Emoji(ChatTone.Critical)} Now critical: {name} on {connectionName}"
                : $"{ChatFormatter.Emoji(tone)} {AlertText.Severity(alert.Severity)}: {name} on {connectionName}";

            var facts = new List<ChatFact> { new("Figure", AlertText.Figure(alert)) };
            if (AlertText.Context(alert) is { } context)
                facts.Add(new ChatFact("Detail", context));
            facts.Add(new ChatFact("Started", "", alert.StartedUtc));

            return new ChatMessage(tone, title, "", facts, SentFrom(pcName),
                                   Fallback: $"{title}. {name} {AlertText.Figure(alert)}.")
            {
                About = $"alert on {connectionName}",
            };
        }

        var many = critical == 0
            ? $"{ChatFormatter.Emoji(tone)} {sorted.Count} alerts on {connectionName}"
            : $"{ChatFormatter.Emoji(tone)} {sorted.Count} alerts on {connectionName} ({critical} critical)";

        var lines = sorted.Take(MaxLines).Select(a => new ChatLine(
            a.Alert.Severity == HealthLevel.Critical ? ChatTone.Critical : ChatTone.Warning,
            HealthThresholds.Info(a.Alert.Indicator).Name,
            (a.NowCritical ? "now critical, " : "") + AlertText.Figure(a.Alert) +
            (AlertText.Context(a.Alert) is { } context ? $" · {context}" : ""),
            a.Alert.StartedUtc)).ToList();

        var names = sorted.Select(a => HealthThresholds.Info(a.Alert.Indicator).Name).ToList();
        return new ChatMessage(tone, many, "", [], SentFrom(pcName), Fallback: $"{many}: {List(names)}.")
        {
            Lines = lines,
            More  = sorted.Count - lines.Count,
            About = $"{sorted.Count} alerts on {connectionName}",
        };
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };
}
