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

    public IReadOnlyList<WaitHistoryBucket> ReadWaits(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) =>
        Waits.Where(b => b.TimeUtc >= fromUtc && b.TimeUtc < toUtc).ToList();

    public IReadOnlyList<StoredConnection> ReadConnections() => [];
    public IReadOnlyList<MetricHistoryPoint> ReadMetrics(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
    public IReadOnlyList<CounterHistoryBucket> ReadCounters(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
    public IReadOnlyList<HistoryQueryTotals> ReadQueryTotals(Guid connectionId, DateTime fromUtc, DateTime toUtc) => [];
    public IReadOnlyDictionary<string, QueryTextInfo> ReadQueryTexts(Guid connectionId, IReadOnlyCollection<string> queryHashes) =>
        new Dictionary<string, QueryTextInfo>();
    public IReadOnlyList<StoredAlert> ReadAlerts(Guid? connectionId, DateTime fromUtc, DateTime toUtc) => [];
    public IReadOnlyList<ScoreHistoryPoint> ReadScores(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
    public ScoreHistoryPoint? ReadLastReachableScore(Guid connectionId, DateTime beforeUtc) => null;
    public DateTime? ReadLastSampleTime(Guid connectionId) => null;
    public IReadOnlyList<StoredActivity> ReadActivity(DateTime fromUtc, DateTime toUtc, Guid? connectionId = null, string? evt = null) => [];
}
