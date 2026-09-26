namespace SqlVitals.Engine.FileIo;

/// <summary>
/// One file's row of sys.dm_io_virtual_file_stats: running totals since its database came
/// online, which is usually when SQL Server started. Only the change between two of them says
/// how the storage is doing now.
/// </summary>
/// <param name="FileType">sys.master_files' type_desc: ROWS, LOG, FILESTREAM or FULLTEXT; empty when the login can't see it.</param>
/// <param name="Volume">The volume or mount point the file is on, from sys.dm_os_volume_stats, or worked out from its path.</param>
public sealed record FileIoCounters(
    int    DatabaseId,
    string DatabaseName,
    int    FileId,
    string FileName,
    string FileType,
    string PhysicalName,
    string Volume,
    long   Reads,
    long   BytesRead,
    long   ReadStallMs,
    long   Writes,
    long   BytesWritten,
    long   WriteStallMs,
    long   SizeBytes)
{
    public bool IsLog => FileType == "LOG";

    /// <summary>Which file this is from one reading to the next.</summary>
    public (int DatabaseId, int FileId) Key => (DatabaseId, FileId);

    /// <summary>
    /// "D:\", "\\filer\sql\", "https://account.blob.core.windows.net/data/": the volume a file is
    /// on, from its path, when sys.dm_os_volume_stats can't say. A mount point shows as its drive;
    /// anything else as its folder.
    /// </summary>
    public static string VolumeFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        path = path.Trim();

        // D:\Data\Sales.mdf
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return char.ToUpperInvariant(path[0]) + @":\";

        // \\filer\sql\Sales.mdf: the share.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}\" : path;
        }

        // /var/opt/mssql/data/Sales.mdf, or a blob URL on Managed Instance: the folder.
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..(slash + 1)] : path;
    }
}

/// <summary>
/// Every database file's I/O totals at one moment, or why they couldn't be read.
/// </summary>
/// <param name="ServerTime">The server's clock when the files were read.</param>
/// <param name="ServerStartTime">When SQL Server started: the totals start again from zero when it changes.</param>
/// <param name="Note">Something that makes the list less than it seems (Azure SQL Database sees only its own files).</param>
public sealed record FileIoReading(
    DateTime                       ServerTime,
    DateTime                       ServerStartTime,
    IReadOnlyList<FileIoCounters>  Files,
    string?                        Note = null)
{
    /// <summary>Why nothing could be read (no permission); null when the files were read.</summary>
    public string? Problem { get; init; }

    public bool Available => Problem is null;

    public static FileIoReading Unavailable(string problem) =>
        new(default, default, []) { Problem = problem };
}
