namespace SqlVitals.Engine.History;

/// <summary>
/// Where history is written (#172): the local SQLite file today (<see cref="HistoryStore"/>), so
/// another store can be added later without touching what records. Only <see cref="HistoryWriter"/>
/// uses one, on its single background thread, so an implementation needn't be thread-safe.
/// </summary>
public interface IHistoryStore : IDisposable
{
    /// <summary>Where it is, for messages: a file path.</summary>
    string Path { get; }

    /// <summary>
    /// Opens it, creating it when missing. Throws <see cref="HistorySchemaTooNewException"/> when
    /// it was written by a newer SqlVitals, which then leaves it alone.
    /// </summary>
    void Open();

    /// <summary>Stores the records in one go: all of them or, on failure, none.</summary>
    void Write(IReadOnlyCollection<HistoryRecord> records);

    /// <summary>Ends the alerts a previous run left active; returns how many.</summary>
    int EndAlertsLeftOpen();

    /// <summary>Deletes what was captured before <paramref name="cutoffUtc"/>, and activity log rows before <paramref name="activityCutoffUtc"/> (none when null).</summary>
    void Purge(DateTime cutoffUtc, DateTime? activityCutoffUtc = null);

    /// <summary>Deletes all history, keeping the activity log.</summary>
    void Clear();

    /// <summary>Deletes the text of every query (#175), keeping the rest of the history.</summary>
    void ClearQueryTexts();

    /// <summary>Whether an error from <see cref="Write"/> means the store is damaged: it is then closed, moved aside and started again.</summary>
    bool IsDamaged(Exception failure);

    /// <summary>After <see cref="IDisposable.Dispose"/>: keeps the damaged store aside, so a new one can be started without losing it.</summary>
    void MoveAside();
}

/// <summary>
/// Makes the <see cref="IHistoryStore"/> for a path, and says whether one is there yet, so the
/// writer never creates a store just to purge, clear or end alerts in it (#172).
/// </summary>
public interface IHistoryStoreFactory
{
    IHistoryStore Create(string path);

    bool Exists(string path);
}

/// <summary>The SQLite file: <see cref="HistoryStore"/>.</summary>
public sealed class SqliteHistoryStores : IHistoryStoreFactory
{
    public static readonly SqliteHistoryStores Instance = new();

    public IHistoryStore Create(string path) => new HistoryStore(path);

    public bool Exists(string path) => File.Exists(path);
}

/// <summary>
/// Reads past ranges from the history (#172): the local SQLite file today (<see cref="HistoryReader"/>).
/// Any thread may use one. Times are UTC on the clock of the PC that recorded them.
/// </summary>
public interface IHistoryReader
{
    /// <summary>Where it reads, for messages: a file path.</summary>
    string Path { get; }

    /// <inheritdoc cref="HistoryReader.ReadConnections"/>
    IReadOnlyList<StoredConnection> ReadConnections();

    /// <inheritdoc cref="HistoryReader.ReadMetrics"/>
    IReadOnlyList<MetricHistoryPoint> ReadMetrics(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket);

    /// <inheritdoc cref="HistoryReader.ReadWaits"/>
    IReadOnlyList<WaitHistoryBucket> ReadWaits(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket);

    /// <inheritdoc cref="HistoryReader.ReadCounters"/>
    IReadOnlyList<CounterHistoryBucket> ReadCounters(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket);

    /// <inheritdoc cref="HistoryReader.ReadQueryTotals"/>
    IReadOnlyList<HistoryQueryTotals> ReadQueryTotals(Guid connectionId, DateTime fromUtc, DateTime toUtc);

    /// <inheritdoc cref="HistoryReader.ReadQueryTexts"/>
    IReadOnlyDictionary<string, Models.QueryTextInfo> ReadQueryTexts(Guid connectionId, IReadOnlyCollection<string> queryHashes);

    /// <inheritdoc cref="HistoryReader.ReadAlerts"/>
    IReadOnlyList<StoredAlert> ReadAlerts(Guid? connectionId, DateTime fromUtc, DateTime toUtc);

    /// <inheritdoc cref="HistoryReader.ReadScores"/>
    IReadOnlyList<ScoreHistoryPoint> ReadScores(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket);

    /// <inheritdoc cref="HistoryReader.ReadLastReachableScore"/>
    ScoreHistoryPoint? ReadLastReachableScore(Guid connectionId, DateTime beforeUtc);

    /// <inheritdoc cref="HistoryReader.ReadLastSampleTime"/>
    DateTime? ReadLastSampleTime(Guid connectionId);

    /// <inheritdoc cref="HistoryReader.ReadActivity"/>
    IReadOnlyList<Activity.StoredActivity> ReadActivity(DateTime fromUtc, DateTime toUtc, Guid? connectionId = null, string? evt = null);
}
