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

    /// <summary>What the message says, in short, for a summary it's folded into; null for a test message.</summary>
    public ChatDigest? Digest { get; init; }

    /// <summary>
    /// Whether it may be folded into a summary when the channel falls behind (#122). A stopped or
    /// watching again message never is (#123, #170): "no alerts will be posted" mustn't become a count.
    /// </summary>
    public bool CanFold => Digest?.Kind is not (ChatMessageKind.Stopped or ChatMessageKind.Watching);
}

/// <summary>What a message is about, for a summary it's folded into.</summary>
public enum ChatMessageKind
{
    /// <summary>Alerts that started or turned critical (#121).</summary>
    Raised,

    /// <summary>Alerts back to normal (#123).</summary>
    Recovered,

    /// <summary>SqlVitals stopped watching servers (#123, #170). Never folded.</summary>
    Stopped,

    /// <summary>SqlVitals is watching servers again (#170). Never folded.</summary>
    Watching,
}

/// <summary>
/// A message in short, kept so that it can be folded into a summary when a channel falls behind
/// (#122): which connection, which indicators, and whether any is critical.
/// </summary>
public sealed record ChatDigest(ChatMessageKind Kind, string Connection, IReadOnlyList<string> Indicators, bool Critical);

/// <summary>An alert to post because it started or turned critical (#121).</summary>
/// <param name="NowCritical">
/// A warning the channel was already told about turned critical: "Now critical". False for an
/// alert that's new to the channel, including a warning a Critical only channel skipped.
/// </param>
public sealed record RaisedAlert(Alert Alert, bool NowCritical = false);

/// <summary>
/// A server SqlVitals stopped watching, and the alerts a channel was told about that were still
/// active then (#123, #170).
/// </summary>
public sealed record StoppedServer(StoppedWatching Stop, IReadOnlyList<Alert> ActiveAlerts);

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
                About  = $"alert on {connectionName}",
                Digest = new ChatDigest(ChatMessageKind.Raised, connectionName, [name], critical > 0),
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
            Lines  = lines,
            More   = sorted.Count - lines.Count,
            About  = $"{sorted.Count} alerts on {connectionName}",
            Digest = new ChatDigest(ChatMessageKind.Raised, connectionName, names, critical > 0),
        };
    }

    /// <summary>
    /// One message in place of the messages that piled up while a channel was busy or waiting to
    /// retry (#122): a line per connection naming its indicators, those that started or turned
    /// critical and those back to normal (#123, F24), so the team can still tell what happened
    /// where. Critical connections first.
    /// </summary>
    /// <param name="folded">The messages it replaces, in the order they were queued.</param>
    public static ChatMessage Summary(IReadOnlyList<ChatMessage> folded)
    {
        if (folded.Count == 0)
            throw new ArgumentException("There's nothing to summarise.", nameof(folded));

        static List<string> Indicators(IEnumerable<ChatDigest> digests, ChatMessageKind kind) =>
            digests.Where(d => d.Kind == kind).SelectMany(d => d.Indicators).Distinct(StringComparer.CurrentCulture).ToList();

        var byConnection = folded
            .Select(m => m.Digest).OfType<ChatDigest>()
            .GroupBy(d => d.Connection, StringComparer.CurrentCulture)
            .Select(g => (
                Connection: g.Key,
                Critical:   g.Any(d => d.Kind == ChatMessageKind.Raised && d.Critical),
                Raised:     Indicators(g, ChatMessageKind.Raised),
                Recovered:  Indicators(g, ChatMessageKind.Recovered)))
            .OrderByDescending(c => c.Critical)
            .ThenByDescending(c => c.Raised.Count > 0)
            .ToList();

        var tone  = byConnection.Any(c => c.Critical)         ? ChatTone.Critical
                  : byConnection.Any(c => c.Raised.Count > 0) ? ChatTone.Warning
                  : byConnection.Count > 0                    ? ChatTone.Good
                  : ChatTone.Neutral;
        var what  = folded.Count == 1 ? "1 more message" : $"{folded.Count} more messages";
        var were  = folded.Count == 1 ? "was" : "were";
        var title = $"📋 {what} combined";
        var text  = $"{what} {were} combined while the channel was busy. See the Alerts page in SqlVitals for the details.";

        var lines = byConnection.Take(MaxLines).Select(c => new ChatLine(
            c.Critical ? ChatTone.Critical : c.Raised.Count > 0 ? ChatTone.Warning : ChatTone.Good,
            c.Connection,
            string.Join("; ", new[]
            {
                c.Raised.Count    > 0 ? $"new or critical: {List(c.Raised)}"   : null,
                c.Recovered.Count > 0 ? $"back to normal: {List(c.Recovered)}" : null,
            }.OfType<string>()))).ToList();

        var connections = byConnection.Select(c => c.Connection).ToList();
        var fallback = connections.Count == 0
            ? $"{what} {were} combined while the channel was busy."
            : $"{what} {were} combined while the channel was busy: updates on {List(connections)}.";
        return new ChatMessage(tone, title, text, [], folded.Select(m => m.SentFrom).FirstOrDefault(s => s is not null), fallback)
        {
            Lines = lines,
            More  = byConnection.Count - lines.Count,
            About = folded.Count == 1 ? "summary of 1 message" : $"summary of {folded.Count} messages",
        };
    }

    /// <summary>
    /// The message for a connection's alerts that ended back to normal on one sample (#123), for
    /// a channel with Also post when an alert ends on: how long each lasted, its worst figure,
    /// and when it started and ended, in each reader's own time zone. Critical first, as the
    /// raised message lists them.
    /// </summary>
    /// <param name="alerts">The ended alerts, with <see cref="Alert.EndedUtc"/> set.</param>
    public static ChatMessage Recovered(string connectionName, IReadOnlyList<Alert> alerts, string pcName)
    {
        if (alerts.Count == 0)
            throw new ArgumentException("There's nothing to post.", nameof(alerts));

        var sorted = alerts.OrderByDescending(a => a.Severity).ThenBy(a => a.Indicator).ToList();
        var names  = sorted.Select(a => HealthThresholds.Info(a.Indicator).Name).ToList();
        var digest = new ChatDigest(ChatMessageKind.Recovered, connectionName, names, Critical: false);
        var good   = ChatFormatter.Emoji(ChatTone.Good);

        static string Lasted(Alert a) => AlertText.Duration(a.Duration(a.EndedUtc ?? a.StartedUtc));

        if (sorted.Count == 1)
        {
            var alert = sorted[0];
            var title = $"{good} Back to normal: {names[0]} on {connectionName}";
            var facts = new List<ChatFact> { new("Worst", AlertText.Figure(alert)) };
            if (AlertText.Context(alert) is { } context)
                facts.Add(new ChatFact("Detail", context));
            facts.Add(new ChatFact("Lasted", Lasted(alert)));
            facts.Add(new ChatFact("Started", "", alert.StartedUtc));
            facts.Add(new ChatFact("Ended", "", alert.EndedUtc ?? alert.StartedUtc));

            return new ChatMessage(ChatTone.Good, title, "", facts, SentFrom(pcName),
                                   Fallback: $"{title}. Worst {AlertText.Figure(alert)}, lasted {Lasted(alert)}.")
            {
                About  = $"end of alert on {connectionName}",
                Digest = digest,
            };
        }

        var many  = $"{good} {sorted.Count} alerts back to normal on {connectionName}";
        var lines = sorted.Take(MaxLines).Select(a => new ChatLine(
            ChatTone.Good,
            HealthThresholds.Info(a.Indicator).Name,
            $"worst {AlertText.Figure(a)}, lasted {Lasted(a)}")).ToList();
        var ended = sorted.Max(a => a.EndedUtc ?? a.StartedUtc);

        return new ChatMessage(ChatTone.Good, many, "", [new ChatFact("Ended", "", ended)], SentFrom(pcName),
                               Fallback: $"{many}: {List(names)}.")
        {
            Lines  = lines,
            More   = sorted.Count - lines.Count,
            About  = $"end of {sorted.Count} alerts on {connectionName}",
            Digest = digest,
        };
    }

    /// <summary>
    /// Why SqlVitals stopped watching, to follow "because": "the PC is going to sleep" (#170).
    /// </summary>
    public static string Why(WatchStop why) => why switch
    {
        WatchStop.StoppedByUser     => "monitoring was stopped in SqlVitals",
        WatchStop.AutomaticOff      => "Start monitoring automatically was turned off",
        WatchStop.BackgroundOff     => "another server was opened, and Monitor in background is off",
        WatchStop.NeedsSetup        => "a saved password or sign-in is missing",
        WatchStop.ConnectionRemoved => "the connection was removed from SqlVitals",
        WatchStop.AppExit           => "the app was closed",
        WatchStop.WindowsShutdown   => "Windows is shutting down",
        WatchStop.WindowsSignOut    => "the Windows user signed out",
        WatchStop.Sleep             => "the PC is going to sleep",
        _                           => "of a change in SqlVitals",
    };

    /// <summary>
    /// The message when SqlVitals stops watching servers a channel takes (#170): Stop, a setting,
    /// the connection removed, Exit, Windows shutting down or signing out, or the PC going to
    /// sleep. Says why, and lists the alerts the channel was told about that were still active
    /// (#123): their ends aren't posted, because nobody is watching any more. One message for
    /// everything that stopped at once, so it fits in the few seconds SqlVitals may have left to
    /// send it (F5). Never folded into a summary.
    /// </summary>
    /// <param name="stoppedUtc">When it stopped, shown in each reader's own time zone: the message may arrive after the PC wakes.</param>
    public static ChatMessage StoppedWatching(IReadOnlyList<StoppedServer> servers, string pcName, DateTime stoppedUtc)
    {
        if (servers.Count == 0)
            throw new ArgumentException("There's nothing to post.", nameof(servers));

        static List<string> Indicators(StoppedServer s) =>
            s.ActiveAlerts.OrderByDescending(a => a.Severity).ThenBy(a => a.Indicator)
                          .Select(a => HealthThresholds.Info(a.Indicator).Name).ToList();
        static string StillActive(StoppedServer s) =>
            (s.ActiveAlerts.Count == 1 ? "1 alert still active: " : $"{s.ActiveAlerts.Count} alerts still active: ") +
            string.Join(", ", Indicators(s));

        var names    = servers.Select(s => s.Stop.Name).ToList();
        var oneWhy   = servers.Select(s => s.Stop.Why).Distinct().Count() == 1;
        var because  = oneWhy ? $" because {Why(servers[0].Stop.Why)}" : "";
        var them     = servers.Count == 1 ? "it" : "them";
        var text     = $"SqlVitals on {pcName} stopped watching {List(names)}{because}. " +
                       $"No alerts will be posted for {them} until SqlVitals is watching again.";
        string title, about;
        var lines = new List<ChatLine>();

        if (servers.Count == 1)
        {
            title = $"⏹ SqlVitals stopped watching {names[0]}";
            about = $"stopped watching {names[0]}";
            if (servers[0].ActiveAlerts.Count > 0)
                text += $" {names[0]} had {StillActive(servers[0])}.";
        }
        else
        {
            title = $"⏹ SqlVitals stopped watching {servers.Count} servers";
            about = $"stopped watching {servers.Count} servers";
            // A line per server, when there's more to say than its name: why, when the servers
            // stopped for different reasons, and its alerts still active.
            if (!oneWhy || servers.Any(s => s.ActiveAlerts.Count > 0))
                lines = servers.Take(MaxLines).Select(s => new ChatLine(
                    ChatTone.Neutral,
                    s.Stop.Name,
                    string.Join("; ", new[]
                    {
                        oneWhy ? null : $"stopped because {Why(s.Stop.Why)}",
                        s.ActiveAlerts.Count > 0 ? StillActive(s) : null,
                    }.OfType<string>()))).ToList();
        }

        return new ChatMessage(ChatTone.Neutral, title, text, [new ChatFact("Stopped", "", stoppedUtc)], SentFrom: null,
                               Fallback: text)
        {
            Lines  = lines,
            More   = lines.Count == 0 ? 0 : servers.Count - lines.Count,
            About  = about,
            Digest = new ChatDigest(ChatMessageKind.Stopped, List(names), servers.SelectMany(Indicators).ToList(), Critical: false),
        };
    }

    /// <summary>
    /// The message when SqlVitals starts watching servers a channel takes (#170): at startup, when
    /// someone presses Start, or when the PC wakes. Says how long each server wasn't watched,
    /// from its last sample, so the team knows how long the channel's quiet meant nothing. A
    /// server never watched before (or whose history was cleared) says so instead. Never folded.
    /// </summary>
    public static ChatMessage WatchingAgain(IReadOnlyList<WatchedConnection> servers, string pcName, DateTime nowUtc)
    {
        if (servers.Count == 0)
            throw new ArgumentException("There's nothing to post.", nameof(servers));

        static string Gap(WatchedConnection s, DateTime now) => AlertText.Duration(now - s.LastWatchedUtc!.Value);

        var names  = servers.Select(s => s.Name).ToList();
        var again  = servers.Any(s => s.LastWatchedUtc is not null);
        var digest = new ChatDigest(ChatMessageKind.Watching, List(names), [], Critical: false);

        if (servers.Count == 1)
        {
            var server = servers[0];
            if (server.LastWatchedUtc is not { } last)
            {
                var first = $"▶ SqlVitals is watching {server.Name}";
                return new ChatMessage(ChatTone.Good, first, $"SqlVitals on {pcName} started watching {server.Name}.", [], SentFrom: null)
                {
                    About  = $"watching {server.Name}",
                    Digest = digest,
                };
            }

            var gap   = Gap(server, nowUtc);
            var title = $"▶ SqlVitals is watching {server.Name} again";
            var text  = $"SqlVitals on {pcName} is watching {server.Name} again, after {gap} without monitoring.";
            return new ChatMessage(ChatTone.Good, title, text,
                                   [new ChatFact("Not watched for", gap), new ChatFact("Last watched", "", last)], SentFrom: null)
            {
                About  = $"watching {server.Name} again",
                Digest = digest,
            };
        }

        var many  = again ? $"▶ SqlVitals is watching {servers.Count} servers again" : $"▶ SqlVitals is watching {servers.Count} servers";
        var says  = again ? $"SqlVitals on {pcName} is watching {List(names)} again." : $"SqlVitals on {pcName} started watching {List(names)}.";
        var lines = servers.Take(MaxLines).Select(s => s.LastWatchedUtc is { } last
            ? new ChatLine(ChatTone.Good, s.Name, $"not watched for {Gap(s, nowUtc)}", last)
            : new ChatLine(ChatTone.Good, s.Name, "watched for the first time")).ToList();

        return new ChatMessage(ChatTone.Good, many, says, [], SentFrom: null)
        {
            Lines  = again ? lines : [],
            More   = again ? servers.Count - lines.Count : 0,
            About  = again ? $"watching {servers.Count} servers again" : $"watching {servers.Count} servers",
            Digest = digest,
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
