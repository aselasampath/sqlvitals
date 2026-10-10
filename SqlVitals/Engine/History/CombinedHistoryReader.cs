using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.History;

/// <summary>
/// One history file read alongside the app's own (#172): the collector service's on this PC, or a
/// collector's share copy from another. <paramref name="Ids"/> maps an app connection to the id
/// the file knows it by; null when the file uses the app's ids (a server the app handed over).
/// </summary>
/// <param name="Name">Who wrote it, for messages: "the collector service".</param>
public sealed record HistorySource(IHistoryReader Reader, string Name, IReadOnlyDictionary<Guid, Guid>? Ids = null)
{
    /// <summary>The id this file has for an app connection; null when it has none.</summary>
    public Guid? IdFor(Guid appId) => Ids is null ? appId : Ids.TryGetValue(appId, out var id) ? id : null;

    /// <summary>The app connection a file's id stands for: itself when it isn't one of the app's.</summary>
    public Guid AppIdFor(Guid fileId) =>
        Ids?.FirstOrDefault(p => p.Value == fileId) is { Key: var app } && app != Guid.Empty ? app : fileId;
}

/// <summary>
/// The app's history and the collectors' as one (#172): a server the service watched for part of
/// a range has its samples, scores and alerts from both files, in time order, under the app's
/// connection id. The app's file is the primary: its failures are the caller's, as before. A
/// collector's file that can't be read (a share that's down, a newer schema) is reported and left
/// out, so the app's own history still shows.
/// </summary>
public sealed class CombinedHistoryReader(IHistoryReader primary, IReadOnlyList<HistorySource> others,
                                          Action<HistorySource, Exception>? onSourceFailed = null) : IHistoryReader
{
    public IHistoryReader Primary { get; } = primary;

    public IReadOnlyList<HistorySource> Others { get; } = others;

    public string Path => Primary.Path;

    public IReadOnlyList<StoredConnection> ReadConnections() =>
        Primary.ReadConnections()
               .Concat(FromOthers(s => s.Reader.ReadConnections().Select(c => c with { Id = s.AppIdFor(c.Id) }).ToList()))
               .GroupBy(c => c.Id)
               .Select(g => g.MaxBy(c => c.LastSeenUtc)!)
               .OrderByDescending(c => c.LastSeenUtc)
               .ToList();

    public IReadOnlyList<MetricHistoryPoint> ReadMetrics(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) =>
        ByTime(connectionId, (r, id) => r.ReadMetrics(id, fromUtc, toUtc, bucket), p => p.TimeUtc);

    public IReadOnlyList<WaitHistoryBucket> ReadWaits(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) =>
        ByTime(connectionId, (r, id) => r.ReadWaits(id, fromUtc, toUtc, bucket), p => p.TimeUtc);

    public IReadOnlyList<CounterHistoryBucket> ReadCounters(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) =>
        ByTime(connectionId, (r, id) => r.ReadCounters(id, fromUtc, toUtc, bucket), p => p.TimeUtc);

    public IReadOnlyList<ScoreHistoryPoint> ReadScores(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) =>
        ByTime(connectionId, (r, id) => r.ReadScores(id, fromUtc, toUtc, bucket), p => p.TimeUtc);

    // The same query_hash in both files is the same query on the same server: its work adds up.
    public IReadOnlyList<HistoryQueryTotals> ReadQueryTotals(Guid connectionId, DateTime fromUtc, DateTime toUtc) =>
        All(connectionId, (r, id) => r.ReadQueryTotals(id, fromUtc, toUtc))
            .GroupBy(t => t.QueryHash)
            .Select(g => new HistoryQueryTotals(g.Key, g.Sum(t => t.Executions), g.Sum(t => t.WorkerTimeUs), g.Sum(t => t.ElapsedTimeUs)))
            .ToList();

    public IReadOnlyDictionary<string, QueryTextInfo> ReadQueryTexts(Guid connectionId, IReadOnlyCollection<string> queryHashes)
    {
        var texts = new Dictionary<string, QueryTextInfo>(Primary.ReadQueryTexts(connectionId, queryHashes));
        foreach (var source in Others)
            if (source.IdFor(connectionId) is { } id)
                foreach (var (hash, text) in Safely(source, () => source.Reader.ReadQueryTexts(id, queryHashes)) ?? new Dictionary<string, QueryTextInfo>())
                    texts.TryAdd(hash, text);
        return texts;
    }

    public IReadOnlyList<StoredAlert> ReadAlerts(Guid? connectionId, DateTime fromUtc, DateTime toUtc)
    {
        var alerts = Primary.ReadAlerts(connectionId, fromUtc, toUtc).ToList();
        foreach (var source in Others)
        {
            Guid? id = connectionId is { } app ? source.IdFor(app) : null;
            if (connectionId is not null && id is null)
                continue;
            alerts.AddRange((Safely(source, () => source.Reader.ReadAlerts(id, fromUtc, toUtc)) ?? [])
                            .Select(a => a with { Alert = a.Alert with { ConnectionId = source.AppIdFor(a.Alert.ConnectionId) } }));
        }
        // Newest first, as each file returns them.
        return alerts.DistinctBy(a => a.Alert.Id).OrderByDescending(a => a.Alert.StartedUtc).ToList();
    }

    public ScoreHistoryPoint? ReadLastReachableScore(Guid connectionId, DateTime beforeUtc) =>
        All(connectionId, (r, id) => r.ReadLastReachableScore(id, beforeUtc) is { } p ? [p] : (IReadOnlyList<ScoreHistoryPoint>)[])
            .MaxBy(p => p.TimeUtc);

    public DateTime? ReadLastSampleTime(Guid connectionId) =>
        All(connectionId, (r, id) => r.ReadLastSampleTime(id) is { } t ? [t] : (IReadOnlyList<DateTime>)[])
            .Select(t => (DateTime?)t).Max();

    public IReadOnlyList<StoredActivity> ReadActivity(DateTime fromUtc, DateTime toUtc, Guid? connectionId = null, string? evt = null)
    {
        var rows = Primary.ReadActivity(fromUtc, toUtc, connectionId, evt).ToList();
        foreach (var source in Others)
        {
            Guid? id = connectionId is { } app ? source.IdFor(app) : null;
            if (connectionId is not null && id is null)
                continue;
            rows.AddRange((Safely(source, () => source.Reader.ReadActivity(fromUtc, toUtc, id, evt)) ?? [])
                          .Select(r => r.Entry.ConnectionId is { } file
                              ? r with { Entry = r.Entry with { ConnectionId = source.AppIdFor(file) } }
                              : r));
        }
        return rows.OrderByDescending(r => r.Entry.TimeUtc).Take(HistoryReader.MaxActivityRows).ToList();
    }

    // One connection's rows from every file that has it, in time order.
    private List<T> ByTime<T>(Guid connectionId, Func<IHistoryReader, Guid, IReadOnlyList<T>> read, Func<T, DateTime> time) =>
        All(connectionId, read).OrderBy(time).ToList();

    private List<T> All<T>(Guid connectionId, Func<IHistoryReader, Guid, IReadOnlyList<T>> read)
    {
        var rows = read(Primary, connectionId).ToList();
        foreach (var source in Others)
            if (source.IdFor(connectionId) is { } id)
                rows.AddRange(Safely(source, () => read(source.Reader, id)) ?? []);
        return rows;
    }

    private List<T> FromOthers<T>(Func<HistorySource, IReadOnlyList<T>> read) =>
        Others.SelectMany(s => Safely(s, () => read(s)) ?? []).ToList();

    private T? Safely<T>(HistorySource source, Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            onSourceFailed?.Invoke(source, ex);
            return null;
        }
    }
}
