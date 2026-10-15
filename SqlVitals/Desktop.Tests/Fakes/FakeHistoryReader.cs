using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>A monitoring history with the waits given and nothing else (#178).</summary>
public sealed class FakeHistoryReader : IHistoryReader
{
    /// <summary>What <see cref="ReadWaits"/> returns: the buckets inside the range asked for.</summary>
    public IReadOnlyList<WaitHistoryBucket> Waits { get; init; } = [];

    public string Path => "fake history";

    /// <summary>Called as <see cref="ReadWaits"/> or <see cref="ReadCounters"/> starts, on the read's thread: a test can hold a read there.</summary>
    public Action? Reading { get; init; }

    public IReadOnlyList<WaitHistoryBucket> ReadWaits(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        Reading?.Invoke();
        return Waits.Where(b => b.TimeUtc >= fromUtc && b.TimeUtc < toUtc).ToList();
    }

    public IReadOnlyList<StoredConnection> ReadConnections() => [];
    /// <summary>The metrics held; a read gets those in its window.</summary>
    public IReadOnlyList<MetricHistoryPoint> Metrics { get; init; } = [];

    /// <summary>What a read of the metrics throws, for a history that can't be read.</summary>
    public Exception? MetricsFail { get; set; }

    public IReadOnlyList<MetricHistoryPoint> ReadMetrics(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        Reading?.Invoke();
        if (MetricsFail is { } failure)
            throw failure;
        return Metrics.Where(p => p.TimeUtc >= fromUtc && p.TimeUtc < toUtc).ToList();
    }
    /// <summary>What <see cref="ReadCounters"/> returns: the buckets inside the range asked for.</summary>
    public IReadOnlyList<CounterHistoryBucket> Counters { get; init; } = [];

    public IReadOnlyList<CounterHistoryBucket> ReadCounters(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        Reading?.Invoke();
        return Counters.Where(b => b.TimeUtc >= fromUtc && b.TimeUtc < toUtc).ToList();
    }
    public IReadOnlyList<HistoryQueryTotals> ReadQueryTotals(Guid connectionId, DateTime fromUtc, DateTime toUtc) => [];
    public IReadOnlyDictionary<string, QueryTextInfo> ReadQueryTexts(Guid connectionId, IReadOnlyCollection<string> queryHashes) =>
        new Dictionary<string, QueryTextInfo>();
    public IReadOnlyList<StoredAlert> ReadAlerts(Guid? connectionId, DateTime fromUtc, DateTime toUtc) => [];
    public IReadOnlyList<ScoreHistoryPoint> ReadScores(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
    public ScoreHistoryPoint? ReadLastReachableScore(Guid connectionId, DateTime beforeUtc) => null;
    public DateTime? ReadLastSampleTime(Guid connectionId) => null;
    public IReadOnlyList<StoredActivity> ReadActivity(DateTime fromUtc, DateTime toUtc, Guid? connectionId = null, string? evt = null) => [];
}
