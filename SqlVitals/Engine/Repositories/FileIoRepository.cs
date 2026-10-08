using SqlVitals.Engine.Errors;
using SqlVitals.Engine.FileIo;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads every database file's I/O totals from sys.dm_io_virtual_file_stats (#41). The totals
/// are since each database came online; the File I/O page turns two readings into latency over
/// the time between them (<see cref="FileIoTracker"/>). Read only. Keeps which volume each file
/// is on, so sys.dm_os_volume_stats is only asked about files it hasn't seen.
/// </summary>
public class FileIoRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IFileIoRepository
{
    private const int AzureSqlDatabaseEdition = 5;

    internal static readonly string ServerSql = SqlScripts.Get("FileIo/Server");

    // The server's clock comes with the rows, so the time between two readings is the time
    // between the totals. 32767 is the hidden, read-only resource database.
    internal static readonly string FilesSql = SqlScripts.Get("FileIo/Files");

    // Azure SQL Database: only the current database's files, and no sys.master_files.
    internal static readonly string FilesAzureSql = SqlScripts.Get("FileIo/FilesAzure");

    // Online databases only: a file of one that isn't has no volume to ask about.
    internal static readonly string VolumesSql = SqlScripts.Get("FileIo/Volumes");

    private const string AzureVolume = "Azure-managed storage";

    // Which volume each file is on, and every file already asked about (some have no volume row).
    private readonly Dictionary<(int, int), string> _volumes = new();
    private readonly HashSet<(int, int)> _volumesAsked = new();
    private bool _volumeStatsFailed;

    public async Task<FileIoReading> ReadFileIoAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();

        dynamic? server;
        List<dynamic> rows;
        bool isAzure;
        try
        {
            server  = await QFirst(conn, ServerSql, cancellationToken)
                      ?? throw new InvalidOperationException("Failed to read the server's start time.");
            isAzure = Convert.ToInt32(server.Edition) == AzureSqlDatabaseEdition;
            rows    = (await Q(conn, isAzure ? FilesAzureSql : FilesSql, cancellationToken)).ToList();
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission)
        {
            return FileIoReading.Unavailable(
                "This login can't read file I/O statistics. Grant it VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE " +
                "on SQL Server 2022 and later); on Azure SQL Database, VIEW DATABASE STATE in the database.");
        }

        var files = rows.Select(r => new FileIoCounters(
            Convert.ToInt32(r.DatabaseId), (string)r.DatabaseName, Convert.ToInt32(r.FileId), (string)r.FileName,
            (string)r.FileType, (string)r.PhysicalName, string.Empty,
            Convert.ToInt64(r.Reads), Convert.ToInt64(r.BytesRead), Convert.ToInt64(r.ReadStallMs),
            Convert.ToInt64(r.Writes), Convert.ToInt64(r.BytesWritten), Convert.ToInt64(r.WriteStallMs),
            Convert.ToInt64(r.SizeBytes ?? 0L))).ToList();

        if (isAzure)
            files = files.Select(f => f with { Volume = AzureVolume }).ToList();
        else
        {
            await LearnVolumesAsync(conn, files, cancellationToken: cancellationToken);
            files = files.Select(f => f with
            {
                Volume = _volumes.TryGetValue(f.Key, out var volume) ? volume : FileIoCounters.VolumeFromPath(f.PhysicalName),
            }).ToList();
        }

        var serverTime = rows.Count > 0 ? (DateTime)rows[0].ServerTime : DateTime.Now;
        return new FileIoReading(serverTime, (DateTime)server.ServerStartTime, files, Note(isAzure, files));
    }

    // Asks sys.dm_os_volume_stats about files it hasn't been asked about. It isn't needed for
    // latency, so a server that won't answer gets volumes worked out from the file paths.
    private async Task LearnVolumesAsync(System.Data.IDbConnection conn, IReadOnlyList<FileIoCounters> files, CancellationToken cancellationToken)
    {
        if (_volumeStatsFailed || files.All(f => _volumesAsked.Contains(f.Key)))
            return;

        try
        {
            foreach (var r in await Q(conn, VolumesSql, cancellationToken))
                if ((string?)r.Volume is { Length: > 0 } volume)
                    _volumes[(Convert.ToInt32(r.DatabaseId), Convert.ToInt32(r.FileId))] = volume;
        }
        catch (WaitStatsException)
        {
            _volumeStatsFailed = true;
        }

        foreach (var f in files)
            _volumesAsked.Add(f.Key);
    }

    private static string? Note(bool isAzure, IReadOnlyList<FileIoCounters> files)
    {
        if (isAzure)
            return "Azure SQL Database shows only this database's files, on storage Azure manages: a slow file there " +
                   "usually means the service tier's I/O limit.";

        return files.Any(f => f.FileType.Length == 0)
            ? "This login can't see some files in sys.master_files, so they show as \"file n\" with no type or path, and " +
              "are held to the data file threshold. VIEW ANY DEFINITION shows them."
            : null;
    }
}
