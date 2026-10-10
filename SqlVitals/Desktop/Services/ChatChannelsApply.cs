using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Services;

/// <summary>The app's chat channels from its settings file (#120), for the Engine service that posts to them (#172).</summary>
public static class ChatChannelsApply
{
    /// <summary>
    /// Applies the saved channels and connection names (<see cref="ChatNotificationService.Apply(IReadOnlyList{ChatChannel}, IReadOnlyDictionary{Guid, string})"/>).
    /// A store that couldn't be read (another program had settings.dat open) is ignored: it would
    /// look like "no channels".
    /// </summary>
    public static void Apply(this ChatNotificationService chat, ConnectionStore store)
    {
        if (store.ReadFailed)
            return;
        chat.Apply(store.ChatChannels, store.Connections.ToDictionary(c => c.Id, c => c.DisplayName));
    }
}
