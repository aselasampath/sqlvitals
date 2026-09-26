using System.Globalization;
using SqlVitals.Engine.History;

namespace SqlVitals.Engine.Backups;

/// <summary>What the Backups page says about a database, most urgent first: the grid sorts by it.</summary>
public enum BackupStatus
{
    /// <summary>msdb has no full backup of it since it was created or restored: nothing to restore from.</summary>
    NoFullBackup,

    /// <summary>
    /// FULL or BULK_LOGGED recovery, but the log chain hasn't started (no full backup since a switch
    /// from SIMPLE, say), so no log backup can be taken and the log is truncated as in SIMPLE.
    /// </summary>
    LogChainBroken,

    /// <summary>FULL or BULK_LOGGED recovery with a full backup, but never a log backup.</summary>
    NoLogBackup,

    /// <summary>The last full backup is older than the RPO allows.</summary>
    FullOverdue,

    /// <summary>The last log backup is older than the RPO allows.</summary>
    LogOverdue,

    Ok,

    /// <summary>Not online (restoring, offline, …): it can't be backed up, so it isn't checked.</summary>
    NotChecked,
}

/// <summary>One backup of a database: when it ran, how big it was, and where it went.</summary>
/// <param name="Bytes">The size written: the compressed size when it was compressed.</param>
/// <param name="Device">The first file or device of its media set.</param>
public sealed record BackupInfo(DateTime Start, DateTime Finish, long? Bytes, bool CopyOnly, string? Device)
{
    public TimeSpan Duration => Finish > Start ? Finish - Start : TimeSpan.Zero;
}

/// <summary>
/// A database as the backups query reads it: sys.databases with the newest full, differential
/// and log backup msdb recorded for it since <see cref="CreateDate"/>. Times are the server's
/// local time, as msdb keeps them.
/// </summary>
/// <param name="LogChainKnown">sys.database_recovery_status had a row for it (the login can see it).</param>
/// <param name="LogChainStarted">Its last_log_backup_lsn is set: a log backup can be taken.</param>
public sealed record DatabaseBackupRow(
    string      Name,
    string      RecoveryModel,
    string      State,
    bool        IsReadOnly,
    DateTime    CreateDate,
    bool        InAvailabilityGroup,
    bool        LogChainKnown,
    bool        LogChainStarted,
    BackupInfo? Full,
    BackupInfo? Differential,
    BackupInfo? Log);

/// <summary>A problem with a database's backups, in a sentence.</summary>
public sealed record BackupWarning(BackupStatus Kind, string Text);

/// <summary>
/// A database for the Backups page (#40): its last full, differential and log backup, how old
/// each is, and what falls short of the <see cref="BackupRpo"/> it was checked against.
/// </summary>
public sealed record DatabaseBackup(DatabaseBackupRow Row, DateTime ServerNow, IReadOnlyList<BackupWarning> Warnings)
{
    public string      Name          => Row.Name;
    public string      RecoveryModel => Row.RecoveryModel;
    public BackupInfo? Full          => Row.Full;
    public BackupInfo? Differential  => Row.Differential;
    public BackupInfo? Log           => Row.Log;

    // Flat for the grid, which sorts on them.
    public DateTime? FullFinish         => Full?.Finish;
    public DateTime? DifferentialFinish => Differential?.Finish;
    public DateTime? LogFinish          => Log?.Finish;
    public long?     FullBytes          => Full?.Bytes;

    public bool IsOnline => Row.State == "ONLINE";

    /// <summary>FULL or BULK_LOGGED: the log is kept until a log backup, which is what gives a point-in-time restore.</summary>
    public bool UsesLogBackups => Row.RecoveryModel is "FULL" or "BULK_LOGGED";

    /// <summary>
    /// Whether the log has to be backed up to meet the RPO. Not for model, whose FULL recovery is
    /// only what new databases start with, nor a read-only database, whose log doesn't change.
    /// </summary>
    public bool NeedsLogBackups => UsesLogBackups && !Row.IsReadOnly && !Row.Name.Equals("model", StringComparison.OrdinalIgnoreCase);

    public BackupStatus Status => !IsOnline ? BackupStatus.NotChecked
        : Warnings.Count == 0 ? BackupStatus.Ok
        : Warnings.Min(w => w.Kind);

    public bool NeedsAttention => Status < BackupStatus.Ok;

    /// <summary>Red: nothing (or no log) to restore. Amber is an overdue backup.</summary>
    public bool IsCritical => Status is BackupStatus.NoFullBackup or BackupStatus.LogChainBroken or BackupStatus.NoLogBackup;

    public bool FullIsOverdue => Warnings.Any(w => w.Kind is BackupStatus.NoFullBackup or BackupStatus.FullOverdue);

    public bool LogIsOverdue => Warnings.Any(w => w.Kind is BackupStatus.LogChainBroken or BackupStatus.NoLogBackup or BackupStatus.LogOverdue);

    public string StatusText => Status switch
    {
        BackupStatus.NoFullBackup   => "No full backup",
        BackupStatus.LogChainBroken => "Log chain broken",
        BackupStatus.NoLogBackup    => "No log backup",
        BackupStatus.FullOverdue    => "Full overdue",
        BackupStatus.LogOverdue     => "Log overdue",
        BackupStatus.Ok             => "OK",
        _                           => StateText,
    };

    /// <summary>"Restoring", "Offline": sys.databases' state_desc, readable.</summary>
    public string StateText => Row.State.Length == 0 ? string.Empty
        : char.ToUpperInvariant(Row.State[0]) + Row.State[1..].ToLowerInvariant().Replace('_', ' ');

    /// <summary>Every warning, for the grid's Warnings column and the detail header.</summary>
    public string WarningsText => !IsOnline
        ? $"{StateText}: a database that isn't online can't be backed up, so it isn't checked."
        : Warnings.Count == 0 ? string.Empty : string.Join(" ", Warnings.Select(w => w.Text));

    public TimeSpan? FullAge => AgeOf(Full);
    public TimeSpan? DifferentialAge => AgeOf(Differential);
    public TimeSpan? LogAge => AgeOf(Log);

    public string FullAgeText         => FormatAge(FullAge);
    public string DifferentialAgeText => FormatAge(DifferentialAge);
    public string LogAgeText          => UsesLogBackups ? FormatAge(LogAge) : "n/a";

    /// <summary>
    /// The newest backup it could be restored to: in FULL or BULK_LOGGED recovery the last log
    /// backup (or a newer full or differential); otherwise the last full or differential. A
    /// differential needs a full to restore on top of. Null when there's nothing to restore.
    /// </summary>
    public DateTime? RecoveryPoint
    {
        get
        {
            if (Full is null)
                return null;

            var newest = Full.Finish;
            if (Differential is { } diff && diff.Finish > newest)
                newest = diff.Finish;
            if (UsesLogBackups && Log is { } log && log.Finish > newest)
                newest = log.Finish;
            return newest;
        }
    }

    /// <summary>
    /// The work that would be lost if the database were lost now, without a tail-log backup:
    /// the time since <see cref="RecoveryPoint"/>.
    /// </summary>
    public TimeSpan? DataAtRisk => RecoveryPoint is { } point ? Max(ServerNow - point, TimeSpan.Zero) : null;

    public string DataAtRiskText => DataAtRisk is { } risk ? FormatAge(risk) : Full is null ? "everything" : string.Empty;

    public string FullSizeText => Full?.Bytes is { } bytes ? HistorySettings.FormatSize(bytes) : string.Empty;


    private TimeSpan? AgeOf(BackupInfo? backup) =>
        backup is null ? null : Max(ServerNow - backup.Finish, TimeSpan.Zero);

    /// <summary>
    /// Works out what's wrong with a database's backups against <paramref name="rpo"/>.
    /// <paramref name="serverNow"/> is the server's local time, which msdb's times are in.
    /// </summary>
    public static DatabaseBackup From(DatabaseBackupRow row, DateTime serverNow, BackupRpo rpo)
    {
        var warnings = new List<BackupWarning>();
        var db = new DatabaseBackup(row, serverNow, warnings);
        if (!db.IsOnline)
            return db;

        if (row.Full is null)
        {
            warnings.Add(new(BackupStatus.NoFullBackup,
                $"No full backup since it was created or restored ({row.CreateDate:yyyy-MM-dd HH:mm}): there is nothing to restore it from." +
                (db.NeedsLogBackups ? " Log backups can't start until there is one." : "")));
            return db;
        }

        if (db.FullAge > rpo.FullMaxAge)
            warnings.Add(new(BackupStatus.FullOverdue,
                $"Last full backup {FormatAge(db.FullAge)} ago, more than the {BackupRpo.Format(rpo.FullMaxAge)} allowed."));

        if (!db.NeedsLogBackups)
            return db;

        if (row.LogChainKnown && !row.LogChainStarted)
            warnings.Add(new(BackupStatus.LogChainBroken,
                $"In {row.RecoveryModel} recovery, but its log chain hasn't started (e.g. it was switched from SIMPLE after " +
                "the last full backup), so no log backup can be taken and the log is truncated as in SIMPLE. A full or " +
                "differential backup starts the chain again."));
        else if (row.Log is null)
            warnings.Add(new(BackupStatus.NoLogBackup,
                $"In {row.RecoveryModel} recovery but never had a log backup: the log keeps growing, and only the last full " +
                "or differential backup can be restored."));
        else if (db.LogAge > rpo.LogMaxAge)
            warnings.Add(new(BackupStatus.LogOverdue,
                $"Last log backup {FormatAge(db.LogAge)} ago, more than the {BackupRpo.Format(rpo.LogMaxAge)} allowed."));

        return db;
    }

    /// <summary>"< 1 min", "45 min", "3 h 20 min", "2 d 4 h", "41 d": how long ago, to the unit that matters.</summary>
    public static string FormatAge(TimeSpan? age)
    {
        if (age is not { } span)
            return string.Empty;

        var culture = CultureInfo.CurrentCulture;
        if (span < TimeSpan.FromMinutes(1)) return "< 1 min";
        if (span < TimeSpan.FromHours(1))   return string.Format(culture, "{0} min", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1))
            return span.Minutes == 0
                ? string.Format(culture, "{0} h", (int)span.TotalHours)
                : string.Format(culture, "{0} h {1} min", (int)span.TotalHours, span.Minutes);
        if (span < TimeSpan.FromDays(10))
            return span.Hours == 0
                ? string.Format(culture, "{0} d", (int)span.TotalDays)
                : string.Format(culture, "{0} d {1} h", (int)span.TotalDays, span.Hours);
        return string.Format(culture, "{0:N0} d", (int)span.TotalDays);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>The databases and their backups, or why they couldn't be read.</summary>
/// <param name="Databases">Most urgent first (<see cref="BackupStatus"/>), then by name.</param>
/// <param name="ServerNow">The server's local time when they were read.</param>
/// <param name="Problem">Why the list is empty, or something that makes it less than it seems.</param>
public sealed record BackupList(
    IReadOnlyList<DatabaseBackup> Databases,
    BackupRpo Rpo,
    DateTime? ServerNow,
    string? Problem)
{
    /// <summary>False when nothing could be read (Azure SQL Database, no msdb permission).</summary>
    public bool Available { get; init; } = true;

    public static BackupList Unavailable(string problem, BackupRpo rpo) =>
        new([], rpo, null, problem) { Available = false };

    public int NoFullCount      => Databases.Count(d => d.Warnings.Any(w => w.Kind == BackupStatus.NoFullBackup));
    public int FullOverdueCount => Databases.Count(d => d.Warnings.Any(w => w.Kind == BackupStatus.FullOverdue));
    public int LogProblemCount  => Databases.Count(d => d.LogIsOverdue);
    public int NotCheckedCount  => Databases.Count(d => d.Status == BackupStatus.NotChecked);
    public int AttentionCount   => Databases.Count(d => d.NeedsAttention);

    /// <summary>Most urgent first, then by name.</summary>
    public static IReadOnlyList<DatabaseBackup> Sort(IEnumerable<DatabaseBackup> databases) => databases
        .OrderBy(d => d.Status)
        .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>
    /// Builds the list, and says so when databases are in an availability group: a backup taken
    /// on another replica is only in that replica's msdb.
    /// </summary>
    public static BackupList From(IEnumerable<DatabaseBackupRow> rows, DateTime serverNow, BackupRpo rpo)
    {
        var databases = Sort(rows.Select(r => DatabaseBackup.From(r, serverNow, rpo)));
        var inAg = databases.Count(d => d.Row.InAvailabilityGroup);
        return new BackupList(databases, rpo, serverNow, inAg == 0 ? null : AvailabilityGroupNote(inAg));
    }

    internal static string AvailabilityGroupNote(int databases) =>
        $"{databases:N0} database{(databases == 1 ? " is" : "s are")} in an availability group. A backup taken on another " +
        "replica is recorded in that replica's msdb, not this one, so it can show as missing here: check the replica " +
        "that takes the backups too.";
}
