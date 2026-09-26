using System.Globalization;
using SqlVitals.Engine.History;

namespace SqlVitals.Engine.FileIo;

/// <summary>What the File I/O page says about a file or volume, most urgent first: the grids sort by it.</summary>
public enum FileLatencyStatus
{
    /// <summary>Reads or writes averaged <see cref="FileLatencyThresholds.CriticalFactor"/> times the threshold or more.</summary>
    Critical,

    /// <summary>Reads or writes averaged more than the threshold.</summary>
    Slow,

    Ok,

    /// <summary>No reads or writes between the two readings, so there's no latency to show.</summary>
    Idle,

    /// <summary>
    /// Its totals went down between the readings: the database was taken offline, restored or
    /// detached and attached again, which starts them from zero. No figure until the next reading.
    /// </summary>
    Restarted,

    /// <summary>Only one reading so far: latency needs the change between two.</summary>
    Waiting,
}

/// <summary>
/// How much a file (or a volume) did between two readings of sys.dm_io_virtual_file_stats.
/// The stall is the time spent waiting for those reads or writes to complete, so stall divided
/// by count is the average latency over just that interval.
/// </summary>
public sealed record FileIoChange(long Reads, long BytesRead, long ReadStallMs, long Writes, long BytesWritten, long WriteStallMs)
{
    public static FileIoChange None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// <paramref name="now"/> less <paramref name="before"/>; null when any total went down,
    /// which only happens when the file's counters started again from zero.
    /// </summary>
    public static FileIoChange? Between(FileIoCounters? before, FileIoCounters now)
    {
        var change = new FileIoChange(
            now.Reads        - (before?.Reads        ?? 0),
            now.BytesRead    - (before?.BytesRead    ?? 0),
            now.ReadStallMs  - (before?.ReadStallMs  ?? 0),
            now.Writes       - (before?.Writes       ?? 0),
            now.BytesWritten - (before?.BytesWritten ?? 0),
            now.WriteStallMs - (before?.WriteStallMs ?? 0));

        return change.Reads < 0 || change.BytesRead < 0 || change.ReadStallMs < 0 ||
               change.Writes < 0 || change.BytesWritten < 0 || change.WriteStallMs < 0
            ? null
            : change;
    }

    /// <summary>Everything the files on one volume did together.</summary>
    public static FileIoChange Sum(IEnumerable<FileIoChange> changes) => changes.Aggregate(None, (a, b) => new FileIoChange(
        a.Reads + b.Reads, a.BytesRead + b.BytesRead, a.ReadStallMs + b.ReadStallMs,
        a.Writes + b.Writes, a.BytesWritten + b.BytesWritten, a.WriteStallMs + b.WriteStallMs));

    public double? ReadLatencyMs  => Reads  > 0 ? (double)ReadStallMs  / Reads  : null;
    public double? WriteLatencyMs => Writes > 0 ? (double)WriteStallMs / Writes : null;

    public bool IsIdle => Reads == 0 && Writes == 0;
}

/// <summary>Latency figures shared by a file and a volume, and how they measure up to a threshold.</summary>
public abstract record LatencyFigures(FileIoChange? Change, double Seconds, double ThresholdMs, bool Restarted)
{
    public double? ReadLatencyMs  => Change?.ReadLatencyMs;
    public double? WriteLatencyMs => Change?.WriteLatencyMs;

    public long? Reads  => Change?.Reads;
    public long? Writes => Change?.Writes;

    public double? ReadsPerSec  => PerSecond(Change?.Reads);
    public double? WritesPerSec => PerSecond(Change?.Writes);

    /// <summary>MB (10^6 bytes, as storage is sold) read per second.</summary>
    public double? ReadMBps  => PerSecond(Change?.BytesRead)    / 1_000_000;
    public double? WriteMBps => PerSecond(Change?.BytesWritten) / 1_000_000;

    /// <summary>Average size of a read, in KB: 8 is single pages, 64 and up is read-ahead or scans.</summary>
    public double? AvgReadKB  => Change is { Reads:  > 0 } c ? c.BytesRead    / 1024.0 / c.Reads  : null;
    public double? AvgWriteKB => Change is { Writes: > 0 } c ? c.BytesWritten / 1024.0 / c.Writes : null;

    public bool ReadIsSlow      => ReadLatencyMs  > ThresholdMs;
    public bool WriteIsSlow     => WriteLatencyMs > ThresholdMs;
    public bool ReadIsCritical  => ReadLatencyMs  >= ThresholdMs * FileLatencyThresholds.CriticalFactor;
    public bool WriteIsCritical => WriteLatencyMs >= ThresholdMs * FileLatencyThresholds.CriticalFactor;

    public FileLatencyStatus Status =>
        Restarted            ? FileLatencyStatus.Restarted
        : Change is null     ? FileLatencyStatus.Waiting
        : Change.IsIdle      ? FileLatencyStatus.Idle
        : ReadIsCritical || WriteIsCritical ? FileLatencyStatus.Critical
        : ReadIsSlow || WriteIsSlow         ? FileLatencyStatus.Slow
        : FileLatencyStatus.Ok;

    public bool IsSlow => Status is FileLatencyStatus.Critical or FileLatencyStatus.Slow;

    /// <summary>The worse of the read and write latency as a multiple of the threshold: how the grids sort within a status.</summary>
    public double Severity => Math.Max(ReadLatencyMs ?? 0, WriteLatencyMs ?? 0) / ThresholdMs;

    public string StatusText => Status switch
    {
        FileLatencyStatus.Critical  => "Very slow",
        FileLatencyStatus.Slow      => "Slow",
        FileLatencyStatus.Ok        => "OK",
        FileLatencyStatus.Idle      => "No I/O",
        FileLatencyStatus.Restarted => "Counters reset",
        _                           => "Measuring…",
    };

    public string ReadLatencyText  => FormatMs(ReadLatencyMs);
    public string WriteLatencyText => FormatMs(WriteLatencyMs);
    public string ReadsText        => FormatCount(Reads);
    public string WritesText       => FormatCount(Writes);
    public string ReadMBpsText     => FormatRate(ReadMBps);
    public string WriteMBpsText    => FormatRate(WriteMBps);
    public string AvgReadKBText    => FormatKB(AvgReadKB);
    public string AvgWriteKBText   => FormatKB(AvgWriteKB);

    private double? PerSecond(long? value) => value is { } v && Seconds > 0 ? v / Seconds : null;

    /// <summary>"0.4", "12.3", "1,250": milliseconds to one decimal, blank when there was nothing to time.</summary>
    public static string FormatMs(double? ms) => ms is { } v
        ? v.ToString(v >= 1000 ? "N0" : "N1", CultureInfo.CurrentCulture)
        : string.Empty;

    private static string FormatCount(long? n) => n is { } v ? v.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    private static string FormatRate(double? mbps) => mbps switch
    {
        null      => string.Empty,
        0         => "0",
        < 0.1     => "< 0.1",
        var v     => v.Value.ToString(v >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture),
    };

    private static string FormatKB(double? kb) => kb is { } v ? v.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
}

/// <summary>
/// A database file for the File I/O page (#41): its average read and write latency between two
/// readings, how busy it was, and whether that's over the threshold for its type.
/// </summary>
/// <param name="Change">What it did between the readings; null while there's only one, or its counters reset.</param>
/// <param name="Seconds">How far apart the readings were.</param>
public sealed record FileLatency(FileIoCounters File, FileIoChange? Change, double Seconds, double ThresholdMs, bool Restarted)
    : LatencyFigures(Change, Seconds, ThresholdMs, Restarted)
{
    public string DatabaseName => File.DatabaseName;
    public string FileName     => File.FileName;
    public string PhysicalName => File.PhysicalName;
    public string Volume       => File.Volume;
    public bool   IsLog        => File.IsLog;
    public long   SizeBytes    => File.SizeBytes;

    public string TypeText => File.FileType switch
    {
        "ROWS"       => "Data",
        "LOG"        => "Log",
        "FILESTREAM" => "FILESTREAM",
        "FULLTEXT"   => "Full-text",
        var other    => other,
    };

    public string SizeText => File.SizeBytes > 0 ? HistorySettings.FormatSize(File.SizeBytes) : string.Empty;

    /// <summary>The average latency since the database came online (usually since SQL Server started), for comparison.</summary>
    public double? StartupReadLatencyMs  => File.Reads  > 0 ? (double)File.ReadStallMs  / File.Reads  : null;
    public double? StartupWriteLatencyMs => File.Writes > 0 ? (double)File.WriteStallMs / File.Writes : null;

    public string StartupReadLatencyText  => FormatMs(StartupReadLatencyMs);
    public string StartupWriteLatencyText => FormatMs(StartupWriteLatencyMs);

    /// <summary>Why the status is what it is, in a sentence or two, for the tooltip and the detail line.</summary>
    public string Explanation
    {
        get
        {
            var threshold = FileLatencyThresholds.Format(ThresholdMs);
            var kind      = IsLog ? "log files" : "data files";
            switch (Status)
            {
                case FileLatencyStatus.Waiting:
                    return "Latency needs two readings: it's the change in I/O stall divided by the change in reads or writes.";
                case FileLatencyStatus.Restarted:
                    return "Its I/O totals went down since the last reading: the database was taken offline, restored or " +
                           "attached again, which starts them from zero. Figures return with the next reading.";
                case FileLatencyStatus.Idle:
                    return "No reads or writes between the readings.";
            }

            var parts = new List<string>();
            if (ReadIsSlow)
                parts.Add($"Reads averaged {FormatMs(ReadLatencyMs)} ms over {ReadsText} read{(Reads == 1 ? "" : "s")}.");
            if (WriteIsSlow)
                parts.Add($"Writes averaged {FormatMs(WriteLatencyMs)} ms over {WritesText} write{(Writes == 1 ? "" : "s")}.");
            if (parts.Count == 0)
                return $"Within the {threshold} set for {kind}.";

            parts.Add(Status == FileLatencyStatus.Critical
                ? $"That's {FileLatencyThresholds.CriticalFactor:0} times or more the {threshold} set for {kind}."
                : $"That's over the {threshold} set for {kind}.");
            if ((ReadIsSlow ? Reads : 0) + (WriteIsSlow ? Writes : 0) < FewIos)
                parts.Add("Only a few I/Os, so one slow one weighs heavily: see whether it stays slow.");
            return string.Join(" ", parts);
        }
    }

    /// <summary>Below this many slow reads plus writes an average says little on its own.</summary>
    internal const int FewIos = 10;

    /// <summary>
    /// The file's latency between <paramref name="before"/> and <paramref name="now"/>. A file
    /// with no <paramref name="before"/> came online between the readings (or, with
    /// <paramref name="hasBaseline"/> false, there's no earlier reading at all).
    /// </summary>
    public static FileLatency From(FileIoCounters? before, FileIoCounters now, bool hasBaseline, double seconds,
                                   FileLatencyThresholds thresholds)
    {
        var threshold = thresholds.For(now.IsLog);
        if (!hasBaseline)
            return new FileLatency(now, null, seconds, threshold, Restarted: false);

        // Another database was given the same id since: all its I/O is since it came online.
        if (before is not null && before.DatabaseName != now.DatabaseName)
            before = null;

        var change = FileIoChange.Between(before, now);
        return new FileLatency(now, change, seconds, threshold, Restarted: change is null);
    }
}

/// <summary>
/// All the files on one volume taken together: latency there is the total stall over the total
/// reads or writes, so busy files count for more. Several slow files on the same volume point at
/// the storage rather than at one database.
/// </summary>
/// <param name="ThresholdMs">The log threshold when every file measured there is a log file, otherwise the data one.</param>
/// <param name="Restarted">Every file there had its counters reset, so nothing was measured.</param>
public sealed record VolumeLatency(string Volume, int FileCount, int SlowFileCount, FileIoChange? Change, double Seconds, double ThresholdMs,
                                   bool Restarted = false)
    : LatencyFigures(Change, Seconds, ThresholdMs, Restarted)
{
    public string VolumeText => Volume.Length == 0 ? "(unknown)" : Volume;

    public string FilesText => SlowFileCount == 0 ? FileCount.ToString("N0", CultureInfo.CurrentCulture)
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} ({1:N0} slow)", FileCount, SlowFileCount);

    internal static IReadOnlyList<VolumeLatency> From(IReadOnlyList<FileLatency> files, double seconds, FileLatencyThresholds thresholds) => files
        .GroupBy(f => f.Volume, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var measured = g.Where(f => f.Change is not null).ToList();
            var change   = measured.Count == 0 ? null : FileIoChange.Sum(measured.Select(f => f.Change!));
            var allLog   = measured.Count > 0 && measured.All(f => f.IsLog);
            return new VolumeLatency(g.First().Volume, g.Count(), g.Count(f => f.IsSlow), change, seconds, thresholds.For(allLog),
                                     Restarted: measured.Count == 0 && g.Any(f => f.Restarted));
        })
        .OrderBy(v => v.Status)
        .ThenByDescending(v => v.Severity)
        .ThenBy(v => v.Volume, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>Every file's latency between two readings, and the volumes they're on, or why they couldn't be read.</summary>
/// <param name="Files">Most urgent first (<see cref="FileLatencyStatus"/>), then the slowest, then by name.</param>
/// <param name="From">The server time of the earlier reading; null while there's only one.</param>
/// <param name="To">The server time of the later reading.</param>
/// <param name="Note">Something to know about the figures: a restart, or that Azure SQL Database sees only its own files.</param>
public sealed record FileLatencyList(
    IReadOnlyList<FileLatency>   Files,
    IReadOnlyList<VolumeLatency> Volumes,
    FileLatencyThresholds        Thresholds,
    DateTime?                    From,
    DateTime?                    To,
    string?                      Note)
{
    /// <summary>Why nothing could be read (no permission); null when the files were read.</summary>
    public string? Problem { get; init; }

    public bool Available => Problem is null;

    /// <summary>Only one reading so far, so no latency yet.</summary>
    public bool Measuring => From is null;

    public double Seconds => From is { } from && To is { } to ? (to - from).TotalSeconds : 0;

    public static FileLatencyList Unavailable(string problem, FileLatencyThresholds thresholds) =>
        new([], [], thresholds, null, null, null) { Problem = problem };

    public int SlowCount      => Files.Count(f => f.IsSlow);
    public int CriticalCount  => Files.Count(f => f.Status == FileLatencyStatus.Critical);
    public int IdleCount      => Files.Count(f => f.Status == FileLatencyStatus.Idle);
    public int RestartedCount => Files.Count(f => f.Status == FileLatencyStatus.Restarted);

    /// <summary>The file furthest over its threshold, if any is over.</summary>
    public FileLatency? Slowest => Files.Where(f => f.IsSlow).MaxBy(f => f.Severity);

    /// <summary>Most urgent first, then the slowest for its threshold, then by database and file.</summary>
    public static IReadOnlyList<FileLatency> Sort(IEnumerable<FileLatency> files) => files
        .OrderBy(f => f.Status)
        .ThenByDescending(f => f.Severity)
        .ThenBy(f => f.DatabaseName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(f => f.File.FileId)
        .ToList();

    /// <summary>
    /// Each file in <paramref name="current"/> against the same file in <paramref name="baseline"/>:
    /// the latency over the time between them. With no baseline the files are listed, waiting.
    /// </summary>
    public static FileLatencyList Between(FileIoReading? baseline, FileIoReading current, FileLatencyThresholds thresholds,
                                          string? note = null)
    {
        var seconds = baseline is null ? 0 : (current.ServerTime - baseline.ServerTime).TotalSeconds;
        var before  = new Dictionary<(int, int), FileIoCounters>();
        foreach (var f in baseline?.Files ?? [])
            before.TryAdd(f.Key, f);

        var files = Sort(current.Files.Select(now =>
            FileLatency.From(before.GetValueOrDefault(now.Key), now, baseline is not null, seconds, thresholds)));

        var notes = new[] { note, current.Note }.Where(n => !string.IsNullOrEmpty(n));
        return new FileLatencyList(
            files,
            VolumeLatency.From(files, seconds, thresholds),
            thresholds,
            baseline?.ServerTime,
            current.ServerTime,
            notes.Any() ? string.Join(" ", notes) : null);
    }
}
