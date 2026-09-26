namespace SqlVitals.Engine.FileIo;

/// <summary>What the File I/O page measures latency over.</summary>
public enum FileIoWindow
{
    /// <summary>Between the last two readings: what the storage is doing now.</summary>
    LastInterval,

    /// <summary>
    /// Between the first reading (when the page opened, or was started over) and the last: a
    /// longer, steadier average, for a workload run while the page is open.
    /// </summary>
    SinceFirstReading,
}

/// <summary>
/// Keeps the readings of one server that the File I/O page compares (#41): the first, the one
/// before the last, and the last. Not thread-safe: one per page, one caller at a time.
/// </summary>
public sealed class FileIoTracker
{
    public FileIoReading? First    { get; private set; }
    public FileIoReading? Previous { get; private set; }
    public FileIoReading? Latest   { get; private set; }

    // Why the readings started again with the last one, until a later reading gives figures.
    private string? _restartNote;

    /// <summary>
    /// Adds a reading. When SQL Server restarted since the last one its totals started again
    /// from zero, so nothing earlier can be compared with: the readings start again from it.
    /// A reading that couldn't be taken is ignored.
    /// </summary>
    public void Add(FileIoReading reading)
    {
        if (!reading.Available)
            return;

        if (Latest is { } last && (reading.ServerStartTime != last.ServerStartTime || reading.ServerTime <= last.ServerTime))
        {
            _restartNote = reading.ServerStartTime != last.ServerStartTime
                ? $"SQL Server restarted at {reading.ServerStartTime:yyyy-MM-dd HH:mm:ss}, which starts every file's totals " +
                  "from zero, so latency is measured again from the reading after that."
                : "The server's clock went back, so latency is measured again from this reading.";
            First    = reading;
            Previous = null;
            Latest   = reading;
            return;
        }

        _restartNote = null;
        Previous = Latest;
        Latest   = reading;
        First  ??= reading;
    }

    /// <summary>Forgets every reading: the next one is the new first.</summary>
    public void StartOver()
    {
        First = Previous = Latest = null;
        _restartNote = null;
    }

    /// <summary>The reading the last one is compared with over <paramref name="window"/>; null while there's only one.</summary>
    public FileIoReading? Baseline(FileIoWindow window) => window switch
    {
        FileIoWindow.SinceFirstReading => ReferenceEquals(First, Latest) ? null : First,
        _                              => Previous,
    };

    /// <summary>Each file's latency over <paramref name="window"/>; null before the first reading.</summary>
    public FileLatencyList? Latencies(FileIoWindow window, FileLatencyThresholds thresholds) =>
        Latest is { } latest ? FileLatencyList.Between(Baseline(window), latest, thresholds, _restartNote) : null;
}
