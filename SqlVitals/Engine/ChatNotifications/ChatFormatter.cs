using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>Writes a <see cref="ChatMessage"/> as the JSON a channel's webhook takes.</summary>
public static class ChatFormatter
{
    // Emoji and "≥" stay readable in the JSON (and in the expected files the tests compare with).
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(ChatMessage message, ChatService service) =>
        service == ChatService.Teams ? Teams(message) : Slack(message);

    // Slack's limits: 150 characters in a header, 3000 in a section's text.
    private const int SlackHeaderMax  = 150;
    private const int SlackSectionMax = 3000;

    /// <summary>
    /// Slack Block Kit, with <c>text</c> as the plain fallback Slack's own notifications show.
    /// </summary>
    public static string Slack(ChatMessage message) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("text", SlackEscape(message.FallbackText));
        json.WriteStartArray("blocks");

        json.WriteStartObject();
        json.WriteString("type", "header");
        json.WriteStartObject("text");
        json.WriteString("type", "plain_text");
        json.WriteString("text", Fit(message.Title, SlackHeaderMax));
        json.WriteBoolean("emoji", true);
        json.WriteEndObject();
        json.WriteEndObject();

        if (message.Text.Length > 0)
        {
            json.WriteStartObject();
            json.WriteString("type", "section");
            WriteMrkdwn(json, "text", Fit(SlackEscape(message.Text), SlackSectionMax));
            json.WriteEndObject();
        }

        if (message.Facts.Count > 0)
        {
            json.WriteStartObject();
            json.WriteString("type", "section");
            json.WriteStartArray("fields");
            foreach (var fact in message.Facts.Take(10))   // Slack takes 10 fields per section
            {
                var value = fact.TimeUtc is { } time ? SlackTime(time) : SlackEscape(fact.Value);
                json.WriteStartObject();
                json.WriteString("type", "mrkdwn");
                json.WriteString("text", Fit($"*{SlackEscape(fact.Title)}*\n{value}", 2000));
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        if (message.Lines.Count > 0)
        {
            var lines = message.Lines.Select(l =>
                $"{Emoji(l.Tone)} *{SlackEscape(l.Label)}* {SlackEscape(l.Text)}" +
                (l.SinceUtc is { } since ? $" · since {SlackTime(since)}" : ""));
            if (message.More > 0)
                lines = lines.Append($"…and {message.More} more");

            json.WriteStartObject();
            json.WriteString("type", "section");
            WriteMrkdwn(json, "text", Fit(string.Join("\n", lines), SlackSectionMax));
            json.WriteEndObject();
        }

        if (message.SentFrom is { } from)
        {
            json.WriteStartObject();
            json.WriteString("type", "context");
            json.WriteStartArray("elements");
            json.WriteStartObject();
            json.WriteString("type", "mrkdwn");
            json.WriteString("text", SlackEscape(from));
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>
    /// An Adaptive Card 1.4 in the envelope Teams workflows take. Teams doesn't accept plain text.
    /// </summary>
    public static string Teams(ChatMessage message) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("type", "message");
        json.WriteStartArray("attachments");
        json.WriteStartObject();
        json.WriteString("contentType", "application/vnd.microsoft.card.adaptive");
        json.WriteNull("contentUrl");
        json.WriteStartObject("content");
        json.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
        json.WriteString("type", "AdaptiveCard");
        json.WriteString("version", "1.4");
        json.WriteStartArray("body");

        json.WriteStartObject();
        json.WriteString("type", "TextBlock");
        json.WriteString("text", message.Title);
        json.WriteString("weight", "Bolder");
        json.WriteString("size", "Medium");
        if (TeamsColour(message.Tone) is { } colour)
            json.WriteString("color", colour);
        json.WriteBoolean("wrap", true);
        json.WriteEndObject();

        if (message.Text.Length > 0)
        {
            json.WriteStartObject();
            json.WriteString("type", "TextBlock");
            json.WriteString("text", message.Text);
            json.WriteBoolean("wrap", true);
            json.WriteEndObject();
        }

        var plainFacts = message.Facts.Where(f => f.TimeUtc is null).ToList();
        if (plainFacts.Count > 0)
        {
            json.WriteStartObject();
            json.WriteString("type", "FactSet");
            json.WriteStartArray("facts");
            foreach (var fact in plainFacts)
            {
                json.WriteStartObject();
                json.WriteString("title", fact.Title);
                json.WriteString("value", fact.Value);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        // Times go in TextBlocks: Microsoft documents the DATE and TIME functions for TextBlock text.
        foreach (var fact in message.Facts.Where(f => f.TimeUtc is not null))
            WriteTextBlock(json, $"**{fact.Title}** {TeamsTime(fact.TimeUtc!.Value)}");

        foreach (var line in message.Lines)
            WriteTextBlock(json, $"{Emoji(line.Tone)} **{line.Label}** {line.Text}" +
                                 (line.SinceUtc is { } since ? $" · since {TeamsTime(since)}" : ""));

        if (message.More > 0)
            WriteTextBlock(json, $"…and {message.More} more", subtle: true);

        if (message.SentFrom is { } from)
        {
            json.WriteStartObject();
            json.WriteString("type", "TextBlock");
            json.WriteString("text", from);
            json.WriteBoolean("isSubtle", true);
            json.WriteString("size", "Small");
            json.WriteBoolean("wrap", true);
            json.WriteEndObject();
        }

        json.WriteEndArray();   // body
        json.WriteEndObject();  // content
        json.WriteEndObject();  // attachment
        json.WriteEndArray();
        json.WriteEndObject();
    });

    /// <summary>
    /// Escapes the three characters Slack gives a meaning to. Without it, a connection named
    /// "&lt;!channel&gt;" would notify everyone in the channel on every alert.
    /// </summary>
    public static string SlackEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>🔴 Critical, 🟠 Warning, ✅ Good; nothing for a neutral message.</summary>
    public static string Emoji(ChatTone tone) => tone switch
    {
        ChatTone.Critical => "🔴",
        ChatTone.Warning  => "🟠",
        ChatTone.Good     => "✅",
        _                 => "",
    };

    /// <summary>
    /// Slack's date token: each reader sees the time in their own time zone ("Today 10:42"), and
    /// clients that can't show it fall back to UTC.
    /// </summary>
    internal static string SlackTime(DateTime utc)
    {
        var at = Utc(utc);
        return $"<!date^{new DateTimeOffset(at).ToUnixTimeSeconds()}^{{date_short_pretty}} {{time}}|{at:yyyy-MM-dd HH:mm} UTC>";
    }

    /// <summary>Adaptive Card DATE and TIME functions: Teams shows them in each reader's own time zone.</summary>
    internal static string TeamsTime(DateTime utc)
    {
        var iso = Utc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return "{{DATE(" + iso + ", SHORT)}} {{TIME(" + iso + ")}}";
    }

    // Alert times are UTC; one that lost its kind on the way is taken as UTC, not as local time.
    private static DateTime Utc(DateTime time) =>
        time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);

    private static void WriteTextBlock(Utf8JsonWriter json, string text, bool subtle = false)
    {
        json.WriteStartObject();
        json.WriteString("type", "TextBlock");
        json.WriteString("text", text);
        json.WriteBoolean("wrap", true);
        if (subtle)
            json.WriteBoolean("isSubtle", true);
        json.WriteEndObject();
    }

    private static string? TeamsColour(ChatTone tone) => tone switch
    {
        ChatTone.Critical => "Attention",
        ChatTone.Warning  => "Warning",
        ChatTone.Good     => "Good",
        _                 => null,
    };

    private static void WriteMrkdwn(Utf8JsonWriter json, string property, string text)
    {
        json.WriteStartObject(property);
        json.WriteString("type", "mrkdwn");
        json.WriteString("text", text);
        json.WriteEndObject();
    }

    private static string Fit(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
            write(json);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
