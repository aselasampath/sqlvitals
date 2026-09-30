namespace SqlVitals.Engine.ChatNotifications;

/// <summary>The chat service a webhook posts to (#108).</summary>
public enum ChatService
{
    Slack,
    Teams,
}

public static class ChatServices
{
    /// <summary>"Slack", "Microsoft Teams".</summary>
    public static string Name(ChatService service) => service == ChatService.Teams ? "Microsoft Teams" : "Slack";
}

/// <summary>
/// A Slack or Microsoft Teams channel SqlVitals posts to through an incoming webhook (#120).
/// Saved inside the DPAPI-encrypted part of settings.dat, because the webhook URL is a secret:
/// anyone who has it can post to the channel.
///
/// A class rather than a record on purpose: a record's generated <see cref="object.ToString"/>
/// prints every property, and would put the URL in any log line or test message that shows it.
/// </summary>
public sealed class ChatChannel
{
    public Guid        Id         { get; set; } = Guid.NewGuid();

    /// <summary>What the user calls it, e.g. "DBA team (Teams)". Shown in Settings, the log and notifications.</summary>
    public string      Name       { get; set; } = string.Empty;

    public ChatService Service    { get; set; } = ChatService.Slack;

    /// <summary>The secret. Never logged, shown only masked unless the user asks (see <see cref="WebhookUrl.Mask"/>).</summary>
    public string      WebhookUrl { get; set; } = string.Empty;

    /// <summary>Off pauses the channel without removing it.</summary>
    public bool        Enabled    { get; set; } = true;

    /// <summary>
    /// Every monitored connection's alerts (#121), including connections added later. Off, only
    /// <see cref="ConnectionIds"/>. Missing from files written before #121, where it reads as on.
    /// </summary>
    public bool        AllConnections { get; set; } = true;

    /// <summary>The connections chosen when <see cref="AllConnections"/> is off.</summary>
    public List<Guid>  ConnectionIds  { get; set; } = new();

    /// <summary>Post critical alerts only (#121); off posts warnings and critical.</summary>
    public bool        CriticalOnly   { get; set; }

    /// <summary>Whether this channel gets a connection's alerts.</summary>
    public bool Includes(Guid connectionId) => AllConnections || ConnectionIds.Contains(connectionId);

    public ChatChannel Clone()
    {
        var copy = (ChatChannel)MemberwiseClone();
        copy.ConnectionIds = new List<Guid>(ConnectionIds ?? []);
        return copy;
    }

    /// <summary>The name and the masked URL, never the URL itself.</summary>
    public override string ToString() => $"{Name} ({ChatServices.Name(Service)}, {ChatNotifications.WebhookUrl.Mask(WebhookUrl)})";
}
