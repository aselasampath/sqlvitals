using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.History;

/// <summary>
/// Turns successive cumulative <see cref="HistoryDetailSnapshot"/>s from one server into the
/// changes over each interval. Not thread-safe: one tracker per connection, one caller at a time.
///
/// Waits and file I/O are small, complete lists, so each snapshot is diffed against the one
/// before it. Queries are not: after the first (baseline) read only the hashes that ran in the
/// interval are fetched, so their previous totals come from a baseline kept across snapshots.
/// </summary>
public sealed class HistoryDetailTracker
{
    /// <summary>Hashes kept in the query baseline before the least recently seen half is dropped.</summary>
    public const int MaxBaselineQueries = 50_000;

    private readonly Dictionary<string, (QueryTotals Totals, DateTime SeenServerTime)> _queryBaseline = new();
    private HistoryDetailSnapshot? _previous;

    /// <summary>
    /// What to pass as <c>queriesExecutedSince</c> on the next read: null until a baseline has been
    /// taken, then the server time of the last snapshot.
    /// </summary>
    public DateTime? QueriesExecutedSince => _previous?.ServerTime;

    /// <summary>
    /// Records <paramref name="current"/> and returns the change since the previous snapshot, or
    /// null when there is nothing to compare with: the first snapshot, or the first after the
    /// server restarted (its counters start again from zero) or its clock went backwards.
    /// </summary>
    public HistoryDetail? Add(HistoryDetailSnapshot current, int topQueries)
    {
        var previous = _previous;
        _previous = current;

        if (previous is null
            || current.ServerStartTime != previous.ServerStartTime
            || current.ServerTime <= previous.ServerTime)
        {
            _queryBaseline.Clear();
            RememberQueries(current);
            return null;
        }

        var waits = DiffWaits(previous.Waits, current.Waits);
        var files = DiffFiles(previous.Files, current.Files);
        var top   = DiffQueries(previous.ServerTime, current.Queries)
                    .OrderByDescending(q => q.WorkerTimeUs)
                    .ThenByDescending(q => q.LogicalReads)
                    .Take(topQueries)
                    .ToList();

        RememberQueries(current);

        return new HistoryDetail(
            current.ServerTime, (current.ServerTime - previous.ServerTime).TotalSeconds,
            waits, files, top, current.Memory);
    }

    // A wait type missing from the previous read had a zero total (only non-zero rows are read).
    // A negative change means the stats were cleared (DBCC SQLPERF), so there's no true figure.
    private static List<WaitTypeTotals> DiffWaits(IReadOnlyList<WaitTypeTotals> previous, IReadOnlyList<WaitTypeTotals> current)
    {
        var before = previous.ToDictionary(w => w.WaitType);
        var result = new List<WaitTypeTotals>();
        foreach (var now in current)
        {
            before.TryGetValue(now.WaitType, out var then);
            var delta = new WaitTypeTotals(
                now.WaitType,
                now.WaitingTasks     - (then?.WaitingTasks     ?? 0),
                now.WaitTimeMs       - (then?.WaitTimeMs       ?? 0),
                now.SignalWaitTimeMs - (then?.SignalWaitTimeMs ?? 0));

            if (delta.WaitingTasks < 0 || delta.WaitTimeMs < 0 || delta.SignalWaitTimeMs < 0)
                continue;
            if (delta.WaitingTasks > 0 || delta.WaitTimeMs > 0)
                result.Add(delta);
        }
        return result;
    }

    // A file missing from the previous read belongs to a database that came online in the
    // interval; its counters started from zero.
    private static List<FileIoTotals> DiffFiles(IReadOnlyList<FileIoTotals> previous, IReadOnlyList<FileIoTotals> current)
    {
        var before = previous.ToDictionary(f => (f.DatabaseName, f.FileId));
        var result = new List<FileIoTotals>();
        foreach (var now in current)
        {
            before.TryGetValue((now.DatabaseName, now.FileId), out var then);
            var delta = now with
            {
                Reads        = now.Reads        - (then?.Reads        ?? 0),
                BytesRead    = now.BytesRead    - (then?.BytesRead    ?? 0),
                ReadStallMs  = now.ReadStallMs  - (then?.ReadStallMs  ?? 0),
                Writes       = now.Writes       - (then?.Writes       ?? 0),
                BytesWritten = now.BytesWritten - (then?.BytesWritten ?? 0),
                WriteStallMs = now.WriteStallMs - (then?.WriteStallMs ?? 0),
            };

            // Negative: the database was taken offline and back, which restarts its counters.
            if (delta.Reads < 0 || delta.BytesRead < 0 || delta.ReadStallMs < 0 ||
                delta.Writes < 0 || delta.BytesWritten < 0 || delta.WriteStallMs < 0)
                continue;
            if (delta.Reads > 0 || delta.Writes > 0)
                result.Add(delta);
        }
        return result;
    }

    private IEnumerable<QueryDelta> DiffQueries(DateTime previousServerTime, IReadOnlyList<QueryTotals> current)
    {
        foreach (var now in current)
        {
            // Every plan for this hash was compiled in the interval, so all its work is new.
            var allNew = now.FirstPlanCreated >= previousServerTime;

            QueryDelta? delta = null;
            if (_queryBaseline.TryGetValue(now.QueryHash, out var then))
            {
                delta = new QueryDelta(
                    now.QueryHash,
                    now.Executions    - then.Totals.Executions,
                    now.WorkerTimeUs  - then.Totals.WorkerTimeUs,
                    now.ElapsedTimeUs - then.Totals.ElapsedTimeUs,
                    now.LogicalReads  - then.Totals.LogicalReads,
                    now.LogicalWrites - then.Totals.LogicalWrites);

                // A plan left the cache, taking its totals with it: the sum can't be compared.
                if (delta.Executions < 0 || delta.WorkerTimeUs < 0 || delta.ElapsedTimeUs < 0 ||
                    delta.LogicalReads < 0 || delta.LogicalWrites < 0)
                    delta = null;
            }

            // Not in the baseline and older than the interval: its earlier totals are unknown.
            if (delta is null && allNew)
                delta = new QueryDelta(now.QueryHash, now.Executions, now.WorkerTimeUs, now.ElapsedTimeUs,
                                       now.LogicalReads, now.LogicalWrites);

            if (delta is { Executions: > 0 })
                yield return delta;
        }
    }

    private void RememberQueries(HistoryDetailSnapshot snapshot)
    {
        foreach (var q in snapshot.Queries)
            _queryBaseline[q.QueryHash] = (q, snapshot.ServerTime);

        if (_queryBaseline.Count <= MaxBaselineQueries)
            return;

        foreach (var hash in _queryBaseline.OrderBy(kv => kv.Value.SeenServerTime)
                                           .Take(_queryBaseline.Count - MaxBaselineQueries / 2)
                                           .Select(kv => kv.Key).ToList())
            _queryBaseline.Remove(hash);
    }
}
