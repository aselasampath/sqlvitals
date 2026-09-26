using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads database backups from msdb's backup history (#40): the newest full, differential and
/// log backup of each database in sys.databases. Read only; msdb keeps its times in the server's
/// local time, and so does the list.
/// </summary>
public class BackupRepository(IConfiguration configuration) : BaseRepository(configuration), IBackupRepository
{
    // SELECT denied on an msdb table (229), or no access to msdb at all (916).
    private static readonly int[] MsdbPermissionErrors = [229, 916];

    private const int AzureSqlDatabaseEdition = 5;

    // Kept to what every edition has, so Azure SQL Database gets its message rather than an error.
    internal const string ServerSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition,
               SYSDATETIME() AS ServerNow
        """;

    // One row per database except tempdb and snapshots, which can't be backed up. backupset is
    // matched by name, so only backups finished since the database was created (or restored,
    // which sets create_date) count: older ones are of an earlier database with the same name.
    // It is read once: ranked per database and type, the newest of each kept, then pivoted.
    // last_log_backup_lsn is NULL while a log backup can't be taken (SIMPLE, or no full backup
    // since the log chain was broken).
    internal const string DatabasesSql = """
        WITH ranked AS (
            SELECT d.database_id,
                   b.type,
                   b.backup_start_date,
                   b.backup_finish_date,
                   CAST(ISNULL(b.compressed_backup_size, b.backup_size) AS BIGINT) AS bytes,
                   CAST(b.is_copy_only AS INT) AS is_copy_only,
                   b.media_set_id,
                   ROW_NUMBER() OVER (PARTITION BY d.database_id, b.type
                                      ORDER BY b.backup_finish_date DESC, b.backup_set_id DESC) AS rn
            FROM msdb.dbo.backupset b
            JOIN sys.databases d
              ON d.name = b.database_name COLLATE DATABASE_DEFAULT
             AND b.backup_finish_date >= d.create_date
            WHERE b.type IN ('D', 'I', 'L')
        ),
        latest AS (
            SELECT r.database_id, r.type, r.backup_start_date, r.backup_finish_date, r.bytes, r.is_copy_only,
                   (SELECT TOP (1) mf.physical_device_name
                    FROM msdb.dbo.backupmediafamily mf
                    WHERE mf.media_set_id = r.media_set_id
                    ORDER BY mf.family_sequence_number) AS device
            FROM ranked r
            WHERE r.rn = 1
        ),
        pivoted AS (
            SELECT l.database_id,
                   MAX(CASE WHEN l.type = 'D' THEN l.backup_start_date END)  AS FullStart,
                   MAX(CASE WHEN l.type = 'D' THEN l.backup_finish_date END) AS FullFinish,
                   MAX(CASE WHEN l.type = 'D' THEN l.bytes END)              AS FullBytes,
                   MAX(CASE WHEN l.type = 'D' THEN l.is_copy_only END)       AS FullCopyOnly,
                   MAX(CASE WHEN l.type = 'D' THEN l.device END)             AS FullDevice,
                   MAX(CASE WHEN l.type = 'I' THEN l.backup_start_date END)  AS DiffStart,
                   MAX(CASE WHEN l.type = 'I' THEN l.backup_finish_date END) AS DiffFinish,
                   MAX(CASE WHEN l.type = 'I' THEN l.bytes END)              AS DiffBytes,
                   MAX(CASE WHEN l.type = 'I' THEN l.is_copy_only END)       AS DiffCopyOnly,
                   MAX(CASE WHEN l.type = 'I' THEN l.device END)             AS DiffDevice,
                   MAX(CASE WHEN l.type = 'L' THEN l.backup_start_date END)  AS LogStart,
                   MAX(CASE WHEN l.type = 'L' THEN l.backup_finish_date END) AS LogFinish,
                   MAX(CASE WHEN l.type = 'L' THEN l.bytes END)              AS LogBytes,
                   MAX(CASE WHEN l.type = 'L' THEN l.is_copy_only END)       AS LogCopyOnly,
                   MAX(CASE WHEN l.type = 'L' THEN l.device END)             AS LogDevice
            FROM latest l
            GROUP BY l.database_id
        )
        SELECT d.name                  AS Name,
               d.recovery_model_desc   AS RecoveryModel,
               d.state_desc            AS State,
               CAST(d.is_read_only AS INT) AS IsReadOnly,
               d.create_date           AS CreateDate,
               CASE WHEN d.replica_id IS NULL THEN 0 ELSE 1 END              AS InAvailabilityGroup,
               CASE WHEN drs.database_id IS NULL THEN 0 ELSE 1 END           AS LogChainKnown,
               CASE WHEN drs.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END   AS LogChainStarted,
               p.FullStart, p.FullFinish, p.FullBytes, p.FullCopyOnly, p.FullDevice,
               p.DiffStart, p.DiffFinish, p.DiffBytes, p.DiffCopyOnly, p.DiffDevice,
               p.LogStart,  p.LogFinish,  p.LogBytes,  p.LogCopyOnly,  p.LogDevice
        FROM sys.databases d
        LEFT JOIN sys.database_recovery_status drs ON drs.database_id = d.database_id
        LEFT JOIN pivoted p ON p.database_id = d.database_id
        WHERE d.database_id <> 2
          AND d.source_database_id IS NULL
        """;

    internal const string HistorySql = """
        SELECT TOP (@maxRows)
               b.type                                     AS Type,
               b.backup_start_date                        AS StartDate,
               b.backup_finish_date                       AS FinishDate,
               CAST(b.backup_size AS BIGINT)              AS Bytes,
               CAST(b.compressed_backup_size AS BIGINT)   AS CompressedBytes,
               CAST(b.is_copy_only AS INT)                AS CopyOnly,
               CAST(b.has_backup_checksums AS INT)        AS HasChecksums,
               CAST(b.is_damaged AS INT)                  AS IsDamaged,
               b.recovery_model                           AS RecoveryModel,
               b.user_name                                AS UserName,
               (SELECT TOP (1) mf.physical_device_name
                FROM msdb.dbo.backupmediafamily mf
                WHERE mf.media_set_id = b.media_set_id
                ORDER BY mf.family_sequence_number)       AS Device
        FROM msdb.dbo.backupset b
        WHERE b.database_name = @databaseName
          AND b.backup_finish_date IS NOT NULL
        ORDER BY b.backup_finish_date DESC, b.backup_set_id DESC
        """;

    public async Task<BackupList> GetBackupStatusAsync(BackupRpo rpo)
    {
        using var conn = CreateConnection();

        var server  = await QFirst(conn, ServerSql);
        int edition = server is null ? 0 : Convert.ToInt32(server.Edition);
        DateTime serverNow = server?.ServerNow as DateTime? ?? DateTime.Now;
        if (edition == AzureSqlDatabaseEdition)
            return BackupList.Unavailable(
                "Azure SQL Database backs up every database itself (full, differential and log backups, kept for " +
                "point-in-time restore) and records none of them in msdb. The Azure portal shows the restore points " +
                "under the database's Backups. SQL Server and Azure SQL Managed Instance are supported here.", rpo);

        List<dynamic> rows;
        try
        {
            rows = (await Q(conn, DatabasesSql)).ToList();
        }
        catch (WaitStatsException ex) when (MsdbPermissionErrors.Contains(ex.SqlErrorNumber))
        {
            return BackupList.Unavailable(
                "This login can't read the backup history in msdb. Members of sysadmin can; " +
                "for another login, create a user for it in msdb and GRANT SELECT on dbo.backupset and " +
                "dbo.backupmediafamily.", rpo);
        }

        return BackupList.From(rows.Select(r => (DatabaseBackupRow)ToRow(r)), serverNow, rpo);
    }

    public async Task<IReadOnlyList<BackupHistoryEntry>> GetBackupHistoryAsync(string databaseName, DateTime createDate)
    {
        using var conn = CreateConnection();
        var rows = await Q(conn, HistorySql, new { databaseName, maxRows = BackupHistoryEntry.MaxRows });
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
        Backup((DateTime?)r.LogStart,  (DateTime?)r.LogFinish,  (long?)r.LogBytes,  (int?)r.LogCopyOnly,  (string?)r.LogDevice));

    private static BackupInfo? Backup(DateTime? start, DateTime? finish, long? bytes, int? copyOnly, string? device) =>
        finish is { } end ? new BackupInfo(start ?? end, end, bytes, copyOnly == 1, device) : null;
}
