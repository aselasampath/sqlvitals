using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Posts to the Slack and Microsoft Teams channels saved in Settings (#108). Lives with the app,
/// not the window, so it carries on while the window is hidden in the notification area.
///
/// #120: the channels and Send test message. Posting alerts comes in #121, retries and the
/// Failing status in #122, and the end of alerts in #123.
///
/// Used on the UI thread. Sending doesn't block it: the request is awaited.
/// </summary>
public sealed class ChatNotificationService : IDisposable
{
    private readonly IWebhookSender _sender;
    private readonly string         _pcName;
    private readonly Action<string> _log;

    private IReadOnlyList<ChatChannel> _channels = [];
    private readonly Dictionary<Guid, ChannelStatus> _status = new();

    /// <param name="log">Where posts and failures are written: the app log, or a list in tests. Never given a URL.</param>
    public ChatNotificationService(IWebhookSender sender, string? pcName = null, Action<string>? log = null)
    {
        _sender = sender;
        _pcName = pcName ?? Environment.MachineName;
        _log    = log ?? AppLog.Info;
    }

    /// <summary>A channel's status changed, e.g. a test message reached it. Raised on the calling thread.</summary>
    public event Action<Guid>? StatusChanged;

    /// <summary>The channels as last applied, in the order they were saved.</summary>
    public IReadOnlyList<ChatChannel> Channels => _channels;

    /// <summary>
    /// Takes the saved channels, after a save or at startup. A store that couldn't be read (another
    /// program had settings.dat open) is ignored: it would look like "no channels" and stop every one.
    /// </summary>
    public void Apply(ConnectionStore store)
    {
        if (store.ReadFailed)
            return;

        _channels = store.ChatChannels.Select(c => c.Clone()).ToList();
        var ids = _channels.Select(c => c.Id).ToHashSet();
        foreach (var gone in _status.Keys.Where(id => !ids.Contains(id)).ToList())
            _status.Remove(gone);
    }

    public ChannelStatus Status(Guid channelId) =>
        _status.TryGetValue(channelId, out var status) ? status : ChannelStatus.NeverUsed;

    /// <summary>
    /// Posts the test message to a saved channel, once, without retrying, and says how it went.
    /// Works while the channel is off, so it can be checked before it's turned on.
    /// </summary>
    public async Task<WebhookResult> SendTestAsync(Guid channelId, CancellationToken cancel = default)
    {
        var channel = _channels.FirstOrDefault(c => c.Id == channelId)
                      ?? throw new InvalidOperationException("That chat channel isn't saved any more.");

        var result = await SendAsync(channel, ChatMessageBuilder.Test(_pcName), "test message", cancel);
        if (result.IsSuccess)
            MarkPosted(channel.Id);
        return result;
    }

    private async Task<WebhookResult> SendAsync(ChatChannel channel, ChatMessage message, string what, CancellationToken cancel)
    {
        WebhookResult result;
        if (!Uri.TryCreate(channel.WebhookUrl, UriKind.Absolute, out var url))
        {
            result = new WebhookResult(WebhookOutcome.Rejected, null, "the saved webhook URL isn't valid", TimeSpan.Zero);
        }
        else
        {
            try
            {
                result = await _sender.SendAsync(url, ChatFormatter.Format(message, channel.Service), channel.Service, cancel);
            }
            catch (Exception ex)
            {
                // The sender doesn't throw for a failed post; this is a bug, logged by type only.
                result = new WebhookResult(WebhookOutcome.Unreachable, null, $"the request failed ({ex.GetType().Name})", TimeSpan.Zero);
            }
        }

        _log($"Chat \"{channel.Name}\" ({ChatServices.Name(channel.Service)}): {what} {result.LogText()}");
        return result;
    }

    private void MarkPosted(Guid channelId)
    {
        _status[channelId] = new ChannelStatus(DateTime.UtcNow);
        StatusChanged?.Invoke(channelId);
    }

    public void Dispose() => (_sender as IDisposable)?.Dispose();
}
