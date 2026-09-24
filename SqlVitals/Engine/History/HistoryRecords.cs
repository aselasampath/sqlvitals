using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.History;

/// <summary>
/// Which saved connection a history row belongs to. Names only, never credentials, so history
/// stays readable after the connection is deleted.
/// </summary>
public sealed record HistoryConnection(Guid Id, string Name, string Server, string Database);

/// <summary>Something queued for <see cref="HistoryWriter"/> to store.</summary>
public abstract record HistoryRecord(HistoryConnection Connection, DateTime CapturedUtc);

/// <summary>One Live Metrics sample (the every-interval tier).</summary>
public sealed record MetricSampleRecord(HistoryConnection Connection, DateTime CapturedUtc, LiveMetricSample Sample)
    : HistoryRecord(Connection, CapturedUtc);

/// <summary>One detail snapshot (the every-few-minutes tier) plus the text of any new top queries.</summary>
public sealed record DetailRecord(
    HistoryConnection Connection, DateTime CapturedUtc, HistoryDetail Detail, IReadOnlyList<QueryTextInfo> QueryTexts)
    : HistoryRecord(Connection, CapturedUtc);

/// <summary>
/// What changed on the server between two <see cref="HistoryDetailSnapshot"/>s. Waits and Files
/// reuse the totals records, holding the change over the interval instead of the running total.
/// </summary>
public sealed record HistoryDetail(
    DateTime                      ServerTime,
    double                        IntervalSeconds,
    IReadOnlyList<WaitTypeTotals> Waits,
    IReadOnlyList<FileIoTotals>   Files,
    IReadOnlyList<QueryDelta>     TopQueries,
    HistoryMemory?                Memory);

/// <summary>Work one query_hash did during the interval.</summary>
public sealed record QueryDelta(
    string QueryHash, long Executions, long WorkerTimeUs, long ElapsedTimeUs, long LogicalReads, long LogicalWrites);

/// <summary>Where records go. <see cref="HistoryWriter"/> in the app; a list in tests.</summary>
public interface IHistorySink
{
    /// <summary>Queues a record. Never blocks and never throws.</summary>
    void Enqueue(HistoryRecord record);

    /// <summary>Goes up each time the history is cleared, so anything already saved is gone.</summary>
    int ClearCount => 0;
}
