using System.Globalization;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Memory: grants, clerks and counters, and the memory KPI snapshot. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class MemoryRepository(IConfiguration configuration) : BaseRepository(configuration), IMemoryRepository
{
    // ── 9. Memory grants ──────────────────────────────────────────────
    public async Task<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)> GetMemoryGrantsAsync()
    {
        // SQL Server 2016+ (ProductMajorVersion >= 13) and Azure SQL have queue_id,
        // wait_order, is_next_candidate, wait_time_ms in sys.dm_exec_query_memory_grants.
        // Older on-prem versions (2014/2012) lack those columns — use NULL/0 placeholders.
        const string grantsSqlFull = """
            SELECT TOP 25
                mg.session_id AS SessionId, mg.request_id AS RequestId,
                mg.scheduler_id AS SchedulerId, mg.dop AS DegreeOfParallelism,
                mg.request_time AS RequestTime, mg.grant_time AS GrantTime,
                DATEDIFF(ms,mg.request_time,ISNULL(mg.grant_time,GETDATE())) AS WaitForGrantMs,
                mg.requested_memory_kb AS RequestedMemoryKB,
                ISNULL(mg.granted_memory_kb,0) AS GrantedMemoryKB,
                ISNULL(mg.used_memory_kb,0) AS UsedMemoryKB,
                ISNULL(mg.max_used_memory_kb,0) AS MaxUsedMemoryKB,
                CASE WHEN ISNULL(mg.granted_memory_kb,0)>0
                     THEN CAST(ISNULL(mg.used_memory_kb,0)*100.0/NULLIF(mg.granted_memory_kb,0) AS DECIMAL(10,1))
                     ELSE NULL END AS MemGrantUsedPct,
                mg.ideal_memory_kb AS IdealMemoryKB,
                mg.required_memory_kb AS RequiredMemoryKB,
                mg.queue_id AS QueueId, mg.wait_order AS WaitOrder,
                CAST(mg.is_next_candidate AS BIT) AS IsNextCandidate,
                ISNULL(mg.wait_time_ms,0) AS WaitTimeMs,
                s.login_name AS LoginName, s.host_name AS HostName,
                s.program_name AS ProgramName,
                DB_NAME(r.database_id) AS DatabaseName,
                r.status AS RequestStatus, r.wait_type AS WaitType,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_query_memory_grants mg
            JOIN sys.dm_exec_sessions s ON mg.session_id = s.session_id
            LEFT JOIN sys.dm_exec_requests r ON mg.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(mg.plan_handle) qt
            ORDER BY ISNULL(mg.granted_memory_kb,mg.requested_memory_kb) DESC
            """;

        // Compatible query for SQL Server 2014 and earlier (ProductMajorVersion <= 12):
        // queue_id, wait_order, is_next_candidate, wait_time_ms did not exist yet.
        const string grantsSqlCompat = """
            SELECT TOP 25
                mg.session_id AS SessionId, mg.request_id AS RequestId,
                mg.scheduler_id AS SchedulerId, mg.dop AS DegreeOfParallelism,
                mg.request_time AS RequestTime, mg.grant_time AS GrantTime,
                DATEDIFF(ms,mg.request_time,ISNULL(mg.grant_time,GETDATE())) AS WaitForGrantMs,
                mg.requested_memory_kb AS RequestedMemoryKB,
                ISNULL(mg.granted_memory_kb,0) AS GrantedMemoryKB,
                ISNULL(mg.used_memory_kb,0) AS UsedMemoryKB,
                ISNULL(mg.max_used_memory_kb,0) AS MaxUsedMemoryKB,
                CASE WHEN ISNULL(mg.granted_memory_kb,0)>0
                     THEN CAST(ISNULL(mg.used_memory_kb,0)*100.0/NULLIF(mg.granted_memory_kb,0) AS DECIMAL(10,1))
                     ELSE NULL END AS MemGrantUsedPct,
                mg.ideal_memory_kb AS IdealMemoryKB,
                mg.required_memory_kb AS RequiredMemoryKB,
                NULL AS QueueId, NULL AS WaitOrder,
                CAST(0 AS BIT) AS IsNextCandidate,
                0 AS WaitTimeMs,
                s.login_name AS LoginName, s.host_name AS HostName,
                s.program_name AS ProgramName,
                DB_NAME(r.database_id) AS DatabaseName,
                r.status AS RequestStatus, r.wait_type AS WaitType,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_query_memory_grants mg
            JOIN sys.dm_exec_sessions s ON mg.session_id = s.session_id
            LEFT JOIN sys.dm_exec_requests r ON mg.session_id = r.session_id
            OUTER APPLY sys.dm_exec_sql_text(mg.plan_handle) qt
            ORDER BY ISNULL(mg.granted_memory_kb,mg.requested_memory_kb) DESC
            """;

        // Select the right variant based on SQL Server version.
        // Azure SQL DB (5) and MI (8) always support the full column set.
        const string sqlMeta = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) AS MajorVersion, CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition";
        var grantsSql = grantsSqlFull; // resolved after meta query below

        const string clerksSql = """
            SELECT TOP 15 mc.type AS ClerkType, mc.name AS ClerkName,
                SUM(mc.pages_kb) AS TotalPagesKB,
                SUM(mc.pages_kb)/1024 AS TotalPagesMB,
                CAST(SUM(mc.pages_kb)*100.0/NULLIF((SELECT SUM(pages_kb) FROM sys.dm_os_memory_clerks),0) AS DECIMAL(10,2)) AS PctOfTotalMemory,
                GETDATE() AS CaptureTime
            FROM sys.dm_os_memory_clerks mc
            GROUP BY mc.type, mc.name ORDER BY TotalPagesKB DESC
            """;

        const string countersSql = """
            SELECT counter_name AS CounterName, cntr_value AS CounterValue, GETDATE() AS CaptureTime
            FROM sys.dm_os_performance_counters
            WHERE object_name LIKE '%Memory Manager%'
              AND counter_name IN (
                  'Memory Grants Outstanding','Memory Grants Pending',
                  'Maximum Workspace Memory (KB)','Target Server Memory (KB)',
                  'Total Server Memory (KB)','Stolen Server Memory (KB)',
                  'Free Memory (KB)','Database Cache Memory (KB)')
            ORDER BY counter_name
            """;

        using var conn = CreateConnection();

        // Detect version once; default to full query if meta unavailable
        var meta = await QFirst(conn, sqlMeta);
        int majorVer = meta != null ? (int)meta.MajorVersion : 13;
        int eng = meta != null ? (int)meta.Edition : 0;
        bool supportsNewCols = majorVer >= 13 || eng == 5 || eng == 8; // 2016+ | Azure SQL DB | MI
        grantsSql = supportsNewCols ? grantsSqlFull : grantsSqlCompat;

        var grants = (await Q(conn, grantsSql)).Select(r =>
        {
            var row = (IDictionary<string, object>)r;
            object? Get(string k) => row.TryGetValue(k, out var v) && v is not DBNull ? v : null;
            static int ToInt(object? v) => v is null ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture);
            static long ToLong(object? v) => v is null ? 0L : Convert.ToInt64(v, CultureInfo.InvariantCulture);
            static bool ToBool(object? v) => v is not null && Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            static int? ToIntN(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
            static double? ToDblN(object? v) => v is null ? null : Convert.ToDouble(v, CultureInfo.InvariantCulture);

            long granted = ToLong(Get("GrantedMemoryKB"));
            bool pending = Get("GrantTime") is null;
            string sev = pending ? "Critical" : granted > 1_048_576 ? "Warning" : "Info";

            return new MemoryGrant(
                ToInt(Get("SessionId")),
                ToInt(Get("RequestId")),
                ToInt(Get("SchedulerId")),
                ToInt(Get("DegreeOfParallelism")),
                (DateTime)row["RequestTime"],
                Get("GrantTime") is DateTime gt ? gt : null,
                ToInt(Get("WaitForGrantMs")),
                ToLong(Get("RequestedMemoryKB")),
                granted,
                ToLong(Get("UsedMemoryKB")),
                ToLong(Get("MaxUsedMemoryKB")),
                ToDblN(Get("MemGrantUsedPct")),
                ToLong(Get("IdealMemoryKB")),
                ToLong(Get("RequiredMemoryKB")),
                ToIntN(Get("QueueId")),
                ToIntN(Get("WaitOrder")),
                ToBool(Get("IsNextCandidate")),
                ToInt(Get("WaitTimeMs")),
                Get("LoginName") as string,
                Get("HostName") as string,
                Get("ProgramName") as string,
                Get("DatabaseName") as string,
                Get("RequestStatus") as string,
                Get("WaitType") as string,
                Get("QueryText") as string,
                (DateTime)row["CaptureTime"],
                pending, sev);
        });

        var clerks = (await Q(conn, clerksSql)).Select(r =>
            new MemoryClerk((string)r.ClerkType, (string)r.ClerkName,
                (long)r.TotalPagesKB, (long)r.TotalPagesMB,
                (double)(r.PctOfTotalMemory ?? 0), (DateTime)r.CaptureTime));

        var counters = (await Q(conn, countersSql)).Select(r =>
            new MemoryCounter((string)r.CounterName, (long)r.CounterValue, (DateTime)r.CaptureTime));

        return (grants, clerks, counters);
    }

    // ── 23. Memory KPI snapshot ──────────────────────────────────────
    // Column sources:
    //   sys.dm_os_sys_info  (osi): physical_memory_kb; sql_memory_model_desc
    //   sys.dm_os_sys_memory (osm): available_physical_memory_kb, total_page_file_kb,
    //                               available_page_file_kb, system_memory_state_desc
    //   sys.dm_os_performance_counters (pc): memory KB counters
    //
    // sql_memory_model_desc came in a service pack (2012 SP4 and 2016 SP1, per the docs), so the
    // major version can't tell whether it's there: the probe asks sys.all_columns instead.
    // Naming a missing column fails the whole query with error 207.
    // Azure SQL DB (EngineEdition 5): sys.dm_os_sys_memory not exposed → counters only.

    /// <summary>The edition, and whether sys.dm_os_sys_info has sql_memory_model_desc.</summary>
    internal const string MemorySnapshotProbeSql = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT) AS Edition,
               CASE WHEN EXISTS (SELECT 1 FROM sys.all_columns
                                 WHERE object_id = OBJECT_ID(N'sys.dm_os_sys_info')
                                   AND name = N'sql_memory_model_desc')
                    THEN 1 ELSE 0 END                   AS HasMemoryModel
        """;

    private const string MemoryModelColumnSql = "osi.sql_memory_model_desc";
    private const string MemoryModelUnknownSql = "N'N/A'";

    /// <summary>
    /// The memory snapshot query for this server: counters only on Azure SQL Database, and
    /// the memory model read only where sys.dm_os_sys_info has it (N/A otherwise).
    /// </summary>
    internal static string MemorySnapshotSql(bool isAzureSqlDb, bool hasMemoryModel) =>
        isAzureSqlDb
            ? MemorySnapshotAzureSql
            : MemorySnapshotOnPremSqlTemplate.Replace("/*MEMORY_MODEL*/",
                hasMemoryModel ? MemoryModelColumnSql : MemoryModelUnknownSql);

    // The memory model is aggregated, not grouped by, so the GROUP BY stays the same
    // whichever expression replaces the placeholder. osi has one row.
    private const string MemorySnapshotOnPremSqlTemplate = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT
                osi.physical_memory_kb                                                                    AS PhysicalMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Total Server Memory (KB)'      THEN pc.cntr_value ELSE 0 END) AS TotalServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Target Server Memory (KB)'     THEN pc.cntr_value ELSE 0 END) AS TargetServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Database Cache Memory (KB)'    THEN pc.cntr_value ELSE 0 END) AS DatabaseCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'SQL Cache Memory (KB)'         THEN pc.cntr_value ELSE 0 END) AS SqlCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Granted Workspace Memory (KB)' THEN pc.cntr_value ELSE 0 END) AS GrantedWorkspaceMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Free Memory (KB)'              THEN pc.cntr_value ELSE 0 END) AS FreeMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Pending'         THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsPending,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Outstanding'     THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsOutstanding,
                osm.available_physical_memory_kb                                                          AS AvailablePhysicalKB,
                ISNULL(osm.total_page_file_kb, 0)                                                         AS TotalPageFileKB,
                ISNULL(osm.available_page_file_kb, 0)                                                     AS AvailablePageFileKB,
                ISNULL(osm.system_memory_state_desc, N'Unknown')                                          AS SystemMemoryState,
                ISNULL(MAX(/*MEMORY_MODEL*/), N'N/A')                                                     AS MemoryModel,
                GETDATE()                                                                                 AS CaptureTime
            FROM sys.dm_os_sys_info osi
            CROSS JOIN sys.dm_os_sys_memory osm
            CROSS JOIN sys.dm_os_performance_counters pc
            WHERE RTRIM(pc.object_name) LIKE N'%Memory Manager%'
              AND RTRIM(pc.counter_name) IN (
                N'Total Server Memory (KB)', N'Target Server Memory (KB)',
                N'Database Cache Memory (KB)', N'SQL Cache Memory (KB)',
                N'Granted Workspace Memory (KB)',
                N'Free Memory (KB)', N'Memory Grants Pending', N'Memory Grants Outstanding'
              )
            GROUP BY
                osi.physical_memory_kb,
                osm.available_physical_memory_kb, osm.total_page_file_kb,
                osm.available_page_file_kb, osm.system_memory_state_desc;
            """;

    // Azure SQL DB: sys.dm_os_sys_memory, sys.dm_os_process_memory and host memory
    // columns on sys.dm_os_sys_info are not exposed. Use counters only.
    private const string MemorySnapshotAzureSql = """
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SELECT
                CAST(0 AS BIGINT)                                                                         AS PhysicalMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Total Server Memory (KB)'      THEN pc.cntr_value ELSE 0 END) AS TotalServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Target Server Memory (KB)'     THEN pc.cntr_value ELSE 0 END) AS TargetServerMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Database Cache Memory (KB)'    THEN pc.cntr_value ELSE 0 END) AS DatabaseCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'SQL Cache Memory (KB)'         THEN pc.cntr_value ELSE 0 END) AS SqlCacheMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Granted Workspace Memory (KB)' THEN pc.cntr_value ELSE 0 END) AS GrantedWorkspaceMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Free Memory (KB)'              THEN pc.cntr_value ELSE 0 END) AS FreeMemoryKB,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Pending'         THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsPending,
                MAX(CASE WHEN RTRIM(pc.counter_name) = 'Memory Grants Outstanding'     THEN pc.cntr_value ELSE 0 END) AS MemoryGrantsOutstanding,
                CAST(0 AS BIGINT)                                                                         AS AvailablePhysicalKB,
                CAST(0 AS BIGINT)                                                                         AS TotalPageFileKB,
                CAST(0 AS BIGINT)                                                                         AS AvailablePageFileKB,
                N'N/A'                                                                                    AS SystemMemoryState,
                N'N/A'                                                                                    AS MemoryModel,
                GETDATE()                                                                                 AS CaptureTime
            FROM sys.dm_os_performance_counters pc
            WHERE RTRIM(pc.object_name) LIKE N'%Memory Manager%'
              AND RTRIM(pc.counter_name) IN (
                N'Total Server Memory (KB)', N'Target Server Memory (KB)',
                N'Database Cache Memory (KB)', N'SQL Cache Memory (KB)',
                N'Granted Workspace Memory (KB)',
                N'Free Memory (KB)', N'Memory Grants Pending', N'Memory Grants Outstanding'
              );
            """;

    public async Task<MemorySnapshot> GetMemorySnapshotAsync()
    {
        using var conn = CreateConnection();

        // EngineEdition 5 = Azure SQL DB; 8 = Managed Instance (MI has the DMVs).
        // No probe row: leave the memory model out rather than risk error 207.
        var meta = await QFirst(conn, MemorySnapshotProbeSql);
        bool isAzureSqlDb = meta is not null && (int)meta.Edition == 5;
        bool hasMemoryModel = meta is not null && (int)meta.HasMemoryModel == 1;

        var r = await QFirst(conn, MemorySnapshotSql(isAzureSqlDb, hasMemoryModel));
        if (r is null) return new MemorySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Unknown", "N/A", DateTime.Now);
        return new MemorySnapshot(
            (long)r.PhysicalMemoryKB,
            (long)r.TotalServerMemoryKB,
            (long)r.TargetServerMemoryKB,
            (long)r.DatabaseCacheMemoryKB,
            (long)r.SqlCacheMemoryKB,
            (long)r.GrantedWorkspaceMemoryKB,
            (long)r.FreeMemoryKB,
            (long)r.MemoryGrantsPending,
            (long)r.MemoryGrantsOutstanding,
            (long)r.AvailablePhysicalKB,
            (long)r.TotalPageFileKB,
            (long)r.AvailablePageFileKB,
            (string)r.SystemMemoryState,
            (string)r.MemoryModel,
            (DateTime)r.CaptureTime);
    }
}
