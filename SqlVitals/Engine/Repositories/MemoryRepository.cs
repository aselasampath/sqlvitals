using System.Globalization;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Memory: grants, clerks and counters, and the memory KPI snapshot. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class MemoryRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IMemoryRepository
{
    // ── 9. Memory grants ──────────────────────────────────────────────
    public async Task<(IEnumerable<MemoryGrant>, IEnumerable<MemoryClerk>, IEnumerable<MemoryCounter>)> GetMemoryGrantsAsync(CancellationToken cancellationToken = default)
    {
        // SQL Server 2016+ (ProductMajorVersion >= 13) and Azure SQL have queue_id,
        // wait_order, is_next_candidate, wait_time_ms in sys.dm_exec_query_memory_grants.
        // Older on-prem versions (2014/2012) lack those columns — use NULL/0 placeholders.
        var grantsSqlFull = SqlScripts.Get("Memory/GrantsFull");

        // Compatible query for SQL Server 2014 and earlier (ProductMajorVersion <= 12):
        // queue_id, wait_order, is_next_candidate, wait_time_ms did not exist yet.
        var grantsSqlCompat = SqlScripts.Get("Memory/GrantsCompat");

        // Azure SQL DB and MI always support the full column set (ServerCapabilities.HasMemoryGrantQueue).
        var grantsSql = (await CapabilitiesAsync(cancellationToken)).HasMemoryGrantQueue ? grantsSqlFull : grantsSqlCompat;

        var clerksSql = SqlScripts.Get("Memory/Clerks");

        var countersSql = SqlScripts.Get("Memory/Counters");

        using var conn = CreateConnection();

        var grants = (await Q(conn, grantsSql, cancellationToken)).Select(r =>
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

        var clerks = (await Q(conn, clerksSql, cancellationToken)).Select(r =>
            new MemoryClerk((string)r.ClerkType, (string)r.ClerkName,
                (long)r.TotalPagesKB, (long)r.TotalPagesMB,
                (double)(r.PctOfTotalMemory ?? 0), (DateTime)r.CaptureTime));

        var counters = (await Q(conn, countersSql, cancellationToken)).Select(r =>
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

    /// <summary>Whether sys.dm_os_sys_info has sql_memory_model_desc.</summary>
    internal static readonly string MemorySnapshotProbeSql = SqlScripts.Get("Memory/MemorySnapshotProbe");

    internal const string MemoryModelColumnSql = "osi.sql_memory_model_desc";
    internal const string MemoryModelUnknownSql = "N'N/A'";

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
    private static readonly string MemorySnapshotOnPremSqlTemplate = SqlScripts.Get("Memory/MemorySnapshotOnPrem");

    // Azure SQL DB: sys.dm_os_sys_memory, sys.dm_os_process_memory and host memory
    // columns on sys.dm_os_sys_info are not exposed. Use counters only.
    private static readonly string MemorySnapshotAzureSql = SqlScripts.Get("Memory/MemorySnapshotAzure");

    public async Task<MemorySnapshot> GetMemorySnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();

        // Azure SQL DB lacks the DMVs; Managed Instance has them.
        // No probe row: leave the memory model out rather than risk error 207.
        bool isAzureSqlDb = (await CapabilitiesAsync(cancellationToken)).IsAzureSqlDatabase;
        var meta = await QFirst(conn, MemorySnapshotProbeSql, cancellationToken);
        bool hasMemoryModel = meta is not null && (int)meta.HasMemoryModel == 1;

        var r = await QFirst(conn, MemorySnapshotSql(isAzureSqlDb, hasMemoryModel), cancellationToken);
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
