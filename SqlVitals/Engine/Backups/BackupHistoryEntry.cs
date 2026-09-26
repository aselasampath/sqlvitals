using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Backups;

/// <summary>
/// One row of msdb.dbo.backupset for the selected database: any kind of backup, newest first.
/// </summary>
/// <param name="Type">backupset.type: D full, I differential, L log, F file, G differential file, P partial, Q differential partial.</param>
/// <param name="Bytes">backup_size: the data backed up.</param>
/// <param name="CompressedBytes">compressed_backup_size: what was written; the same as Bytes when not compressed.</param>
/// <param name="BeforeCreate">
/// It finished before the database was created or restored, so it's of an earlier database with the
/// same name, or of this one before the restore, and isn't counted.
/// </param>
public sealed record BackupHistoryEntry(
    string    Type,
    DateTime  Start,
    DateTime  Finish,
    long?     Bytes,
    long?     CompressedBytes,
    bool      CopyOnly,
    bool      HasChecksums,
    bool      IsDamaged,
    string?   RecoveryModel,
    string?   UserName,
    string?   Device,
    bool      BeforeCreate)
{
    /// <summary>Most rows read for one database: a log backup every 15 minutes is about five days.</summary>
    public const int MaxRows = 500;

    public string TypeText => TypeName(Type) + (CopyOnly ? " (copy-only)" : "");

    public TimeSpan Duration => Finish > Start ? Finish - Start : TimeSpan.Zero;

    public string DurationText => MsdbTime.Format(Duration);

    public string SizeText => Bytes is { } b ? HistorySettings.FormatSize(b) : string.Empty;

    /// <summary>The size written, shown only when compression made it smaller.</summary>
    public string CompressedText =>
        CompressedBytes is { } c && Bytes is { } b && c < b ? HistorySettings.FormatSize(c) : string.Empty;

    public string ChecksumText => IsDamaged ? "Damaged" : HasChecksums ? "Yes" : "No";

    public bool IsFull => Type == "D";

    public static string TypeName(string type) => type switch
    {
        "D" => "Full",
        "I" => "Differential",
        "L" => "Log",
        "F" => "File or filegroup",
        "G" => "Differential file",
        "P" => "Partial",
        "Q" => "Differential partial",
        _   => type,
    };
}
