using System.Data;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public class PlanCacheHealthRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null)
    : BaseRepository(target, capabilities), IPlanCacheHealthRepository
{
    public async Task<(
        PlanCacheHealthSummary Summary,
        IEnumerable<MultiPlanQuery> MultiPlanQueries,
        IEnumerable<KeyLookupQuery> KeyLookupQueries,
        IEnumerable<CardinalityMisestimate> CardinalityMisestimates,
        IEnumerable<SkewedParallelQuery> SkewedParallelQueries,
        IEnumerable<PlanGuide> PlanGuides
    )> GetPlanCacheHealthAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();

        // ── 1. Summary KPIs ──────────────────────────────────────────────────
        // Single SELECT with subqueries — avoids multi-statement DECLARE issues with Dapper.
        // Join hints detected via SQL text (OPTION + LOOP/HASH/MERGE JOIN keywords) rather
        // than scanning full plan XML, which would scan thousands of large XML blobs and time out.
        var summarySql = SqlScripts.Get("PlanCacheHealth/Summary");

        // ── 2. Multi-plan queries (parameterisation failure) ─────────────────
        // Same query hash with > 5 distinct plans. Top 30 by plan count.
        var multiPlanSql = SqlScripts.Get("PlanCacheHealth/MultiPlan");

        // ── 3. RID / Key Lookups (plan cache search) ─────────────────────────
        // Pre-filter to TOP 500 queries by logical reads before scanning plan XML.
        // This limits the expensive XML scan to the most I/O-intensive plans only.
        // Uses dm_exec_text_query_plan (already NVARCHAR(MAX)) to avoid double CAST.
        var keyLookupSql = SqlScripts.Get("PlanCacheHealth/KeyLookup");

        // ── 4. Cardinality misestimates: CardinalitySql (below) ───────────────
        // ── 5. Skewed parallelism: SkewSql (below) ────────────────────────────

        // ── 6. Plan guides ────────────────────────────────────────────────────
        var planGuidesSql = SqlScripts.Get("PlanCacheHealth/PlanGuides");

        // ── Execute all queries ───────────────────────────────────────────────
        // The summary and multi-plan queries are the page: if they fail, it fails.
        // The rest are optional sections (see Optional).
        var summaryRows   = await Q(conn, summarySql,   cancellationToken, commandTimeout: 60);
        var multiPlanRows = await Q(conn, multiPlanSql, cancellationToken, commandTimeout: SqlTarget.ExtendedCommandTimeoutSeconds);

        var keyLookupRows   = await Optional(Q(conn, keyLookupSql,   cancellationToken, commandTimeout: SqlTarget.ExtendedCommandTimeoutSeconds));
        var cardinalityRows = await Optional(Q(conn, CardinalitySql, cancellationToken, commandTimeout: 30));
        var skewRows        = await Optional(Q(conn, SkewSql,        cancellationToken, commandTimeout: 30));
        var planGuideRows   = await Optional(Q(conn, planGuidesSql,  cancellationToken, commandTimeout: 15));

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

    // sys.dm_exec_query_profiles has one row per node per thread, for queries running with
    // lightweight profiling (on by default from SQL Server 2019) or a statistics-profile trace.
    // Its row columns are estimate_row_count and row_count on every version.

    /// <summary>
    /// Cardinality misestimates in running queries: nodes whose actual and estimated rows are
    /// 1000× apart. A parallel node's threads are summed first, as the DMV's documented
    /// example does, so each node is one row.
    /// </summary>
    internal static readonly string CardinalitySql = SqlScripts.Get("PlanCacheHealth/Cardinality");

    /// <summary>
    /// Skewed parallelism in running queries: parallel nodes where one worker thread has
    /// produced 5× the rows of another. Thread 0 is the coordinator, so it's left out.
    /// </summary>
    internal static readonly string SkewSql = SqlScripts.Get("PlanCacheHealth/Skew");

    /// <summary>
    /// Runs an optional section's query: a missing column or object (207, 208) leaves just
    /// that section empty instead of failing the page. <see cref="BaseRepository.Q"/> wraps
    /// every error in a <see cref="WaitStatsException"/>, so that's what's caught here.
    /// </summary>
    internal static async Task<IEnumerable<dynamic>> Optional(Task<IEnumerable<dynamic>> query)
    {
        try { return await query; }
        catch (WaitStatsException ex) when (ex.SqlErrorNumber is 207 or 208) { return []; }
    }
}
