using System.Globalization;
using System.Windows.Input;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Home;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A chat channel's card in Settings → Alerts &amp; chat (#131): never the URL, only its mask. The
/// view colours its dot and label from <see cref="Level"/>, and its status line red while
/// <see cref="IsFailing"/> (#190).
/// </summary>
public sealed record ChatChannelCard(
    Guid                  Id,
    string                Name,
    string                Detail,
    IReadOnlyList<string> Tags,
    string                Label,
    TileStatus            Level,
    string                StatusText,
    bool                  IsFailing)
{
    public string AutomationName => $"{Name}, {Label.ToLower(CultureInfo.CurrentCulture)}, {StatusText}";
    public string TestName       => $"Send a test message to {Name}";
    public string EditName       => $"Edit {Name}";

    /// <summary>Send test; Alerts &amp; chat's view model sets it.</summary>
    public ICommand? SendTestCommand { get; init; }

    /// <summary>Edit; Alerts &amp; chat's view model sets it.</summary>
    public ICommand? EditCommand { get; init; }

    /// <param name="connections">Which connections it posts for, as the test message says it.</param>
    public static ChatChannelCard From(ChatChannel channel, ChannelStatus status, string connections, DateTime nowLocal)
    {
        // Off first: a paused channel isn't failing, it's not being used.
        var (label, level) = !channel.Enabled ? ("OFF", TileStatus.Off)
                           : status.IsFailing ? ("FAILING", TileStatus.Critical)
                           : status.LastPostedUtc is not null ? ("WORKING", TileStatus.Healthy)
                           : ("NOT USED YET", TileStatus.Off);

        var tags = new List<string>
        {
            connections.Length == 0 ? connections : char.ToUpper(connections[0], CultureInfo.CurrentCulture) + connections[1..],
            channel.CriticalOnly ? "Critical only" : "Warnings and critical",
        };
        if (!channel.PostEnds)
            tags.Add("No end messages");
        if (channel.Heartbeat is { IsOn: true } heartbeat)
            tags.Add(HeartbeatTag(heartbeat, !string.IsNullOrWhiteSpace(channel.HeartbeatPingUrl)));

        return new ChatChannelCard(
            channel.Id,
            channel.Name,
            $"{ChatServices.Name(channel.Service)} · {WebhookUrl.Mask(channel.WebhookUrl)}",
            tags,
            label,
            level,
            status.Text(nowLocal),
            status.IsFailing);
    }

    /// <summary>"Heartbeat daily 09:00", "Heartbeat every 6 h + ping" (#170).</summary>
    internal static string HeartbeatTag(HeartbeatSchedule heartbeat, bool pings) =>
        "Heartbeat " +
        (heartbeat.Every == HeartbeatEvery.Daily ? $"daily {HeartbeatSchedule.Clock(heartbeat.At)}"
         : heartbeat.Hours == 1                  ? "hourly"
         : $"every {heartbeat.Hours} h") +
        (pings ? " + ping" : "");
}
