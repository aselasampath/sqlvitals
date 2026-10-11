using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads the slow health checks (#174) on the history's detail schedule: free space per volume from
/// sys.dm_os_volume_stats, the databases past their backup RPO from msdb's backup history (the
/// Backups page's query), and the Agent jobs that failed since the last read from msdb's job history.
/// Each part is read on its own, so one the login can't read leaves the others.
/// </summary>
public class ServerChecksRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IServerChecksRepository
{
    // One row per file of an online database, with its volume's size and free space. Grouped
    // into volumes here (ToVolumes): on Linux the mount point is NULL, so the file's path decides.
    internal static readonly string VolumesSql = SqlScripts.Get("ServerChecks/Volumes");

    // The newest job history row, read first: failures after it wait for the next read. With the
    // server's name, since a row id means something only in that server's msdb.
    internal static readonly string JobHistoryEndSql = SqlScripts.Get("ServerChecks/JobHistoryEnd");

    // Step 0 rows are a job's outcome; run_status 0 is failed (3, cancelled, isn't). After the
    // last read's row, or on the first read, ended within the look-back on the server's clock; a
    // run_date and run_time that don't make a time (TRY_CONVERT) are left out, never an error.
    internal static readonly string FailedJobsSql = SqlScripts.Get("ServerChecks/FailedJobs");

    // The Backups page's reader, for its rules on what is past the RPO (#40).
    private readonly BackupRepository _backups = new(target, capabilities);

    internal const string AzureVolumes = "Azure SQL Database manages its storage: there are no volumes to read.";
    internal const string AzureBackups = "Azure SQL Database backs up every database itself and records none of them in msdb.";
    internal const string AzureJobs    = "Azure SQL Database has no SQL Server Agent.";

    internal const string VolumesPermission =
        "This login can't read free disk space: it needs VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on SQL Server " +
        "2022 and later), and VIEW ANY DEFINITION to see every database's files.";
    internal const string BackupsPermission =
        "This login can't read the backup history in msdb: create a user for it in msdb and GRANT SELECT on dbo.backupset " +
        "and dbo.backupmediafamily.";
    internal const string JobsPermission =
        "This login can't read SQL Server Agent's job history in msdb: create a user for it in msdb and GRANT SELECT on " +
        "dbo.sysjobs and dbo.sysjobhistory.";

    private const double BytesPerGb = 1024d * 1024 * 1024;

    public async Task<ServerCheckFigures> GetServerChecksAsync(ServerChecksRequest request, CancellationToken cancellationToken = default)
    {
        if ((await CapabilitiesAsync(cancellationToken)).IsAzureSqlDatabase)
            return new ServerCheckFigures(null, AzureVolumes, null, AzureBackups, null, AzureJobs);

        using var conn = CreateConnection();

        var (volumes, volumesNoData) = await PartAsync(async () =>
            ToVolumes(await Q(conn, VolumesSql, cancellationToken)), VolumesPermission, cancellationToken);

        BackupList? backups = null;
        var (pastRpo, backupsNoData) = await PartAsync(async () =>
        {
            backups = await _backups.GetRecentBackupStatusAsync(request.Rpo, cancellationToken);
            return PastRpo(backups);
        }, BackupsPermission, cancellationToken);
        // The page's reader says why it read nothing: msdb's history the login can't read.
        if (backups is { Available: false } unavailable)
            (pastRpo, backupsNoData) = (null, unavailable.Problem);

        long?   lastInstance = null;
        string? jobsServer   = null;
        var (failed, jobsNoData) = await PartAsync(async () =>
        {
            var end = await QFirst(conn, JobHistoryEndSql, cancellationToken);
            jobsServer   = end?.ServerName as string;
            lastInstance = (long?)end?.LastInstanceId;
            if (lastInstance is null)
            {
                lastInstance = 0;
                return (IReadOnlyList<string>)[];
            }
            var rows = await Q(conn, FailedJobsSql, cancellationToken, new
            {
                upToInstanceId  = lastInstance,
                afterInstanceId = AfterInstanceId(request, jobsServer, lastInstance.Value),
                lookBackSeconds = (int)Math.Clamp(request.FirstLookBack.TotalSeconds, 0, int.MaxValue),
            });
            return (IReadOnlyList<string>)rows.Select(r => (string)r.Name).ToList();
        }, JobsPermission, cancellationToken);

        return new ServerCheckFigures(volumes, volumesNoData, pastRpo, backupsNoData, failed, jobsNoData,
                                      failed is null ? null : lastInstance, failed is null ? null : jobsServer);
    }

    /// <summary>
    /// The job history row failures are counted after: the last read's, if it was on this server
    /// and its history hasn't started again since. After a failover to another replica, or a
    /// history cleared and refilled, the read starts again as a first one: failures within the look-back.
    /// </summary>
    internal static long? AfterInstanceId(ServerChecksRequest request, string? server, long lastInstance) =>
        request.JobsAfterInstanceId is { } after &&
        after <= lastInstance &&
        (request.JobsServer is null || string.Equals(request.JobsServer, server, StringComparison.OrdinalIgnoreCase))
            ? after
            : null;

    /// <summary>
    /// The volumes query's rows (one per file) as volumes: by mount point, or where SQL Server on
    /// Linux reports none, by the file's path as the File I/O page names it. A volume's size is the
    /// largest seen, its free space the least; a row with no size is left out.
    /// </summary>
    internal static IReadOnlyList<VolumeSpace> ToVolumes(IEnumerable<dynamic> rows) => rows
        .Select(r => (Volume: (string?)r.Volume is { Length: > 0 } mount ? mount : FileIoCounters.VolumeFromPath((string?)r.PhysicalName),
                      Total: (long?)r.TotalBytes ?? 0, Free: (long?)r.FreeBytes ?? 0))
        .Where(r => r.Volume.Length > 0 && r.Total > 0)
        .GroupBy(r => r.Volume, StringComparer.OrdinalIgnoreCase)
        .Select(g => new VolumeSpace(g.First().Volume, g.Max(r => r.Total) / BytesPerGb, g.Min(r => r.Free) / BytesPerGb))
        .OrderBy(v => v.Volume, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The databases a backup check counts (#174): those the Backups page says need attention, but
    /// in an availability group only on the replica that should take the backups.
    /// </summary>
    internal static IReadOnlyList<string> PastRpo(BackupList list) =>
        list.Databases.Where(d => d.NeedsAttention && d.Row.PreferredBackupReplica).Select(d => d.Name).ToList();

    // One part of the read: what it returned, or null and why not. Cancelling still throws.
    private static async Task<(T? Value, string? NoData)> PartAsync<T>(Func<Task<T>> read, string permission, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return (await read(), null);
        }
        catch (WaitStatsException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, ex.Failure.IsPermission ? permission
                        : ex.Failure.Kind == SqlFailureKind.LockTimeout ? "The read waited too long for a lock another session holds."
                        : $"Couldn't be read: {(ex.InnerException ?? ex).Message}");
        }
    }
}
