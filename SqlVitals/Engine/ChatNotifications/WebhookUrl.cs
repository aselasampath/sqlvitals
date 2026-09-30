namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// The result of checking a webhook URL before it's saved: an error that blocks the save, or a
/// warning that doesn't. <see cref="Detected"/> is the service the URL belongs to, when known.
/// </summary>
public sealed record WebhookUrlCheck(string? Error, string? Warning, ChatService? Detected)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Checks, compares and masks Slack and Microsoft Teams webhook URLs (#120). Pure, so every rule
/// is unit tested.
/// </summary>
public static class WebhookUrl
{
    /// <summary>The wiki's Teams setup steps, linked from the Office 365 connector error.</summary>
    public const string TeamsSetupLink = "https://github.com/aselasampath/sqlvitals/blob/main/docs/Chat-Notifications.md#microsoft-teams";

    public const int MaxLength = 2048;

    private const string SlackHost = "hooks.slack.com";

    // Teams "Workflows" (Power Automate) webhook hosts. Microsoft has moved these before, so an
    // unknown host is allowed with a warning rather than refused.
    private static readonly string[] TeamsHostSuffixes = [".logic.azure.com", ".api.powerplatform.com"];

    /// <summary>The service a URL belongs to, from its host; null when it's neither (or not a URL).</summary>
    public static ChatService? Detect(string? url)
    {
        if (!TryParse(url, out var uri))
            return null;
        if (IsHost(uri, SlackHost))
            return ChatService.Slack;
        if (TeamsHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            return ChatService.Teams;
        return null;
    }

    /// <summary>
    /// Checks a URL for a channel of <paramref name="service"/>. <paramref name="others"/> are the
    /// other saved channels (the one being edited, <paramref name="selfId"/>, is skipped), so the
    /// same URL can't be saved twice.
    /// </summary>
    public static WebhookUrlCheck Check(string? url, ChatService service, IEnumerable<ChatChannel> others, Guid selfId)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return Fail("Paste the channel's webhook URL. It starts with https://.");
        if (text.Length > MaxLength)
            return Fail($"That's too long for a webhook URL (over {MaxLength} characters). Copy it again from Slack or Teams.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return Fail("That isn't a whole URL. Paste the webhook URL, starting with https://.");
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return Fail("The webhook URL must start with https://, so alerts are sent encrypted.");

        // The older Teams "Incoming Webhook" connector, which Microsoft is retiring.
        if (uri.Host.EndsWith(".webhook.office.com", StringComparison.OrdinalIgnoreCase) ||
            (IsHost(uri, "outlook.office.com") && uri.AbsolutePath.StartsWith("/webhook", StringComparison.OrdinalIgnoreCase)))
            return Fail("This is an Office 365 connector URL, which Microsoft is retiring, so SqlVitals doesn't use it. " +
                        $"Create a Teams workflow webhook instead: {TeamsSetupLink}");

        var detected = Detect(text);
        string? warning = null;

        if (IsHost(uri, SlackHost))
        {
            if (service != ChatService.Slack)
                return Fail("This is a Slack URL. Choose Slack as the service.", detected);
            if (!uri.AbsolutePath.StartsWith("/services/", StringComparison.OrdinalIgnoreCase))
                return Fail("This is a Slack Workflow Builder link, which doesn't take SqlVitals' messages. " +
                            "Use an Incoming Webhook URL: it starts with https://hooks.slack.com/services/.", detected);
        }
        else if (service == ChatService.Slack)
        {
            return Fail("A Slack webhook URL starts with https://hooks.slack.com/services/.", detected);
        }
        else if (detected is null)
        {
            warning = "SqlVitals doesn't recognise this host as a Teams workflow. You can save it, " +
                      "then send a test message to check it reaches the channel.";
        }

        var key = Normalize(uri);
        var duplicate = others.FirstOrDefault(c => c.Id != selfId && TryParse(c.WebhookUrl, out var other) && Normalize(other) == key);
        if (duplicate is not null)
            return Fail($"This URL is already used by the channel \"{duplicate.Name}\". Each channel needs its own webhook.", detected);

        return new WebhookUrlCheck(null, warning, detected);
    }

    /// <summary>
    /// The URL as compared for duplicates: scheme and host in lower case, no default port, no
    /// trailing "/" on the path. Path and query are kept as they are; they're case-sensitive.
    /// </summary>
    public static string Normalize(Uri uri)
    {
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : string.Empty;
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}{uri.Query}";
    }

    /// <summary>
    /// The URL as Settings shows it once saved: the host and the last 4 characters,
    /// "hooks.slack.com/…/x7Qa". Enough to tell two channels apart, useless for posting.
    /// </summary>
    public static string Mask(string? url)
    {
        if (!TryParse(url, out var uri))
            return string.IsNullOrWhiteSpace(url) ? string.Empty : "…";

        var rest = (uri.AbsolutePath + uri.Query).TrimEnd('/');
        var tail = rest.Length > 8 ? rest[^4..] : string.Empty;
        return tail.Length == 0 ? $"{uri.Host}/…" : $"{uri.Host}/…/{tail}";
    }

    private static bool TryParse(string? url, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri!) && uri.Host.Length > 0;
    }

    private static bool IsHost(Uri uri, string host) => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase);

    private static WebhookUrlCheck Fail(string error, ChatService? detected = null) => new(error, null, detected);
}
