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
                json.WriteStartObject();
                json.WriteString("type", "mrkdwn");
                json.WriteString("text", Fit($"*{SlackEscape(fact.Title)}*\n{SlackEscape(fact.Value)}", 2000));
                json.WriteEndObject();
            }
            json.WriteEndArray();
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

        if (message.Facts.Count > 0)
        {
            json.WriteStartObject();
            json.WriteString("type", "FactSet");
            json.WriteStartArray("facts");
            foreach (var fact in message.Facts)
            {
                json.WriteStartObject();
                json.WriteString("title", fact.Title);
                json.WriteString("value", fact.Value);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

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
