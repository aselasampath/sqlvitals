using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public class TempDbRepository(IConfiguration configuration) : BaseRepository(configuration), ITempDbRepository
{
    // Run in tempdb's own context: FILEPROPERTY only sees the current database's files, and
    // sys.master_files holds tempdb's startup size rather than its size now.
    internal const string FilesSql = """
        EXEC tempdb.sys.sp_executesql N'
            SELECT f.name AS FileName, f.type_desc AS FileType,
                f.physical_name AS PhysicalPath,
                CAST(f.size AS BIGINT) * 8 / 1024 AS FileSizeMB,
                CAST(ISNULL(FILEPROPERTY(f.name, ''SpaceUsed''), 0) AS BIGINT) * 8 / 1024 AS SpaceUsedMB,
                CAST(f.size - ISNULL(FILEPROPERTY(f.name, ''SpaceUsed''), 0) AS BIGINT) * 8 / 1024 AS FreeSpaceMB,
                CAST(ISNULL(FILEPROPERTY(f.name, ''SpaceUsed''), 0) * 100.0 / NULLIF(f.size, 0) AS DECIMAL(10,2)) AS UsedPct,
                CASE f.is_percent_growth WHEN 1 THEN CAST(f.growth AS VARCHAR(10)) + ''%''
                    ELSE CAST(CAST(f.growth AS BIGINT) * 8 / 1024 AS VARCHAR(20)) + '' MB'' END AS AutoGrowth,
                GETDATE() AS CaptureTime
            FROM sys.database_files f
            ORDER BY f.type_desc, f.file_id;';
        """;

    internal const string SessionsSql = """
        SELECT TOP 20 su.session_id AS SessionId,
            su.user_objects_alloc_page_count*8 AS UserObjAllocKB,
            su.user_objects_dealloc_page_count*8 AS UserObjDeallocKB,
            su.internal_objects_alloc_page_count*8 AS InternalObjAllocKB,
            su.internal_objects_dealloc_page_count*8 AS InternalObjDeallocKB,
            (su.user_objects_alloc_page_count+su.internal_objects_alloc_page_count
             -su.user_objects_dealloc_page_count-su.internal_objects_dealloc_page_count)*8 AS NetTempDBUsageKB,
            s.login_name AS LoginName, s.host_name AS HostName,
            s.program_name AS ProgramName,
            r.wait_type AS WaitType, r.status AS RequestStatus,
            ISNULL(r.cpu_time,0) AS CpuTimeMs,
            DB_NAME(r.database_id) AS DatabaseName,
            SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
            GETDATE() AS CaptureTime
        FROM sys.dm_db_session_space_usage su
        JOIN sys.dm_exec_sessions s ON su.session_id = s.session_id
        LEFT JOIN sys.dm_exec_requests r ON su.session_id = r.session_id
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) qt
        WHERE s.is_user_process = 1
          AND (su.user_objects_alloc_page_count+su.internal_objects_alloc_page_count) > 0
        ORDER BY NetTempDBUsageKB DESC
        """;

    // Point-in-time counters only (cntr_type 65792). The rates (version generation and cleanup,
    // temp table creation) are totals since the server started, which only make sense as the
    // difference between two readings: the Performance Counters page charts them that way.
    // The version store counters have no instance; Active Transactions is per database.
    internal const string CountersSql = """
        SELECT RTRIM(counter_name) AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
        FROM sys.dm_os_performance_counters
        WHERE cntr_type = 65792
          AND (   (object_name LIKE N'%:Transactions%'
                   AND counter_name IN (N'Version Store Size (KB)', N'Free Space in tempdb (KB)'))
               OR (object_name LIKE N'%:Databases%' AND instance_name = N'tempdb'
                   AND counter_name = N'Active Transactions')
               OR (object_name LIKE N'%:General Statistics%'
                   AND counter_name IN (N'Active Temp Tables', N'Temp Tables For Destruction')))
        """;

    public async Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync()
    {
        using var conn = CreateConnection();
        var files = (await Q(conn, FilesSql)).Select(r => new TempDbFile(
            (string)r.FileName, (string)r.FileType, (string)r.PhysicalPath,
            (long)r.FileSizeMB, (long)r.SpaceUsedMB, (long)r.FreeSpaceMB,
            (double)(r.UsedPct ?? 0), (string)r.AutoGrowth, (DateTime)r.CaptureTime));

        var sessions = (await Q(conn, SessionsSql)).Select(r =>
        {
            long net = (long)r.NetTempDBUsageKB;
            string sev = net > 1_000_000 ? "Critical" : net > 100_000 ? "Warning" : "Info";
            return new TempDbSession(
                (int)r.SessionId, (long)r.UserObjAllocKB, (long)r.UserObjDeallocKB,
                (long)r.InternalObjAllocKB, (long)r.InternalObjDeallocKB, net,
                (string?)r.LoginName, (string?)r.HostName, (string?)r.ProgramName,
                (string?)r.WaitType, (string?)r.RequestStatus, (long)r.CpuTimeMs,
                (string?)r.DatabaseName, (string?)r.QueryText, (DateTime)r.CaptureTime, sev);
        });

        var counters = (await Q(conn, CountersSql)).Select(r =>
            new TempDbCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        return (files, sessions, counters);
    }
}
