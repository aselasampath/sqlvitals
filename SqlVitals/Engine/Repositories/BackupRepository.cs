using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads database backups from msdb's backup history (#40): the newest full, differential and
/// log backup of each database in sys.databases. Read only; msdb keeps its times in the server's
/// local time, and so does the list.
/// </summary>
public class BackupRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IBackupRepository
{
    // The server's clock; the edition comes from the shared capabilities (#46).
    internal static readonly string ServerSql = SqlScripts.Get("Backup/Server");

    // One row per database except tempdb and snapshots, which can't be backed up. backupset is
    // matched by name, so only backups finished since the database was created (or restored,
    // which sets create_date) count: older ones are of an earlier database with the same name.
    // It is read once: ranked per database and type, the newest of each kept, then pivoted.
    // last_log_backup_lsn is NULL while a log backup can't be taken (SIMPLE, or no full backup
    // since the log chain was broken). @windowMinutes, for the health check (#174), reads only
    // the backups that finished within it; the page passes NULL for all of them.
    internal static readonly string DatabasesSql = SqlScripts.Get("Backup/Databases");

    internal static readonly string HistorySql = SqlScripts.Get("Backup/History");

    public Task<BackupList> GetBackupStatusAsync(BackupRpo rpo, CancellationToken cancellationToken = default) =>
        ReadAsync(rpo, window: null, cancellationToken);

    /// <summary>
    /// The databases past <paramref name="rpo"/>, as the page judges them, for the health check
    /// (#174). It runs every few minutes, so it reads only the backups that finished within the
    /// RPO's longer age: an older one is past it anyway, so the databases that need attention are
    /// the same, only told as having none rather than one overdue.
    /// </summary>
    internal Task<BackupList> GetRecentBackupStatusAsync(BackupRpo rpo, CancellationToken cancellationToken = default) =>
        ReadAsync(rpo, rpo.FullMaxAge > rpo.LogMaxAge ? rpo.FullMaxAge : rpo.LogMaxAge, cancellationToken);

    private async Task<BackupList> ReadAsync(BackupRpo rpo, TimeSpan? window, CancellationToken cancellationToken)
    {
        using var conn = CreateConnection();

        if ((await CapabilitiesAsync(cancellationToken)).IsAzureSqlDatabase)
            return BackupList.Unavailable(
                "Azure SQL Database backs up every database itself (full, differential and log backups, kept for " +
                "point-in-time restore) and records none of them in msdb. The Azure portal shows the restore points " +
                "under the database's Backups. SQL Server and Azure SQL Managed Instance are supported here.", rpo);

        var server = await QFirst(conn, ServerSql, cancellationToken);
        DateTime serverNow = server?.ServerNow as DateTime? ?? DateTime.Now;

        List<dynamic> rows;
        try
        {
            rows = (await Q(conn, DatabasesSql, cancellationToken,
                            new { windowMinutes = window is { } w ? (int?)Math.Ceiling(w.TotalMinutes) : null })).ToList();
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission)
        {
            return BackupList.Unavailable(
                "This login can't read the backup history in msdb. Members of sysadmin can; " +
                "for another login, create a user for it in msdb and GRANT SELECT on dbo.backupset and " +
                "dbo.backupmediafamily.", rpo);
        }

        return BackupList.From(rows.Select(r => (DatabaseBackupRow)ToRow(r)), serverNow, rpo);
    }

    public async Task<IReadOnlyList<BackupHistoryEntry>> GetBackupHistoryAsync(string databaseName, DateTime createDate, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var rows = await Q(conn, HistorySql, cancellationToken, new { databaseName, maxRows = BackupHistoryEntry.MaxRows });
        return rows.Select(r => new BackupHistoryEntry(
            (string)r.Type,
            (DateTime)r.StartDate,
            (DateTime)r.FinishDate,
            (long?)r.Bytes,
            (long?)r.CompressedBytes,
            (int)r.CopyOnly == 1,
            (int)r.HasChecksums == 1,
            (int)r.IsDamaged == 1,
            (string?)r.RecoveryModel,
            (string?)r.UserName,
            (string?)r.Device,
            (DateTime)r.FinishDate < createDate)).ToList();
    }

    private static DatabaseBackupRow ToRow(dynamic r) => new(
        (string)r.Name,
        (string?)r.RecoveryModel ?? string.Empty,
        (string?)r.State ?? string.Empty,
        (int)r.IsReadOnly == 1,
        (DateTime)r.CreateDate,
        (int)r.InAvailabilityGroup == 1,
        (int)r.LogChainKnown == 1,
        (int)r.LogChainStarted == 1,
        Backup((DateTime?)r.FullStart, (DateTime?)r.FullFinish, (long?)r.FullBytes, (int?)r.FullCopyOnly, (string?)r.FullDevice),
        Backup((DateTime?)r.DiffStart, (DateTime?)r.DiffFinish, (long?)r.DiffBytes, (int?)r.DiffCopyOnly, (string?)r.DiffDevice),
        Backup((DateTime?)r.LogStart,  (DateTime?)r.LogFinish,  (long?)r.LogBytes,  (int?)r.LogCopyOnly,  (string?)r.LogDevice),
        (int?)r.PreferredBackupReplica != 0);

    private static BackupInfo? Backup(DateTime? start, DateTime? finish, long? bytes, int? copyOnly, string? device) =>
        finish is { } end ? new BackupInfo(start ?? end, end, bytes, copyOnly == 1, device) : null;
}
