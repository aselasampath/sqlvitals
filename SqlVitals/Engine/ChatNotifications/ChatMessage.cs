namespace SqlVitals.Engine.ChatNotifications;

/// <summary>How a message is coloured: the Teams title colour, and the tone the emoji in its title carries.</summary>
public enum ChatTone
{
    Neutral,
    Warning,
    Critical,
    Good,
}

/// <summary>A name and value shown side by side: a Slack section field, a Teams FactSet row.</summary>
public sealed record ChatFact(string Title, string Value);

/// <summary>
/// One message to a chat channel, before it's written as Slack Block Kit or a Teams Adaptive
/// Card (see <see cref="ChatFormatter"/>). Built by <see cref="ChatMessageBuilder"/>; it never
/// holds a connection string, login, password, query text or webhook URL.
/// </summary>
/// <param name="Title">The first line, e.g. "SqlVitals test message".</param>
/// <param name="Text">The body, one paragraph.</param>
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
}

/// <summary>Builds the messages SqlVitals posts, in the same words as the rest of the app.</summary>
public static class ChatMessageBuilder
{
    /// <summary>"Sent from SqlVitals on PC01": the product and the PC, never the Windows user (#108, D9).</summary>
    public static string SentFrom(string pcName) => $"Sent from SqlVitals on {pcName}";

    /// <summary>The message Send test message posts (#120).</summary>
    public static ChatMessage Test(string pcName) => new(
        ChatTone.Neutral,
        "SqlVitals test message",
        $"Test message from SqlVitals on {pcName}. Alerts will be posted here.",
        [],
        SentFrom: null);
}
