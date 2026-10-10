using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Service;

/// <summary>An app connection as <see cref="CollectorMatch"/> compares it: its id, server and database label.</summary>
public sealed record AppConnection(Guid Id, string Server, string Database);

/// <summary>
/// Which of a collector's servers are the app's connections (#172), so the app reads their
/// history from the collector's file and stops collecting them itself. A server the app handed
/// over has the app's own id; one added on the collector by its command line is matched by
/// server and database, ignoring case, the most recently seen first. Each collector server is
/// one app connection's at most.
/// </summary>
public static class CollectorMatch
{
    /// <returns>App connection id → the id the collector's file has for it.</returns>
    public static IReadOnlyDictionary<Guid, Guid> Match(IEnumerable<AppConnection> app, IReadOnlyList<StoredConnection> collector)
    {
        var connections = app.ToList();
        var matched = new Dictionary<Guid, Guid>();
        var taken   = new HashSet<Guid>();

        // Same id first: a handed-over server is that connection, whatever it's called now.
        var ids = collector.Select(c => c.Id).ToHashSet();
        foreach (var conn in connections.Where(c => ids.Contains(c.Id)))
        {
            matched[conn.Id] = conn.Id;
            taken.Add(conn.Id);
        }

        foreach (var conn in connections.Where(c => !matched.ContainsKey(c.Id)))
        {
            var same = collector
                .Where(c => !taken.Contains(c.Id) && !connections.Any(a => a.Id == c.Id) &&
                            string.Equals(c.Server.Trim(), conn.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.Database.Trim(), conn.Database.Trim(), StringComparison.OrdinalIgnoreCase))
                .MaxBy(c => c.LastSeenUtc);
            if (same is null)
                continue;
            matched[conn.Id] = same.Id;
            taken.Add(same.Id);
        }
        return matched;
    }
}
