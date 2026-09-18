using System.Data;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public class PlanCacheHealthRepository(IConfiguration configuration)
    : BaseRepository(configuration), IPlanCacheHealthRepository
{
    public async Task<(
        PlanCacheHealthSummary Summary,
        IEnumerable<MultiPlanQuery> MultiPlanQueries,
        IEnumerable<KeyLookupQuery> KeyLookupQueries,
        IEnumerable<CardinalityMisestimate> CardinalityMisestimates,
        IEnumerable<SkewedParallelQuery> SkewedParallelQueries,
        IEnumerable<PlanGuide> PlanGuides
    )> GetPlanCacheHealthAsync()
    {
        using var conn = CreateConnection();

        // ── 1. Summary KPIs ──────────────────────────────────────────────────
        // Single SELECT with subqueries — avoids multi-statement DECLARE issues with Dapper.
        // Join hints detected via SQL text (OPTION + LOOP/HASH/MERGE JOIN keywords) rather
        // than scanning full plan XML, which would scan thousands of large XML blobs and time out.
        const string summarySql = """
            SELECT
                -- Cache erased: server restarted < 24h ago OR oldest multi-use plan < 1h old
                CAST(
                    CASE
                        WHEN DATEDIFF(HOUR, si.sqlserver_start_time, GETDATE()) < 24 THEN 1
                        WHEN (
                            SELECT MIN(qs2.creation_time)
                            FROM sys.dm_exec_query_stats qs2
                            WHERE qs2.execution_count > 1
                        ) > DATEADD(MINUTE, -60, GETDATE()) THEN 1
                        ELSE 0
                    END
                AS BIT)                                                      AS PlanCacheErasedRecently,

                -- Parameterisation failures: same query_hash with > 5 distinct plans
                (SELECT COUNT(*)
                 FROM (
                     SELECT query_hash
                     FROM sys.dm_exec_query_stats
                     WHERE query_hash <> 0x0000000000000000
                     GROUP BY query_hash
                     HAVING COUNT(DISTINCT plan_handle) > 5
                 ) t)                                                        AS MultiPlanQueryCount,

                -- Total cached plans
                (SELECT COUNT(*) FROM sys.dm_exec_cached_plans)             AS TotalCachedPlans,

                -- Active plan guides
                (SELECT COUNT(*) FROM sys.plan_guides WHERE is_disabled = 0) AS PlanGuidesEnabled,

                -- Join hints: scan SQL text for OPTION (...JOIN) keywords — fast, no XML needed
                (SELECT COUNT(DISTINCT qs3.query_hash)
                 FROM sys.dm_exec_query_stats qs3
                 CROSS APPLY sys.dm_exec_sql_text(qs3.sql_handle) st3
                 WHERE st3.text LIKE '%OPTION%'
                   AND (  st3.text LIKE '%LOOP JOIN%'
                       OR st3.text LIKE '%HASH JOIN%'
                       OR st3.text LIKE '%MERGE JOIN%')
                )                                                            AS JoinHintCount
            FROM sys.dm_os_sys_info si;
            """;

        // ── 2. Multi-plan queries (parameterisation failure) ─────────────────
        // Same query hash with > 5 distinct plans. Top 30 by plan count.
        const string multiPlanSql = """
            SELECT TOP 30
                pc                         AS PlanCount,
                total_exec                 AS TotalExecutions,
                CAST(query_hash AS VARCHAR(18)) AS QueryHash,
                query_text                 AS QueryText,
                db_name                    AS DatabaseName
            FROM (
                SELECT
                    COUNT(DISTINCT qs.plan_handle)          AS pc,
                    SUM(qs.execution_count)                 AS total_exec,
                    qs.query_hash,
                    MIN(SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400)) AS query_text,
                    MIN(DB_NAME(st.dbid))                   AS db_name
                FROM sys.dm_exec_query_stats qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
                WHERE qs.query_hash <> 0x0000000000000000
                GROUP BY qs.query_hash
                HAVING COUNT(DISTINCT qs.plan_handle) > 5
            ) t
            ORDER BY pc DESC;
            """;

        // ── 3. RID / Key Lookups (plan cache search) ─────────────────────────
        // Pre-filter to TOP 500 queries by logical reads before scanning plan XML.
        // This limits the expensive XML scan to the most I/O-intensive plans only.
        // Uses dm_exec_text_query_plan (already NVARCHAR(MAX)) to avoid double CAST.
        const string keyLookupSql = """
            SELECT TOP 40
                CASE
                    WHEN tqp.query_plan LIKE '%RID Lookup%' THEN 'RID Lookup'
                    ELSE 'Key Lookup'
                END                               AS LookupType,
                qs.execution_count                AS ExecutionCount,
                qs.total_logical_reads            AS TotalLogicalReads,
                CAST(qs.total_logical_reads AS FLOAT)/NULLIF(qs.execution_count,0) AS AvgLogicalReads,
                DB_NAME(st.dbid)                  AS DatabaseName,
                OBJECT_NAME(st.objectid, st.dbid) AS ObjectName,
                SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText
            FROM (
                SELECT TOP 500 plan_handle, sql_handle, execution_count, total_logical_reads
                FROM sys.dm_exec_query_stats
                ORDER BY total_logical_reads DESC
            ) qs
            CROSS APPLY sys.dm_exec_text_query_plan(qs.plan_handle, 0, -1) tqp
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE tqp.query_plan LIKE '%Key Lookup%'
               OR tqp.query_plan LIKE '%RID Lookup%'
            ORDER BY qs.total_logical_reads DESC;
            """;

        // ── 4. Cardinality misestimates — live active queries ─────────────────
        // Uses dm_exec_query_profiles (SQL 2016+ with lightweight profiling).
        // Columns estimated_row_count / actual_row_count only exist on SQL 2016+
        // with a compatible build. Error handling is done in C# (see below).
        const string cardinalitySql = """
            SELECT TOP 20
                qp.session_id                          AS SessionId,
                qp.node_id                             AS NodeId,
                CAST(qp.estimated_row_count AS BIGINT) AS EstimatedRows,
                CAST(qp.actual_row_count    AS BIGINT) AS ActualRows,
                CAST(
                    CASE
                        WHEN qp.estimated_row_count = 0 THEN qp.actual_row_count
                        WHEN qp.actual_row_count    = 0 THEN qp.estimated_row_count
                        WHEN qp.actual_row_count > qp.estimated_row_count
                             THEN qp.actual_row_count * 1.0 / qp.estimated_row_count
                        ELSE qp.estimated_row_count * 1.0 / qp.actual_row_count
                    END AS DECIMAL(18,1)) AS MisestimateRatio,
                DB_NAME(r.database_id) AS DatabaseName,
                SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText
            FROM sys.dm_exec_query_profiles qp
            JOIN sys.dm_exec_requests r ON r.session_id = qp.session_id
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
            WHERE qp.session_id <> @@SPID
              AND qp.estimated_row_count > 0
              AND qp.actual_row_count > 0
              AND (   qp.actual_row_count * 1.0 / qp.estimated_row_count >= 1000
                   OR qp.estimated_row_count * 1.0 / qp.actual_row_count >= 1000)
            ORDER BY
                CASE
                    WHEN qp.actual_row_count > qp.estimated_row_count
                         THEN qp.actual_row_count * 1.0 / qp.estimated_row_count
                    ELSE qp.estimated_row_count * 1.0 / qp.actual_row_count
                END DESC;
            """;

        // ── 5. Skewed parallelism — live active queries ───────────────────────
        // Groups by session + node, finds max/min actual_row_count across threads.
        // Error handling in C# (see below).
        const string skewSql = """
            SELECT TOP 20
                qp.session_id                             AS SessionId,
                qp.node_id                                AS NodeId,
                MAX(qp.actual_row_count)                  AS MaxActualRows,
                MIN(qp.actual_row_count)                  AS MinActualRows,
                CAST(MAX(qp.actual_row_count) AS FLOAT)
                    / NULLIF(MIN(qp.actual_row_count), 0) AS SkewRatio,
                DB_NAME(r.database_id)                    AS DatabaseName,
                SUBSTRING(REPLACE(REPLACE(ISNULL(st.text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText
            FROM sys.dm_exec_query_profiles qp
            JOIN sys.dm_exec_requests r ON r.session_id = qp.session_id
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
            WHERE qp.session_id <> @@SPID
              AND qp.thread_id > 0
            GROUP BY qp.session_id, qp.node_id, r.database_id, st.text
            HAVING COUNT(*) > 1
               AND MAX(qp.actual_row_count) > 0
               AND CAST(MAX(qp.actual_row_count) AS FLOAT)
                   / NULLIF(MIN(qp.actual_row_count), 0) >= 5
            ORDER BY SkewRatio DESC;
            """;

        // ── 6. Plan guides ────────────────────────────────────────────────────
        const string planGuidesSql = """
            SELECT
                pg.name                   AS GuideName,
                pg.scope_type_desc        AS GuideType,
                DB_NAME()                 AS DatabaseName,
                CAST(NULL AS nvarchar(256))            AS ObjectName,
                SUBSTRING(REPLACE(REPLACE(ISNULL(pg.query_text,''),CHAR(13),' '),CHAR(10),' '),1,400) AS QueryText,
                pg.is_disabled            AS IsDisabled
            FROM sys.plan_guides pg
            ORDER BY pg.is_disabled, pg.name;
            """;

        // ── Execute all queries ───────────────────────────────────────────────
        // Cardinality and skew queries use columns that only exist on SQL Server 2016+
        // with specific build levels. Catch any SqlException at the C# level so a
        // version-incompatible server still loads the rest of the page cleanly.
        var summaryRows   = await Q(conn, summarySql,   commandTimeout: 60);
        var multiPlanRows = await Q(conn, multiPlanSql, commandTimeout: 120);

        // Key lookup scan reads plan XML (dm_exec_text_query_plan) — can fail on
        // permission-restricted accounts or very large plan caches. Isolate the failure.
        IEnumerable<dynamic> keyLookupRows;
        try   { keyLookupRows = await Q(conn, keyLookupSql, commandTimeout: 120); }
        catch (SqlException ex) when (ex.Number is 207 or 208) { keyLookupRows = Enumerable.Empty<dynamic>(); }

        IEnumerable<dynamic> cardinalityRows;
        try   { cardinalityRows = await Q(conn, cardinalitySql, commandTimeout: 30); }
        catch (SqlException ex) when (ex.Number is 207 or 208) { cardinalityRows = Enumerable.Empty<dynamic>(); }

        IEnumerable<dynamic> skewRows;
        try   { skewRows = await Q(conn, skewSql, commandTimeout: 30); }
        catch (SqlException ex) when (ex.Number is 207 or 208) { skewRows = Enumerable.Empty<dynamic>(); }

        IEnumerable<dynamic> planGuideRows;
        try   { planGuideRows = await Q(conn, planGuidesSql, commandTimeout: 15); }
        catch (SqlException ex) when (ex.Number is 207 or 208) { planGuideRows = Enumerable.Empty<dynamic>(); }

        // ── Map summary ───────────────────────────────────────────────────────
        var sr = summaryRows.FirstOrDefault();
        int multiCount   = sr is null ? 0 : (int)sr.MultiPlanQueryCount;
        int totalPlans   = sr is null ? 0 : (int)sr.TotalCachedPlans;
        int guides       = sr is null ? 0 : (int)sr.PlanGuidesEnabled;
        int joinHints    = sr is null ? 0 : (int)sr.JoinHintCount;
        bool cacheErased = sr is not null && (bool)sr.PlanCacheErasedRecently;

        string sev = cacheErased || multiCount > 10 ? "CRITICAL"
                   : multiCount > 3 || guides > 0 || joinHints > 0 ? "WARNING"
                   : "OK";

        var summary = new PlanCacheHealthSummary(
            cacheErased, multiCount, totalPlans, guides, joinHints, sev);

        // ── Map multi-plan queries ─────────────────────────────────────────────
        var multiPlan = multiPlanRows.Select(r => new MultiPlanQuery(
            (int)r.PlanCount,
            (long)r.TotalExecutions,
            (string?)r.QueryHash,
            (string?)r.QueryText,
            (string?)r.DatabaseName,
            (int)r.PlanCount > 20 ? "WARNING" : "INFO"));

        // ── Map key lookups ────────────────────────────────────────────────────
        var keyLookups = keyLookupRows.Select(r => new KeyLookupQuery(
            (string)r.LookupType,
            (long)r.ExecutionCount,
            (long)r.TotalLogicalReads,
            (double)(r.AvgLogicalReads ?? 0.0),
            (string?)r.DatabaseName,
            (string?)r.ObjectName,
            (string?)r.QueryText));

        // ── Map cardinality misestimates ──────────────────────────────────────
        var cardinality = cardinalityRows.Select(r =>
        {
            double ratio = (double)(r.MisestimateRatio ?? 0);
            return new CardinalityMisestimate(
                (int)r.SessionId,
                (int)r.NodeId,
                (long)r.EstimatedRows,
                (long)r.ActualRows,
                ratio,
                (string?)r.DatabaseName,
                (string?)r.QueryText,
                ratio >= 10_000 ? "CRITICAL" : "WARNING");
        });

        // ── Map skewed parallelism ─────────────────────────────────────────────
        var skewed = skewRows.Select(r =>
        {
            double skewRatio = (double)(r.SkewRatio ?? 0);
            return new SkewedParallelQuery(
                (int)r.SessionId,
                (int)r.NodeId,
                (long)r.MaxActualRows,
                (long)r.MinActualRows,
                skewRatio,
                (string?)r.DatabaseName,
                (string?)r.QueryText,
                skewRatio >= 10 ? "CRITICAL" : "WARNING");
        });

        // ── Map plan guides ────────────────────────────────────────────────────
        var guides2 = planGuideRows.Select(r => new PlanGuide(
            (string)r.GuideName,
            (string)r.GuideType,
            (string?)r.DatabaseName,
            (string?)r.ObjectName,
            (string?)r.QueryText,
            (bool)r.IsDisabled));

        return (summary, multiPlan, keyLookups, cardinality, skewed, guides2);
    }
}
