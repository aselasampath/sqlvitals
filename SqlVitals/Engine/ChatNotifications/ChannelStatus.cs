using System.Globalization;

namespace SqlVitals.Engine.ChatNotifications;

/// <summary>
/// What a chat channel's row in Settings says about it (#120): "Never used" or "Last posted
/// 10:42". Kept in memory, so it starts again at "Never used" each time SqlVitals starts.
/// </summary>
/// <param name="LastPostedUtc">When a message last reached the channel (or its Teams workflow).</param>
public sealed record ChannelStatus(DateTime? LastPostedUtc)
{
    public static ChannelStatus NeverUsed { get; } = new((DateTime?)null);

    /// <param name="nowLocal">This PC's time now, so a post from another day shows its date.</param>
    public string Text(DateTime nowLocal)
    {
        if (LastPostedUtc is not { } utc)
            return "Never used";

        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.Date == nowLocal.Date
            ? $"Last posted {local.ToString("HH:mm", CultureInfo.CurrentCulture)}"
            : $"Last posted {local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}";
    }
}
