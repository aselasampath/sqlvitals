using SqlVitals.Engine.Backups;

namespace SqlVitals.Engine.Repositories;

/// <summary>Database backups and the RPO warnings about them, read from msdb (#40).</summary>
public interface IBackupRepository
{
    /// <summary>
    /// Every database with its newest full, differential and log backup, checked against
    /// <paramref name="rpo"/>. Never throws for Azure SQL Database or a login that can't read
    /// msdb's backup history: the list then carries the reason.
    /// </summary>
    Task<BackupList> GetBackupStatusAsync(BackupRpo rpo);

    /// <summary>
    /// The backups msdb keeps for one database, newest first, at most
    /// <see cref="BackupHistoryEntry.MaxRows"/>. <paramref name="createDate"/> marks the ones
    /// from before it was created or restored.
    /// </summary>
    Task<IReadOnlyList<BackupHistoryEntry>> GetBackupHistoryAsync(string databaseName, DateTime createDate);
}
