using System.Data;
using System.Globalization;
using Dapper;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads the connection's database's Query Store for the Query Store page (#111): its settings, top
/// queries by any metric, totals over time, variation, waits, forced plans and one query's history.
/// Only sys.query_store_* and sys.database_query_store_options are read. Every query groups and
/// ranks on the server, so only the top N rows come back, never the raw runtime stats.
/// </summary>
public class QueryStoreRepository(IConfiguration configuration) : BaseRepository(configuration), IQueryStoreRepository
{
    // Up to 30 days of a large Query Store can take a while, as on Query Regressions.
    private const int ExtendedTimeoutSec = 120;

    // Invalid column name (207) or object name (208): something this version lacks.
    private static readonly int[] MissingObjectErrors = [207, 208];

    // Asked once per connection; the repository is rebuilt when the connection changes.
    private QueryStoreFeatures? _features;

    // ── Versions ──────────────────────────────────────────────────────────
    // A view or column's presence is asked of sys.all_columns, as for plan_type_desc (#109): naming
    // a missing one fails the whole query. A missing view has no OBJECT_ID, so it has no columns.

    private static string Has(string view, string column) =>
        $"CASE WHEN EXISTS (SELECT 1 FROM sys.all_columns WHERE object_id = OBJECT_ID(N'{view}') AND name = N'{column}') THEN 1 ELSE 0 END";

    /// <summary>Every probe in one round trip. The names are constants, never user input.</summary>
    internal static readonly string FeaturesSql = $"""
        SELECT {Has("sys.query_store_wait_stats", "wait_category")}                          AS WaitStats,
               {Has("sys.database_query_store_options", "wait_stats_capture_mode_desc")}     AS WaitStatsCaptureMode,
               {Has("sys.query_store_runtime_stats", "avg_log_bytes_used")}                  AS LogBytes,
               {Has("sys.query_store_runtime_stats", "avg_tempdb_space_used")}               AS TempDb,
               {Has("sys.query_store_plan", "plan_forcing_type_desc")}                       AS ForcingType,
               {Has("sys.query_store_plan", "plan_type_desc")}                               AS PlanType,
               {Has("sys.query_store_query_hints", "query_hint_text")}                       AS Hints
        """;

    private async Task<QueryStoreFeatures> FeaturesAsync(IDbConnection conn)
    {
        if (_features is { } known)
            return known;

        var r = Row(await QFirst(conn, FeaturesSql));
        return _features = r is null
            ? QueryStoreFeatures.None
            : new QueryStoreFeatures(Bool(r, "WaitStats"), Bool(r, "WaitStatsCaptureMode"), Bool(r, "LogBytes"),
                                     Bool(r, "TempDb"), Bool(r, "ForcingType"), Bool(r, "PlanType"), Bool(r, "Hints"));
    }

    // ── Settings ──────────────────────────────────────────────────────────

    /// <summary>wait_stats_capture_mode_desc is SQL Server 2017+; before that it reads as null.</summary>
    internal static string HealthSql(bool hasWaitStatsCaptureMode) =>
        HealthSqlTemplate.Replace("/*WAIT_STATS_CAPTURE*/", hasWaitStatsCaptureMode ? "wait_stats_capture_mode_desc" : "CAST(NULL AS nvarchar(60))");

    private const string HealthSqlTemplate = """
        SELECT DB_NAME()                    AS DatabaseName,
               actual_state_desc            AS ActualState,
               desired_state_desc           AS DesiredState,
               readonly_reason              AS ReadOnlyReason,
               current_storage_size_mb      AS CurrentStorageSizeMB,
               max_storage_size_mb          AS MaxStorageSizeMB,
               CAST(current_storage_size_mb * 100.0 / NULLIF(max_storage_size_mb, 0) AS DECIMAL(10,1)) AS StorageUsedPct,
               flush_interval_seconds       AS FlushIntervalSec,
               interval_length_minutes      AS IntervalLengthMin,
               stale_query_threshold_days   AS StaleQueryThresholdDays,
               size_based_cleanup_mode_desc AS SizeCleanupMode,
               query_capture_mode_desc      AS CaptureMode,
               /*WAIT_STATS_CAPTURE*/ AS WaitStatsCaptureMode,
               max_plans_per_query          AS MaxPlansPerQuery,
               (SELECT COUNT(*) FROM sys.query_store_query)                         AS TotalQueries,
               (SELECT COUNT(*) FROM sys.query_store_plan)                          AS TotalPlans,
               (SELECT COUNT(*) FROM sys.query_store_plan WHERE is_forced_plan = 1) AS ForcedPlans,
               (SELECT MIN(start_time) FROM sys.query_store_runtime_stats_interval) AS DataFrom,
               GETDATE()                    AS CaptureTime
        FROM sys.database_query_store_options
        """;

    public async Task<QueryStoreStatus> GetQueryStoreStatusAsync()
    {
        using var conn = CreateConnection();
        var features = await FeaturesAsync(conn);

        try
        {
            if (Row(await QFirst(conn, HealthSql(features.WaitStatsCaptureMode))) is { } r)
                return new QueryStoreStatus(Str(r, "DatabaseName"), ReadHealth(r), features);
        }
        catch (WaitStatsException ex) when (ex.SqlErrorNumber == 208)
        {
            // SQL Server 2014 and earlier: no Query Store catalog views at all.
        }

        var db = Row(await QFirst(conn, "SELECT DB_NAME() AS DatabaseName"));
        return new QueryStoreStatus(db is null ? string.Empty : Str(db, "DatabaseName"), null, features);
    }

    private static QueryStoreHealth ReadHealth(IDictionary<string, object> r) => new(
        Str(r, "DatabaseName"),
        Str(r, "ActualState"),
        Str(r, "DesiredState"),
        (int)Long(r, "ReadOnlyReason"),
        Long(r, "CurrentStorageSizeMB"),
        Long(r, "MaxStorageSizeMB"),
        Dbl(r, "StorageUsedPct"),
        (int)Long(r, "FlushIntervalSec"),
        (int)Long(r, "IntervalLengthMin"),
        (int)Long(r, "StaleQueryThresholdDays"),
        Str(r, "SizeCleanupMode"),
        Str(r, "CaptureMode"),
        StrOrNull(r, "WaitStatsCaptureMode"),
        (int)Long(r, "MaxPlansPerQuery"),
        (int)Long(r, "TotalQueries"),
        (int)Long(r, "TotalPlans"),
        (int)Long(r, "ForcedPlans"),
        Utc(r, "DataFrom"),
        r["CaptureTime"] is DateTime t ? t : DateTime.Now);

    // ── Export for AI: the top 20 by CPU in the last 24 hours, as before #111 ──

    // plan_type_desc is SQL Server 2022+. Before that every plan is a compiled plan: the
    // dispatcher and query variant plans of parameter-sensitive plan optimization came with it.
    internal static string QueryStoreTopQueriesSql(bool hasPlanType) =>
        QueryStoreTopQueriesSqlTemplate.Replace("/*PLAN_TYPE*/", hasPlanType ? "p.plan_type_desc" : "N'Compiled Plan'");

    public async Task<(QueryStoreHealth?, IEnumerable<QueryStoreTopQuery>)> GetQueryStoreAsync()
    {
        var status = await GetQueryStoreStatusAsync();
        if (!status.IsSupported)
            return (null, []);

        using var conn = CreateConnection();
        IEnumerable<QueryStoreTopQuery> queries = [];
        try
        {
            var rows = await Q(conn, QueryStoreTopQueriesSql(status.Features.PlanType));
            queries = rows.Select(r =>
            {
                double cpu = (double)r.TotalCpuMs;
                string sev = cpu > 60_000 ? "Critical" : cpu > 10_000 ? "Warning" : "Info";
                return new QueryStoreTopQuery(
                    (long)r.QueryId, (long)r.PlanId,
                    (string)r.QueryText, (long)r.TotalExecutions,
                    cpu, (double)r.AvgCpuMs,
                    (double)r.TotalDurationMs, (double)r.AvgDurationMs,
                    (long)r.TotalLogicalReads, (double)r.AvgLogicalReads,
                    (long)r.TotalLogicalWrites, (long)r.TotalPhysicalReads,
                    (double)r.TotalMemoryMB, (double)r.AvgMemoryKB,
                    (bool)r.IsForcedPlan, (string)r.PlanType,
                    UtcOrMin((object)r.LastExecutionTimeUtc), (DateTime)r.CaptureTime, sev);
            }).ToList();
        }
        catch (WaitStatsException ex) when (ex.SqlErrorNumber == 208) { /* Query Store catalog views don't exist on this SQL version */ }

        return (status.Health, queries);
    }

    private const string QueryStoreTopQueriesSqlTemplate = """
            ;WITH PlanAgg AS (
                SELECT rs.plan_id,
                    SUM(rs.avg_cpu_time*rs.count_executions) AS TotalCpuUs,
                    SUM(rs.avg_duration*rs.count_executions) AS TotalDurationUs,
                    SUM(rs.avg_logical_io_reads*rs.count_executions) AS TotalLogicalReads,
                    SUM(rs.avg_logical_io_writes*rs.count_executions) AS TotalLogicalWrites,
                    SUM(rs.avg_physical_io_reads*rs.count_executions) AS TotalPhysicalReads,
                    SUM(rs.avg_query_max_used_memory*rs.count_executions) AS TotalMemoryPages,
                    SUM(rs.count_executions) AS TotalExecutions,
                    MAX(rs.last_execution_time) AS LastExecutionTime
                FROM sys.query_store_runtime_stats rs
                WHERE rs.last_execution_time >= DATEADD(HOUR,-24,GETUTCDATE())
                GROUP BY rs.plan_id
            ),
            Ranked AS (
                SELECT p.query_id, pa.plan_id, pa.TotalCpuUs, pa.TotalDurationUs,
                    pa.TotalLogicalReads, pa.TotalLogicalWrites, pa.TotalPhysicalReads,
                    pa.TotalMemoryPages, pa.TotalExecutions, pa.LastExecutionTime,
                    p.is_forced_plan, /*PLAN_TYPE*/ AS plan_type_desc,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalCpuUs/pa.TotalExecutions ELSE 0 END AS AvgCpuUs,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalDurationUs/pa.TotalExecutions ELSE 0 END AS AvgDurationUs,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalLogicalReads/pa.TotalExecutions ELSE 0 END AS AvgLogicalReads,
                    CASE WHEN pa.TotalExecutions>0 THEN pa.TotalMemoryPages*8/pa.TotalExecutions ELSE 0 END AS AvgMemoryKB,
                    ROW_NUMBER() OVER (PARTITION BY p.query_id ORDER BY pa.TotalCpuUs DESC) AS rn
                FROM PlanAgg pa JOIN sys.query_store_plan p ON pa.plan_id=p.plan_id
                WHERE p.query_plan IS NOT NULL
            )
            SELECT TOP 20 r.query_id AS QueryId, r.plan_id AS PlanId,
                qt.query_sql_text AS QueryText,
                r.TotalExecutions,
                r.TotalCpuUs/1000.0 AS TotalCpuMs, r.AvgCpuUs/1000.0 AS AvgCpuMs,
                r.TotalDurationUs/1000.0 AS TotalDurationMs, r.AvgDurationUs/1000.0 AS AvgDurationMs,
                r.TotalLogicalReads, r.AvgLogicalReads,
                r.TotalLogicalWrites, r.TotalPhysicalReads,
                r.TotalMemoryPages*8/1024.0 AS TotalMemoryMB, r.AvgMemoryKB*1.0 AS AvgMemoryKB,
                r.is_forced_plan AS IsForcedPlan, r.plan_type_desc AS PlanType,
                r.LastExecutionTime AS LastExecutionTimeUtc, GETDATE() AS CaptureTime
            FROM Ranked r
            JOIN sys.query_store_query q ON r.query_id=q.query_id
            JOIN sys.query_store_query_text qt ON q.query_text_id=qt.query_text_id
            WHERE r.rn=1 ORDER BY r.TotalCpuUs DESC OPTION (LOOP JOIN)
            """;

    // ── The period ────────────────────────────────────────────────────────
    // A preset ends at the server's now (@minutes back); a custom period is fixed (@fromUtc, @toUtc).
    // Each interval counts in the period its start falls in, as on Query Regressions, so a period
    // counts whole intervals.

    internal const string PeriodSql = """
        DECLARE @periodTo   datetimeoffset = ISNULL(@toUtc,   SYSDATETIMEOFFSET());
        DECLARE @periodFrom datetimeoffset = ISNULL(@fromUtc, DATEADD(MINUTE, -@minutes, @periodTo));
        """;

    private const string RuntimeInPeriod = """
        FROM sys.query_store_runtime_stats_interval i
        JOIN sys.query_store_runtime_stats rs ON rs.runtime_stats_interval_id = i.runtime_stats_interval_id
        JOIN sys.query_store_plan p           ON p.plan_id = rs.plan_id
        WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
        """;

    // The procedure, function or trigger a statement is in; NULL for ad hoc SQL (object_id 0).
    private const string ObjectNameSql =
        "CASE WHEN q.object_id <> 0 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) END";

    // Picks each query's plan with the most executions in the period, for View Plan.
    private const string RankedPlans =
        "(SELECT *, ROW_NUMBER() OVER (PARTITION BY query_id ORDER BY n DESC, plan_id DESC) AS plan_rank FROM per_plan) ranked_plans";

    /// <summary>σ from N, Σnμ and Σn(σ² + μ²), clamped at 0 (see <see cref="CombinedStdDev"/>).</summary>
    internal static string StdDevSql(string sum, string sumOfSquares, string count) =>
        $"SQRT(CASE WHEN {sumOfSquares} / NULLIF({count}, 0) - SQUARE({sum} / NULLIF({count}, 0)) > 0 " +
        $"THEN {sumOfSquares} / NULLIF({count}, 0) - SQUARE({sum} / NULLIF({count}, 0)) ELSE 0 END)";

    private static DynamicParameters Parameters(QueryStorePeriod period, object? more = null)
    {
        var p = new DynamicParameters(period.SqlParameters);
        if (more is not null)
            p.AddDynamicParams(more);
        return p;
    }

    // ── Top Queries ───────────────────────────────────────────────────────

    /// <summary>
    /// Ranks queries by a metric and statistic. Only the chosen metric's columns are named, so a
    /// SQL Server 2017+ metric's columns appear only when it's asked for.
    /// </summary>
    internal static string TopQueriesSql(QueryStoreMetric metric, QueryStoreStatistic statistic)
    {
        var column = QueryStoreMetrics.Info(metric).Column;
        var sums = column is null
            ? "CAST(0 AS float) AS m_sum, CAST(0 AS float) AS m_sum_sq, CAST(0 AS float) AS m_max"
            : $"SUM(rs.avg_{column} * rs.count_executions) AS m_sum,\n" +
              $"               SUM(rs.count_executions * (SQUARE(rs.stdev_{column}) + SQUARE(rs.avg_{column}))) AS m_sum_sq,\n" +
              $"               MAX(CAST(rs.max_{column} AS float)) AS m_max";
        var rank = column is null ? "CAST(n AS float)" : statistic switch
        {
            QueryStoreStatistic.Average => "m_sum / NULLIF(n, 0)",
            QueryStoreStatistic.Max     => "m_max",
            QueryStoreStatistic.StdDev  => StdDevSql("m_sum", "m_sum_sq", "n"),
            _                           => "m_sum",
        };
        return TopQueriesSqlTemplate
            .Replace("/*PERIOD*/", PeriodSql)
            .Replace("/*RUNTIME_IN_PERIOD*/", RuntimeInPeriod)
            .Replace("/*METRIC_SUMS*/", sums)
            .Replace("/*RANKED_PLANS*/", RankedPlans)
            .Replace("/*RANK*/", rank)
            .Replace("/*OBJECT_NAME*/", ObjectNameSql);
    }

    private const string TopQueriesSqlTemplate = """
        /*PERIOD*/
        ;WITH per_plan AS (
            SELECT p.query_id, rs.plan_id,
                   SUM(rs.count_executions)                                                   AS n,
                   SUM(CASE WHEN rs.execution_type <> 0 THEN rs.count_executions ELSE 0 END) AS failed,
                   SUM(rs.avg_cpu_time         * rs.count_executions)                        AS cpu_us,
                   SUM(rs.avg_duration         * rs.count_executions)                        AS duration_us,
                   SUM(rs.avg_logical_io_reads * rs.count_executions)                        AS logical_reads,
                   /*METRIC_SUMS*/,
                   MAX(rs.last_execution_time)                                               AS last_execution
            /*RUNTIME_IN_PERIOD*/
            GROUP BY p.query_id, rs.plan_id
        ),
        per_query AS (
            SELECT query_id,
                   COUNT(*)            AS plans,
                   SUM(n)              AS n,
                   SUM(failed)         AS failed,
                   SUM(cpu_us)         AS cpu_us,
                   SUM(duration_us)    AS duration_us,
                   SUM(logical_reads)  AS logical_reads,
                   SUM(m_sum)          AS m_sum,
                   SUM(m_sum_sq)       AS m_sum_sq,
                   MAX(m_max)          AS m_max,
                   MAX(last_execution) AS last_execution,
                   MAX(CASE WHEN plan_rank = 1 THEN plan_id END) AS top_plan_id
            FROM /*RANKED_PLANS*/
            GROUP BY query_id
        ),
        top_queries AS (
            SELECT TOP (@top) *, /*RANK*/ AS metric_value
            FROM per_query
            ORDER BY metric_value DESC
        )
        SELECT t.query_id          AS QueryId,
               q.object_id         AS ObjectId,
               /*OBJECT_NAME*/     AS ObjectName,
               qt.query_sql_text   AS QueryText,
               t.plans             AS Plans,
               t.n                 AS Executions,
               t.failed            AS Failed,
               t.metric_value      AS MetricValue,
               t.cpu_us            AS CpuUs,
               t.duration_us       AS DurationUs,
               t.logical_reads     AS LogicalReads,
               (SELECT MAX(fp.plan_id) FROM sys.query_store_plan fp
                WHERE fp.query_id = t.query_id AND fp.is_forced_plan = 1) AS ForcedPlanId,
               t.top_plan_id       AS TopPlanId,
               t.last_execution    AS LastExecution
        FROM top_queries t
        JOIN sys.query_store_query q       ON q.query_id = t.query_id
        JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
        ORDER BY t.metric_value DESC
        """;

    public Task<QueryStoreRows<QueryStoreQueryRow>> GetQueryStoreTopQueriesAsync(
        QueryStorePeriod period, QueryStoreMetric metric, QueryStoreStatistic statistic, int top) =>
        ReadAsync(async conn =>
        {
            var scale = metric == QueryStoreMetric.Executions ? 1 : QueryStoreMetrics.Info(metric).Scale;
            var rows = await Q(conn, TopQueriesSql(metric, statistic), Parameters(period, new { top }), ExtendedTimeoutSec);
            return Rows(rows).Select(r =>
            {
                var executions = Long(r, "Executions");
                return new QueryStoreQueryRow(
                    Long(r, "QueryId"),
                    Long(r, "ObjectId"),
                    StrOrNull(r, "ObjectName"),
                    Str(r, "QueryText"),
                    (int)Long(r, "Plans"),
                    executions,
                    Long(r, "Failed"),
                    Dbl(r, "MetricValue") * scale,
                    Dbl(r, "CpuUs") / 1000,
                    executions > 0 ? Dbl(r, "DurationUs") / 1000 / executions : 0,
                    Dbl(r, "LogicalReads"),
                    LongOrNull(r, "ForcedPlanId"),
                    LongOrNull(r, "TopPlanId"),
                    Utc(r, "LastExecution"));
            }).ToList();
        });

    // ── Over Time and Query details: every metric's total ─────────────────

    /// <summary>Σ(avg × count) of every metric; the SQL Server 2017+ ones only where they exist.</summary>
    internal static string TotalsSql(bool hasLogBytes, bool hasTempDb) => $"""
        SUM(rs.count_executions)                                                   AS Executions,
               SUM(CASE WHEN rs.execution_type <> 0 THEN rs.count_executions ELSE 0 END) AS Failed,
               SUM(rs.avg_cpu_time              * rs.count_executions)             AS CpuUs,
               SUM(rs.avg_duration              * rs.count_executions)             AS DurationUs,
               SUM(rs.avg_logical_io_reads      * rs.count_executions)             AS LogicalReads,
               SUM(rs.avg_logical_io_writes     * rs.count_executions)             AS LogicalWrites,
               SUM(rs.avg_physical_io_reads     * rs.count_executions)             AS PhysicalReads,
               SUM(rs.avg_query_max_used_memory * rs.count_executions)             AS MemoryPages,
               SUM(rs.avg_rowcount              * rs.count_executions)             AS ReturnedRows,
               {(hasLogBytes ? "SUM(rs.avg_log_bytes_used * rs.count_executions)" : "CAST(0 AS float)")} AS LogBytes,
               {(hasTempDb ? "SUM(rs.avg_tempdb_space_used * rs.count_executions)" : "CAST(0 AS float)")} AS TempDbPages
        """;

    private static QueryStoreTotals ReadTotals(IDictionary<string, object> r) => new(
        Long(r, "Executions"), Dbl(r, "CpuUs"), Dbl(r, "DurationUs"), Dbl(r, "LogicalReads"), Dbl(r, "LogicalWrites"),
        Dbl(r, "PhysicalReads"), Dbl(r, "MemoryPages"), Dbl(r, "ReturnedRows"), Dbl(r, "LogBytes"), Dbl(r, "TempDbPages"));

    /// <summary>
    /// The database's totals per slot: slot n starts at @origin + n × @slotMinutes. Integer division
    /// of the minutes since @origin puts each interval in exactly one slot.
    /// </summary>
    internal static string OverTimeSql(bool hasLogBytes, bool hasTempDb) => $"""
        {PeriodSql}
        SELECT rs.slot AS Slot,
               {TotalsSql(hasLogBytes, hasTempDb)}
        FROM (
            SELECT DATEDIFF(MINUTE, @origin, i.start_time) / @slotMinutes AS slot, r.*
            FROM sys.query_store_runtime_stats_interval i
            JOIN sys.query_store_runtime_stats r ON r.runtime_stats_interval_id = i.runtime_stats_interval_id
            WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
        ) rs
        GROUP BY rs.slot
        ORDER BY rs.slot
        """;

    public Task<QueryStoreRows<QueryStoreSlot>> GetQueryStoreOverTimeAsync(QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin) =>
        ReadAsync(async conn =>
        {
            var features = await FeaturesAsync(conn);
            var rows = await Q(conn, OverTimeSql(features.LogBytes, features.TempDb),
                Parameters(period, new { origin, slotMinutes = (int)slotLength.TotalMinutes }), ExtendedTimeoutSec);
            return Rows(rows)
                .Select(r => new QueryStoreSlot(QueryStoreTimeSlots.SlotStartUtc(origin, slotLength, Long(r, "Slot")), ReadTotals(r)))
                .ToList();
        });

    // ── High Variation ────────────────────────────────────────────────────

    /// <summary>Queries ranked by the combined std dev of a metric over every interval and plan in the period.</summary>
    internal static string HighVariationSql(QueryStoreMetric metric)
    {
        var column = QueryStoreMetrics.Info(metric).Column
                     ?? throw new ArgumentException("Executions don't vary per execution.", nameof(metric));
        return HighVariationSqlTemplate
            .Replace("/*PERIOD*/", PeriodSql)
            .Replace("/*RUNTIME_IN_PERIOD*/", RuntimeInPeriod)
            .Replace("/*RANKED_PLANS*/", RankedPlans)
            .Replace("/*STDEV*/", StdDevSql("m_sum", "m_sum_sq", "n"))
            .Replace("/*OBJECT_NAME*/", ObjectNameSql)
            .Replace("/*COLUMN*/", column);
    }

    private const string HighVariationSqlTemplate = """
        /*PERIOD*/
        ;WITH per_plan AS (
            SELECT p.query_id, rs.plan_id,
                   SUM(rs.count_executions)                                                                  AS n,
                   SUM(rs.avg_/*COLUMN*/ * rs.count_executions)                                             AS m_sum,
                   SUM(rs.count_executions * (SQUARE(rs.stdev_/*COLUMN*/) + SQUARE(rs.avg_/*COLUMN*/)))    AS m_sum_sq,
                   MIN(CAST(rs.min_/*COLUMN*/ AS float))                                                    AS m_min,
                   MAX(CAST(rs.max_/*COLUMN*/ AS float))                                                    AS m_max
            /*RUNTIME_IN_PERIOD*/
            GROUP BY p.query_id, rs.plan_id
        ),
        per_query AS (
            SELECT query_id,
                   COUNT(*)      AS plans,
                   SUM(n)        AS n,
                   SUM(m_sum)    AS m_sum,
                   SUM(m_sum_sq) AS m_sum_sq,
                   MIN(m_min)    AS m_min,
                   MAX(m_max)    AS m_max,
                   MAX(CASE WHEN plan_rank = 1 THEN plan_id END) AS top_plan_id
            FROM /*RANKED_PLANS*/
            GROUP BY query_id
            HAVING SUM(n) >= @minExecutions
        ),
        top_queries AS (
            SELECT TOP (@top) *,
                   m_sum / NULLIF(n, 0) AS m_avg,
                   /*STDEV*/            AS m_stdev
            FROM per_query
            ORDER BY m_stdev DESC
        )
        SELECT t.query_id        AS QueryId,
               q.object_id       AS ObjectId,
               /*OBJECT_NAME*/   AS ObjectName,
               qt.query_sql_text AS QueryText,
               t.plans           AS Plans,
               t.n               AS Executions,
               t.m_avg           AS AvgValue,
               t.m_min           AS MinValue,
               t.m_max           AS MaxValue,
               t.m_stdev         AS StdDevValue,
               t.top_plan_id     AS TopPlanId
        FROM top_queries t
        JOIN sys.query_store_query q       ON q.query_id = t.query_id
        JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
        ORDER BY t.m_stdev DESC
        """;

    public Task<QueryStoreRows<QueryStoreVariationRow>> GetQueryStoreHighVariationAsync(
        QueryStorePeriod period, QueryStoreMetric metric, int minExecutions, int top) =>
        ReadAsync(async conn =>
        {
            var scale = QueryStoreMetrics.Info(metric).Scale;
            var rows = await Q(conn, HighVariationSql(metric), Parameters(period, new { minExecutions, top }), ExtendedTimeoutSec);
            return Rows(rows).Select(r => new QueryStoreVariationRow(
                Long(r, "QueryId"),
                Long(r, "ObjectId"),
                StrOrNull(r, "ObjectName"),
                Str(r, "QueryText"),
                (int)Long(r, "Plans"),
                Long(r, "Executions"),
                Dbl(r, "AvgValue") * scale,
                Dbl(r, "MinValue") * scale,
                Dbl(r, "MaxValue") * scale,
                Dbl(r, "StdDevValue") * scale,
                LongOrNull(r, "TopPlanId"))).ToList();
        });

    // ── Waits (SQL Server 2017+ and Azure SQL) ────────────────────────────

    internal static readonly string WaitCategoriesSql = $"""
        {PeriodSql}
        SELECT ws.wait_category                 AS Category,
               MAX(ws.wait_category_desc)       AS CategoryName,
               SUM(ws.total_query_wait_time_ms) AS WaitMs
        FROM sys.query_store_runtime_stats_interval i
        JOIN sys.query_store_wait_stats ws ON ws.runtime_stats_interval_id = i.runtime_stats_interval_id
        WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
        GROUP BY ws.wait_category
        HAVING SUM(ws.total_query_wait_time_ms) > 0
        ORDER BY WaitMs DESC
        """;

    public Task<QueryStoreRows<QueryStoreWaitCategory>> GetQueryStoreWaitCategoriesAsync(QueryStorePeriod period) =>
        ReadAsync(async conn =>
        {
            var rows = Rows(await Q(conn, WaitCategoriesSql, Parameters(period), ExtendedTimeoutSec)).ToList();
            var all  = rows.Sum(r => Dbl(r, "WaitMs"));
            return rows.Select(r =>
            {
                var category = (int)Long(r, "Category");
                var waitMs   = Dbl(r, "WaitMs");
                return new QueryStoreWaitCategory(category, QueryStoreWaitCategories.Name(category, StrOrNull(r, "CategoryName")),
                                                  waitMs, all > 0 ? waitMs * 100 / all : 0);
            }).ToList();
        });

    // The category's total is summed before TOP, over every query that waited in it. Executions
    // come from the runtime stats, for the listed queries only.
    internal static readonly string WaitQueriesSql = $"""
        {PeriodSql}
        ;WITH waits AS (
            SELECT p.query_id, SUM(ws.total_query_wait_time_ms) AS wait_ms
            FROM sys.query_store_runtime_stats_interval i
            JOIN sys.query_store_wait_stats ws ON ws.runtime_stats_interval_id = i.runtime_stats_interval_id
            JOIN sys.query_store_plan p        ON p.plan_id = ws.plan_id
            WHERE i.start_time >= @periodFrom AND i.start_time < @periodTo
              AND ws.wait_category = @category
            GROUP BY p.query_id
        ),
        top_queries AS (
            SELECT TOP (@top) query_id, wait_ms, SUM(wait_ms) OVER () AS category_ms
            FROM waits
            ORDER BY wait_ms DESC
        ),
        per_plan AS (
            SELECT p.query_id, rs.plan_id, SUM(rs.count_executions) AS n
            {RuntimeInPeriod}
              AND p.query_id IN (SELECT query_id FROM top_queries)
            GROUP BY p.query_id, rs.plan_id
        ),
        executions AS (
            SELECT query_id, SUM(n) AS n, MAX(CASE WHEN plan_rank = 1 THEN plan_id END) AS top_plan_id
            FROM {RankedPlans}
            GROUP BY query_id
        )
        SELECT t.query_id        AS QueryId,
               q.object_id       AS ObjectId,
               {ObjectNameSql} AS ObjectName,
               qt.query_sql_text AS QueryText,
               t.wait_ms         AS WaitMs,
               t.category_ms     AS CategoryMs,
               ISNULL(e.n, 0)    AS Executions,
               e.top_plan_id     AS TopPlanId
        FROM top_queries t
        JOIN sys.query_store_query q       ON q.query_id = t.query_id
        JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
        LEFT JOIN executions e             ON e.query_id = t.query_id
        ORDER BY t.wait_ms DESC
        """;

    public Task<QueryStoreRows<QueryStoreWaitQuery>> GetQueryStoreWaitQueriesAsync(QueryStorePeriod period, int category, int top) =>
        ReadAsync(async conn =>
        {
            var rows = await Q(conn, WaitQueriesSql, Parameters(period, new { category, top }), ExtendedTimeoutSec);
            return Rows(rows).Select(r => new QueryStoreWaitQuery(
                Long(r, "QueryId"),
                Long(r, "ObjectId"),
                StrOrNull(r, "ObjectName"),
                Str(r, "QueryText"),
                Dbl(r, "WaitMs"),
                Dbl(r, "CategoryMs"),
                Long(r, "Executions"),
                LongOrNull(r, "TopPlanId"))).ToList();
        });

    // ── Forced Plans ──────────────────────────────────────────────────────

    /// <summary>
    /// plan_forcing_type_desc is SQL Server 2017+. SQL Server 2016 had no automatic plan correction,
    /// so every forced plan there was forced by hand.
    /// </summary>
    internal static string ForcedPlansSql(bool hasForcingType) =>
        ForcedPlansSqlTemplate
            .Replace("/*PERIOD*/", PeriodSql)
            .Replace("/*OBJECT_NAME*/", ObjectNameSql)
            .Replace("/*FORCING_TYPE*/", hasForcingType ? "p.plan_forcing_type_desc" : "N'MANUAL'");

    private const string ForcedPlansSqlTemplate = """
        /*PERIOD*/
        SELECT p.query_id                     AS QueryId,
               p.plan_id                      AS PlanId,
               q.object_id                    AS ObjectId,
               /*OBJECT_NAME*/                AS ObjectName,
               qt.query_sql_text              AS QueryText,
               /*FORCING_TYPE*/               AS ForcingType,
               p.force_failure_count          AS ForceFailures,
               p.last_force_failure_reason_desc AS LastFailureReason,
               p.last_execution_time          AS LastExecution,
               s.n                            AS Executions,
               s.duration_us                  AS DurationUs
        FROM sys.query_store_plan p
        JOIN sys.query_store_query q       ON q.query_id = p.query_id
        JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
        OUTER APPLY (
            SELECT SUM(rs.count_executions)                    AS n,
                   SUM(rs.avg_duration * rs.count_executions)  AS duration_us
            FROM sys.query_store_runtime_stats rs
            JOIN sys.query_store_runtime_stats_interval i ON i.runtime_stats_interval_id = rs.runtime_stats_interval_id
            WHERE rs.plan_id = p.plan_id
              AND i.start_time >= @periodFrom AND i.start_time < @periodTo
        ) s
        WHERE p.is_forced_plan = 1
        ORDER BY p.force_failure_count DESC, p.query_id, p.plan_id
        """;

    /// <summary>Query Store hints: SQL Server 2022+ and Azure SQL only.</summary>
    internal const string HintsSql = """
        SELECT h.query_id                            AS QueryId,
               h.query_hint_text                     AS HintText,
               h.source_desc                         AS Source,
               h.query_hint_failure_count            AS Failures,
               h.last_query_hint_failure_reason_desc AS LastFailureReason,
               qt.query_sql_text                     AS QueryText
        FROM sys.query_store_query_hints h
        LEFT JOIN sys.query_store_query q       ON q.query_id = h.query_id
        LEFT JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
        ORDER BY h.query_hint_failure_count DESC, h.query_id
        """;

    // Reading the hints needs VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on 2022+); a denied
    // SELECT on the view is 229.
    private static readonly int[] PermissionErrors = [229, 297, 300];

    public async Task<QueryStoreForcedPlans> GetQueryStoreForcedPlansAsync(QueryStorePeriod period)
    {
        QueryStoreFeatures features;
        using (var conn = CreateConnection())
            features = await FeaturesAsync(conn);

        var plans = await ReadAsync(async conn =>
        {
            var rows = await Q(conn, ForcedPlansSql(features.ForcingType), Parameters(period), ExtendedTimeoutSec);
            return Rows(rows).Select(r =>
            {
                var executions = Long(r, "Executions");
                return new QueryStoreForcedPlan(
                    Long(r, "QueryId"),
                    Long(r, "PlanId"),
                    Long(r, "ObjectId"),
                    StrOrNull(r, "ObjectName"),
                    Str(r, "QueryText"),
                    QueryStoreText.ForcedBy(StrOrNull(r, "ForcingType")),
                    Long(r, "ForceFailures"),
                    StrOrNull(r, "LastFailureReason"),
                    Utc(r, "LastExecution"),
                    executions,
                    executions > 0 ? Dbl(r, "DurationUs") / 1000 / executions : null);
            }).ToList();
        });

        if (!features.Hints)
            return new QueryStoreForcedPlans(plans, null);

        QueryStoreRows<QueryStoreHint> hints;
        try
        {
            hints = await ReadAsync(async conn => Rows(await Q(conn, HintsSql)).Select(r => new QueryStoreHint(
                Long(r, "QueryId"),
                Str(r, "HintText"),
                Str(r, "Source"),
                Long(r, "Failures"),
                StrOrNull(r, "LastFailureReason"),
                Str(r, "QueryText"))).ToList());
        }
        catch (WaitStatsException ex) when (PermissionErrors.Contains(ex.SqlErrorNumber))
        {
            hints = QueryStoreRows<QueryStoreHint>.Missing(
                "Reading Query Store hints needs VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on SQL Server 2022 and later).");
        }
        return new QueryStoreForcedPlans(plans, hints);
    }

    // ── Query details ─────────────────────────────────────────────────────

    internal static readonly string QueryInfoSql = $"""
        SELECT q.query_id AS QueryId, q.object_id AS ObjectId, {ObjectNameSql} AS ObjectName, qt.query_sql_text AS QueryText
        FROM sys.query_store_query q
        JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
        WHERE q.query_id = @queryId
        """;

    internal const string QueryPlansSql = """
        SELECT plan_id AS PlanId, is_forced_plan AS IsForced, force_failure_count AS ForceFailures,
               last_force_failure_reason_desc AS LastFailureReason
        FROM sys.query_store_plan
        WHERE query_id = @queryId
        """;

    /// <summary>One query's executions per interval and plan in the period, with every metric's total.</summary>
    internal static string QueryIntervalsSql(bool hasLogBytes, bool hasTempDb) => $"""
        {PeriodSql}
        SELECT i.start_time AS StartTime,
               rs.plan_id   AS PlanId,
               {TotalsSql(hasLogBytes, hasTempDb)},
               MIN(rs.first_execution_time) AS FirstExecution,
               MAX(rs.last_execution_time)  AS LastExecution
        {RuntimeInPeriod}
          AND p.query_id = @queryId
        GROUP BY i.start_time, rs.plan_id
        ORDER BY i.start_time, rs.plan_id
        """;

    public async Task<QueryStoreQueryDetails> GetQueryStoreQueryDetailsAsync(long queryId, QueryStorePeriod period)
    {
        using var conn = CreateConnection();
        var features = await FeaturesAsync(conn);

        var info = Row(await QFirst(conn, QueryInfoSql, new { queryId }));
        if (info is null)
            return new QueryStoreQueryDetails(null, [], []);

        var query = new QueryStoreQueryInfo(Long(info, "QueryId"), Long(info, "ObjectId"), StrOrNull(info, "ObjectName"), Str(info, "QueryText"));

        var plans = Rows(await Q(conn, QueryPlansSql, new { queryId })).Select(r => new QueryStorePlanState(
            Long(r, "PlanId"), Bool(r, "IsForced"), Long(r, "ForceFailures"), StrOrNull(r, "LastFailureReason"))).ToList();

        var intervals = Rows(await Q(conn, QueryIntervalsSql(features.LogBytes, features.TempDb),
                Parameters(period, new { queryId }), ExtendedTimeoutSec))
            .Select(r => new QueryStorePlanInterval(
                Utc(r, "StartTime") ?? DateTime.MinValue,
                Long(r, "PlanId"),
                Long(r, "Failed"),
                ReadTotals(r),
                Utc(r, "FirstExecution"),
                Utc(r, "LastExecution")))
            .ToList();

        return new QueryStoreQueryDetails(query, intervals, plans);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // A tab whose view or column this version lacks is left empty with the reason; the rest of the page still loads.
    private async Task<QueryStoreRows<T>> ReadAsync<T>(Func<IDbConnection, Task<List<T>>> read)
    {
        try
        {
            using var conn = CreateConnection();
            return new QueryStoreRows<T>(await read(conn));
        }
        catch (WaitStatsException ex) when (MissingObjectErrors.Contains(ex.SqlErrorNumber))
        {
            return QueryStoreRows<T>.Missing(
                $"This version of SQL Server doesn't have what this tab reads: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // Dapper's dynamic rows are dictionaries of column name to value.
    private static IDictionary<string, object>? Row(object? row) => row as IDictionary<string, object>;

    private static IEnumerable<IDictionary<string, object>> Rows(IEnumerable<dynamic> rows) =>
        rows.Select(r => (IDictionary<string, object>)r);

    private static object? Value(IDictionary<string, object> r, string key) =>
        r.TryGetValue(key, out var v) && v is not DBNull ? v : null;

    private static long Long(IDictionary<string, object> r, string key) =>
        Value(r, key) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;

    private static long? LongOrNull(IDictionary<string, object> r, string key) =>
        Value(r, key) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;

    private static double Dbl(IDictionary<string, object> r, string key) =>
        Value(r, key) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

    private static bool Bool(IDictionary<string, object> r, string key) =>
        Value(r, key) is { } v && Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1;

    private static string Str(IDictionary<string, object> r, string key) => Value(r, key) as string ?? string.Empty;

    private static string? StrOrNull(IDictionary<string, object> r, string key) => Value(r, key) as string;

    // Query Store keeps its times as datetimeoffset, in UTC.
    private static DateTime? Utc(IDictionary<string, object> r, string key) => Value(r, key) switch
    {
        DateTimeOffset offset => offset.UtcDateTime,
        DateTime time         => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _                     => null,
    };

    private static DateTime UtcOrMin(object value) => value switch
    {
        DateTimeOffset offset => offset.UtcDateTime,
        DateTime time         => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _                     => DateTime.MinValue,
    };
}
