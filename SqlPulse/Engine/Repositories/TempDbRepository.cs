using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlPulse.Engine.Models;

namespace SqlPulse.Engine.Repositories;

public class TempDbRepository(IConfiguration configuration) : BaseRepository(configuration), ITempDbRepository
{
    public async Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync()
    {
        const string filesSql = """
            SELECT mf.name AS FileName, mf.type_desc AS FileType,
                mf.physical_name AS PhysicalPath,
                mf.size * 8 / 1024 AS FileSizeMB,
                ISNULL(FILEPROPERTY(mf.name,'SpaceUsed'),0) * 8 / 1024 AS SpaceUsedMB,
                (mf.size - ISNULL(FILEPROPERTY(mf.name,'SpaceUsed'),0)) * 8 / 1024 AS FreeSpaceMB,
                CAST(ISNULL(FILEPROPERTY(mf.name,'SpaceUsed'),0)*100.0/NULLIF(mf.size,0) AS DECIMAL(10,2)) AS UsedPct,
                CASE mf.is_percent_growth WHEN 1 THEN CAST(mf.growth AS VARCHAR)+'%'
                    ELSE CAST(mf.growth*8/1024 AS VARCHAR)+' MB' END AS AutoGrowth,
                GETDATE() AS CaptureTime
            FROM sys.master_files mf WHERE mf.database_id = 2
            ORDER BY mf.type_desc, mf.file_id
            """;

        const string sessionsSql = """
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
            WHERE su.session_id > 50
              AND (su.user_objects_alloc_page_count+su.internal_objects_alloc_page_count) > 0
            ORDER BY NetTempDBUsageKB DESC
            """;

        const string countersSql = """
            SELECT counter_name AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%Transactions%' AND instance_name='tempdb'
              AND counter_name IN ('Active Transactions','Version Store Size (KB)','Version Generation rate (KB/s)','Version Cleanup rate (KB/s)')
            UNION ALL
            SELECT counter_name, cntr_value, GETDATE()
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%General Statistics%'
              AND counter_name IN ('Active Temp Tables','Temp Tables Creation Rate','Temp Tables For Destruction')
            """;

        using var conn = CreateConnection();
        var files = (await Q(conn, filesSql)).Select(r => new TempDbFile(
            (string)r.FileName, (string)r.FileType, (string)r.PhysicalPath,
            (long)r.FileSizeMB, (long)r.SpaceUsedMB, (long)r.FreeSpaceMB,
            (double)(r.UsedPct ?? 0), (string)r.AutoGrowth, (DateTime)r.CaptureTime));

        var sessions = (await Q(conn, sessionsSql)).Select(r =>
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

        var counters = (await Q(conn, countersSql)).Select(r =>
            new TempDbCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        return (files, sessions, counters);
    }
}
