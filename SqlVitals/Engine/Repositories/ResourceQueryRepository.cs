using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>The plan cache's costliest queries, an object's plan, and implicit conversions. Split from <see cref="WaitStatsRepository"/> (#47).</summary>
public class ResourceQueryRepository(IConfiguration configuration) : BaseRepository(configuration), IResourceQueryRepository
{
    // ── 13 + 14. Resource-intensive queries (logical reads + CPU) ─────
    public async Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
                       IEnumerable<ResourceIntensiveQuery> ByCpu,
                       IEnumerable<ResourceIntensiveQuery> ByHighestReads)>
        GetResourceIntensiveQueriesAsync(int topN = 20)
    {
        const string objectTypeSql = """
            CASE
                WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
                ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                              WHEN 'FN' THEN 'Scalar Func'
                                              WHEN 'TF' THEN 'Table Func'
                                              WHEN 'IF' THEN 'Inline Func'
                                              WHEN 'V'  THEN 'View'
                                              WHEN 'TR' THEN 'Trigger'
                                              ELSE type_desc END
                             FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
            END AS ObjectType
            """;
        _ = objectTypeSql; // used inline in the SQL literals below

        const string readsSql = """
            SELECT TOP (@TopN)
                SUBSTRING(qt.text,(qs.statement_start_offset/2)+1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset)/2)+1) AS QueryText,
                qs.execution_count AS ExecutionCount,
                qs.total_logical_reads AS TotalLogicalReads,
                CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
                qs.total_worker_time AS TotalCPUTime,
                CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
                qs.total_elapsed_time AS TotalElapsedTime,
                CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.last_execution_time AS LastExecutionTime,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(qt.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
                GETDATE() AS CaptureTime,
                CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
                CASE
                    WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
                    ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                                  WHEN 'FN' THEN 'Scalar Func'
                                                  WHEN 'TF' THEN 'Table Func'
                                                  WHEN 'IF' THEN 'Inline Func'
                                                  WHEN 'V'  THEN 'View'
                                                  WHEN 'TR' THEN 'Trigger'
                                                  ELSE type_desc END
                                 FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
                END AS ObjectType
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE qt.dbid = DB_ID()
            ORDER BY qs.total_logical_reads DESC
            """;

        const string cpuSql = """
            SELECT TOP (@TopN)
                SUBSTRING(qt.text,(qs.statement_start_offset/2)+1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(qt.text) ELSE qs.statement_end_offset END
                      - qs.statement_start_offset)/2)+1) AS QueryText,
                qs.execution_count AS ExecutionCount,
                qs.total_logical_reads AS TotalLogicalReads,
                CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
                qs.total_worker_time AS TotalCPUTime,
                CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
                qs.total_elapsed_time AS TotalElapsedTime,
                CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.last_execution_time AS LastExecutionTime,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(qt.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
                GETDATE() AS CaptureTime,
                CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
                CASE
                    WHEN qt.objectid IS NULL THEN 'Ad-Hoc'
                    ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                                  WHEN 'FN' THEN 'Scalar Func'
                                                  WHEN 'TF' THEN 'Table Func'
                                                  WHEN 'IF' THEN 'Inline Func'
                                                  WHEN 'V'  THEN 'View'
                                                  WHEN 'TR' THEN 'Trigger'
                                                  ELSE type_desc END
                                 FROM sys.objects WHERE object_id = qt.objectid), 'Ad-Hoc')
                END AS ObjectType
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) qt
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE qt.dbid = DB_ID()
            ORDER BY qs.total_worker_time DESC
            """;

        const string highestReadsSql = """
            SELECT TOP (@TopN)
                st.text AS QueryText,
                qs.execution_count AS ExecutionCount,
                qs.total_logical_reads AS TotalLogicalReads,
                CAST(ROUND(CAST(qs.total_logical_reads AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgLogicalReads,
                qs.total_worker_time AS TotalCPUTime,
                CAST(ROUND(CAST(qs.total_worker_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgCPUTime,
                qs.total_elapsed_time AS TotalElapsedTime,
                CAST(ROUND(CAST(qs.total_elapsed_time AS FLOAT) / NULLIF(qs.execution_count, 0), 0) AS BIGINT) AS AvgElapsedTime,
                qs.total_physical_reads AS TotalPhysicalReads,
                qs.total_logical_writes AS TotalLogicalWrites,
                qs.last_execution_time AS LastExecutionTime,
                qs.creation_time AS PlanCreationTime,
                DB_NAME(st.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(st.objectid,st.dbid),'') AS ObjectName,
                GETDATE() AS CaptureTime,
                CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan,
                qs.last_logical_reads AS LastLogicalReads,
                CASE
                    WHEN st.objectid IS NULL THEN 'Ad-Hoc'
                    ELSE ISNULL((SELECT CASE type WHEN 'P'  THEN 'Stored Proc'
                                                  WHEN 'FN' THEN 'Scalar Func'
                                                  WHEN 'TF' THEN 'Table Func'
                                                  WHEN 'IF' THEN 'Inline Func'
                                                  WHEN 'V'  THEN 'View'
                                                  WHEN 'TR' THEN 'Trigger'
                                                  ELSE type_desc END
                                 FROM sys.objects WHERE object_id = st.objectid), 'Ad-Hoc')
                END AS ObjectType
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            OUTER APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE st.dbid = DB_ID()
            ORDER BY qs.total_logical_reads DESC
            """;

        static ResourceIntensiveQuery Map(dynamic r) => new(
            (string?)r.QueryText, (string?)r.DatabaseName, (string?)r.ObjectName,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads, (long)r.AvgLogicalReads,
            (long)r.TotalCPUTime, (long)r.AvgCPUTime,
            (long)r.TotalElapsedTime, (long)r.AvgElapsedTime,
            (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (DateTime)r.LastExecutionTime, (DateTime)r.PlanCreationTime,
            (DateTime)r.CaptureTime, (string?)r.QueryPlan, null, (string?)r.ObjectType);

        static ResourceIntensiveQuery MapHighestReads(dynamic r) => new(
            (string?)r.QueryText, (string?)r.DatabaseName, (string?)r.ObjectName,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads, (long)r.AvgLogicalReads,
            (long)r.TotalCPUTime, (long)r.AvgCPUTime,
            (long)r.TotalElapsedTime, (long)r.AvgElapsedTime,
            (long)r.TotalPhysicalReads, (long)r.TotalLogicalWrites,
            (DateTime)r.LastExecutionTime, (DateTime)r.PlanCreationTime,
            (DateTime)r.CaptureTime,
            (string?)r.QueryPlan, (long?)r.LastLogicalReads, (string?)r.ObjectType);

        var p = new { TopN = topN };
        using var conn = CreateConnection();
        // These queries scan dm_exec_query_stats and fetch plans, which can be slow.
        // Increase timeout to 2 minutes to avoid timeouts on busy servers.
        const int extendedTimeout = 120;

        var byReads = (await Q(conn, readsSql, p, extendedTimeout)).Select(r => Map(r));
        var byCpu = (await Q(conn, cpuSql, p, extendedTimeout)).Select(r => Map(r));
        var byHighestReads = (await Q(conn, highestReadsSql, p, extendedTimeout)).Select(r => MapHighestReads(r));
        return (byReads, byCpu, byHighestReads);
    }

    public async Task<string?> GetObjectQueryPlanAsync(string objectName)
    {
        const string sql = """
            SELECT TOP 1 CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan
            FROM (
                SELECT plan_handle FROM sys.dm_exec_procedure_stats
                WHERE object_id = OBJECT_ID(@ObjectName) AND database_id = DB_ID()
                UNION ALL
                SELECT plan_handle FROM sys.dm_exec_function_stats
                WHERE object_id = OBJECT_ID(@ObjectName) AND database_id = DB_ID()
            ) x
            CROSS APPLY sys.dm_exec_query_plan(x.plan_handle) qp
            WHERE qp.query_plan IS NOT NULL
            """;
        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { ObjectName = objectName });
        return r is null ? null : (string?)r.QueryPlan;
    }

    // ── 17. Implicit conversions ──────────────────────────────────────
    public async Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync()
    {
        const string sql = """
            SELECT TOP 50
                DB_NAME(qt.dbid) AS DatabaseName,
                ISNULL(OBJECT_NAME(qt.objectid,qt.dbid),'') AS ObjectName,
                cp.usecounts AS ExecutionCount,
                cp.size_in_bytes/1024 AS PlanSizeKB,
                SUBSTRING(REPLACE(REPLACE(ISNULL(qt.text,''),CHAR(13),' '),CHAR(10),' '),1,500) AS QueryText,
                GETDATE() AS CaptureTime
            FROM sys.dm_exec_cached_plans cp
            CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) qt
            WHERE CAST(qp.query_plan AS NVARCHAR(MAX)) LIKE '%CONVERT_IMPLICIT%'
            ORDER BY cp.usecounts DESC
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql)).Select(r => new ImplicitConversion(
            (string?)r.DatabaseName, (string?)r.ObjectName,
            (int)r.ExecutionCount, (long)r.PlanSizeKB,
            (string?)r.QueryText, (DateTime)r.CaptureTime));
    }
}
