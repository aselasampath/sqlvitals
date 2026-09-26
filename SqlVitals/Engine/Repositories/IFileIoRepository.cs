using SqlVitals.Engine.FileIo;

namespace SqlVitals.Engine.Repositories;

/// <summary>Database file I/O totals, read from sys.dm_io_virtual_file_stats (#41).</summary>
public interface IFileIoRepository
{
    /// <summary>
    /// Every database file's reads, writes and I/O stall since its database came online, with the
    /// server's clock and start time to compare readings by. Never throws for a login without
    /// permission to read them: the reading then carries the reason.
    /// </summary>
    Task<FileIoReading> ReadFileIoAsync();
}
