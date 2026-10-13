using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public class TempDbRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), ITempDbRepository
{
    // Run in tempdb's own context: FILEPROPERTY only sees the current database's files, and
    // sys.master_files holds tempdb's startup size rather than its size now.
    internal static readonly string FilesSql = SqlScripts.Get("TempDb/Files");

    internal static readonly string SessionsSql = SqlScripts.Get("TempDb/Sessions");

    // Point-in-time counters only (cntr_type 65792). The rates (version generation and cleanup,
    // temp table creation) are totals since the server started, which only make sense as the
    // difference between two readings: the Performance Counters page charts them that way.
    // The version store counters have no instance; Active Transactions is per database.
    internal static readonly string CountersSql = SqlScripts.Get("TempDb/Counters");

    public async Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var files = (await Q(conn, FilesSql, cancellationToken)).Select(r => new TempDbFile(
            (string)r.FileName, (string)r.FileType, (string)r.PhysicalPath,
            (long)r.FileSizeMB, (long)r.SpaceUsedMB, (long)r.FreeSpaceMB,
            (double)(r.UsedPct ?? 0), (string)r.AutoGrowth, (DateTime)r.CaptureTime));

        var sessions = (await Q(conn, SessionsSql, cancellationToken)).Select(r =>
        {
            long net = (long)r.NetTempDBUsageKB;
            string sev = net > 1_000_000 ? "Critical" : net > 100_000 ? "Warning" : "Info";
            return new TempDbSession(
                (int)r.SessionId, (long)r.UserObjAllocKB, (long)r.UserObjDeallocKB,
                (long)r.InternalObjAllocKB, (long)r.InternalObjDeallocKB, net,
                (string?)r.LoginName, (string?)r.HostName, (string?)r.ProgramName,
                (string?)r.WaitType, (string?)r.RequestStatus, (long)r.CpuTimeMs,
                (string?)r.DatabaseName, (string?)r.QueryText, (DateTime)r.CaptureTime, sev, (long)r.WaitTimeMs);
        });

        var counters = (await Q(conn, CountersSql, cancellationToken)).Select(r =>
            new TempDbCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        return (files, sessions, counters);
    }
}
