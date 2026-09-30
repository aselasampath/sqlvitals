namespace SqlVitals.Engine.Activity;

/// <summary>One setting, or one field of a connection, as text.</summary>
public sealed record SettingValue(string Name, string Value);

/// <summary>A saved connection's fields as text, with the name it's shown under.</summary>
public sealed record ConnectionSnapshot(Guid Id, string Name, IReadOnlyList<SettingValue> Fields);

/// <summary>Everything in the settings file as text, to compare before and after a save (#115).</summary>
/// <param name="ChatChannels">The Slack and Teams channels (#120), each with its fields, like a connection.</param>
public sealed record SettingsSnapshot(IReadOnlyList<SettingValue>       Settings,
                                      IReadOnlyList<ConnectionSnapshot> Connections,
                                      IReadOnlyList<ConnectionSnapshot>? ChatChannels = null)
{
    public static SettingsSnapshot Empty { get; } = new([], []);

    public IReadOnlyList<ConnectionSnapshot> Channels => ChatChannels ?? [];
}

/// <summary>
/// Turns one save of the settings into activity log rows: a row per setting that changed, per
/// connection added or removed, and per field of a connection that changed. Done in one place, on
/// the whole file before and after, so a setting added later can't be missed.
///
/// A secret is never written: for any setting or field named in <see cref="SecretNames"/> the row
/// only says it changed. A value that didn't change writes nothing.
/// </summary>
public static class SettingsDiff
{
    /// <summary>What a row says in place of a secret's values.</summary>
    public const string SecretChanged = "changed";

    /// <summary>
    /// Settings and connection fields whose values are never logged. The additional parameters
    /// are here too: they are a piece of connection string, which may carry anything.
    /// </summary>
    public static IReadOnlySet<string> SecretNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "Additional parameters", "Connection string", "Access token", "Webhook URL",
    };

    public static bool IsSecret(string name) => SecretNames.Contains(name);

    /// <param name="summarize">A connection's one-line description for Connection added, e.g. "sql01 · Sales · SQL login".</param>
    public static IReadOnlyList<ActivityEntry> Between(SettingsSnapshot before, SettingsSnapshot after, string source,
                                                       DateTime nowUtc, Func<ConnectionSnapshot, string?>? summarize = null)
    {
        var rows = new List<ActivityEntry>();

        foreach (var (name, old, now) in Changes(before.Settings, after.Settings))
            rows.Add(Changed(ActivityEvents.SettingChanged, nowUtc, null, null, source, name, old, now));

        var oldConnections = before.Connections.ToDictionary(c => c.Id);
        var newConnections = after.Connections.ToDictionary(c => c.Id);

        foreach (var conn in after.Connections.Where(c => !oldConnections.ContainsKey(c.Id)))
            rows.Add(new ActivityEntry(nowUtc, ActivityEvents.ConnectionAdded, conn.Id, conn.Name, source,
                                       Detail: summarize?.Invoke(conn)));

        foreach (var conn in after.Connections.Where(c => oldConnections.ContainsKey(c.Id)))
            foreach (var (name, old, now) in Changes(oldConnections[conn.Id].Fields, conn.Fields))
                rows.Add(Changed(ActivityEvents.ConnectionEdited, nowUtc, conn.Id, conn.Name, source, name, old, now));

        foreach (var conn in before.Connections.Where(c => !newConnections.ContainsKey(c.Id)))
            rows.Add(new ActivityEntry(nowUtc, ActivityEvents.ConnectionRemoved, conn.Id, conn.Name, source));

        // Chat channels aren't connections, so the Connection column stays empty: the channel is
        // named in the setting ("DBA team: Service") or, when added or removed, in the detail.
        var oldChannels = before.Channels.ToDictionary(c => c.Id);
        var newChannels = after.Channels.ToDictionary(c => c.Id);

        foreach (var channel in after.Channels.Where(c => !oldChannels.ContainsKey(c.Id)))
            rows.Add(new ActivityEntry(nowUtc, ActivityEvents.ChatChannelAdded, null, null, source, Detail: channel.Name));

        foreach (var channel in after.Channels.Where(c => oldChannels.ContainsKey(c.Id)))
            foreach (var (name, old, now) in Changes(oldChannels[channel.Id].Fields, channel.Fields))
                rows.Add(IsSecret(name)
                    ? new ActivityEntry(nowUtc, ActivityEvents.ChatChannelEdited, null, null, source, $"{channel.Name}: {name}", Detail: SecretChanged)
                    : new ActivityEntry(nowUtc, ActivityEvents.ChatChannelEdited, null, null, source, $"{channel.Name}: {name}", old, now));

        foreach (var channel in before.Channels.Where(c => !newChannels.ContainsKey(c.Id)))
            rows.Add(new ActivityEntry(nowUtc, ActivityEvents.ChatChannelRemoved, null, null, source, Detail: channel.Name));

        return rows;
    }

    // Every name whose value differs, in the order they're listed; a name on one side only counts.
    private static IEnumerable<(string Name, string? Old, string? New)> Changes(
        IReadOnlyList<SettingValue> before, IReadOnlyList<SettingValue> after)
    {
        var old = before.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Last().Value);
        var now = after.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Last().Value);

        foreach (var name in after.Select(s => s.Name).Concat(before.Select(s => s.Name)).Distinct())
        {
            old.TryGetValue(name, out var o);
            now.TryGetValue(name, out var n);
            if (!string.Equals(o, n, StringComparison.Ordinal))
                yield return (name, o, n);
        }
    }

    private static ActivityEntry Changed(string evt, DateTime nowUtc, Guid? connectionId, string? connectionName,
                                         string source, string name, string? old, string? now) =>
        IsSecret(name)
            ? new ActivityEntry(nowUtc, evt, connectionId, connectionName, source, name, Detail: SecretChanged)
            : new ActivityEntry(nowUtc, evt, connectionId, connectionName, source, name, old, now);
}
