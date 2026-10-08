using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Extensions.Configuration;
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

    /// <summary>Every probe in one round trip.</summary>
    internal static readonly string FeaturesSql = SqlScripts.Get("QueryStore/Features");

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

    private static readonly string HealthSqlTemplate = SqlScripts.Get("QueryStore/Health");

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

        var db = Row(await QFirst(conn, SqlScripts.Get("QueryStore/DatabaseName")));
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

    private static readonly string QueryStoreTopQueriesSqlTemplate = SqlScripts.Get("QueryStore/OverviewTopQueries");

    // ── The period ────────────────────────────────────────────────────────
    // A preset ends at the server's now (@minutes back); a custom period is fixed (@fromUtc, @toUtc).
    // Each interval counts in the period its start falls in, as on Query Regressions, so a period
    // counts whole intervals.

    internal static readonly string PeriodSql = SqlScripts.Get("QueryStore/Period");

    internal static readonly string RuntimeInPeriod = SqlScripts.Get("QueryStore/RuntimeInPeriod");

    // The procedure, function or trigger a statement is in; NULL for ad hoc SQL (object_id 0).
    internal const string ObjectNameSql =
        "CASE WHEN q.object_id <> 0 THEN OBJECT_SCHEMA_NAME(q.object_id) + N'.' + OBJECT_NAME(q.object_id) END";

    // Picks each query's plan with the most executions in the period, for View Plan.
    internal const string RankedPlans =
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
        return TopQueriesSqlTemplate
            .Replace("/*PERIOD*/", PeriodSql)
            .Replace("/*RUNTIME_IN_PERIOD*/", RuntimeInPeriod)
            .Replace("/*METRIC_SUMS*/", MetricSumsSql(column))
            .Replace("/*RANKED_PLANS*/", RankedPlans)
            .Replace("/*RANK*/", RankSql(column, statistic))
            .Replace("/*OBJECT_NAME*/", ObjectNameSql);
    }

    /// <summary>Each plan's sums of a metric's column; zeros when the metric is the execution count (no column).</summary>
    internal static string MetricSumsSql(string? column) => column is null
        ? "CAST(0 AS float) AS m_sum, CAST(0 AS float) AS m_sum_sq, CAST(0 AS float) AS m_max"
        : $"SUM(rs.avg_{column} * rs.count_executions) AS m_sum,\n" +
          $"               SUM(rs.count_executions * (SQUARE(rs.stdev_{column}) + SQUARE(rs.avg_{column}))) AS m_sum_sq,\n" +
          $"               MAX(CAST(rs.max_{column} AS float)) AS m_max";

    /// <summary>What the top queries are ranked by: the statistic over the sums, or the execution count.</summary>
    internal static string RankSql(string? column, QueryStoreStatistic statistic) => column is null ? "CAST(n AS float)" : statistic switch
    {
        QueryStoreStatistic.Average => "m_sum / NULLIF(n, 0)",
        QueryStoreStatistic.Max     => "m_max",
        QueryStoreStatistic.StdDev  => StdDevSql("m_sum", "m_sum_sq", "n"),
        _                           => "m_sum",
    };

    private static readonly string TopQueriesSqlTemplate = SqlScripts.Get("QueryStore/TopQueries");

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
    internal static string TotalsSql(bool hasLogBytes, bool hasTempDb) => SqlScripts.Get("QueryStore/Totals")
        .Replace("/*LOG_BYTES*/", hasLogBytes ? "SUM(rs.avg_log_bytes_used * rs.count_executions)" : "CAST(0 AS float)")
        .Replace("/*TEMPDB*/", hasTempDb ? "SUM(rs.avg_tempdb_space_used * rs.count_executions)" : "CAST(0 AS float)");

    private static QueryStoreTotals ReadTotals(IDictionary<string, object> r) => new(
        Long(r, "Executions"), Dbl(r, "CpuUs"), Dbl(r, "DurationUs"), Dbl(r, "LogicalReads"), Dbl(r, "LogicalWrites"),
        Dbl(r, "PhysicalReads"), Dbl(r, "MemoryPages"), Dbl(r, "ReturnedRows"), Dbl(r, "LogBytes"), Dbl(r, "TempDbPages"));

    /// <summary>
    /// The database's totals per slot: slot n starts at @origin + n × @slotMinutes. Integer division
    /// of the minutes since @origin puts each interval in exactly one slot.
    /// </summary>
    internal static string OverTimeSql(bool hasLogBytes, bool hasTempDb) => SqlScripts.Get("QueryStore/OverTime")
        .Replace("/*PERIOD*/", PeriodSql)
        .Replace("/*TOTALS*/", TotalsSql(hasLogBytes, hasTempDb));

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

    private static readonly string HighVariationSqlTemplate = SqlScripts.Get("QueryStore/HighVariation");

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

    internal static readonly string WaitCategoriesSql = SqlScripts.Get("QueryStore/WaitCategories")
        .Replace("/*PERIOD*/", PeriodSql);

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
    internal static readonly string WaitQueriesSql = SqlScripts.Get("QueryStore/WaitQueries")
        .Replace("/*PERIOD*/", PeriodSql)
        .Replace("/*RUNTIME_IN_PERIOD*/", RuntimeInPeriod)
        .Replace("/*RANKED_PLANS*/", RankedPlans)
        .Replace("/*OBJECT_NAME*/", ObjectNameSql);

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

    private static readonly string ForcedPlansSqlTemplate = SqlScripts.Get("QueryStore/ForcedPlans");

    /// <summary>Query Store hints: SQL Server 2022+ and Azure SQL only.</summary>
    internal static readonly string HintsSql = SqlScripts.Get("QueryStore/Hints");

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

    internal static readonly string QueryInfoSql = SqlScripts.Get("QueryStore/QueryInfo").Replace("/*OBJECT_NAME*/", ObjectNameSql);

    internal static readonly string QueryPlansSql = SqlScripts.Get("QueryStore/QueryPlans");

    /// <summary>One query's executions per interval and plan in the period, with every metric's total.</summary>
    internal static string QueryIntervalsSql(bool hasLogBytes, bool hasTempDb) => SqlScripts.Get("QueryStore/QueryIntervals")
        .Replace("/*PERIOD*/", PeriodSql)
        .Replace("/*TOTALS*/", TotalsSql(hasLogBytes, hasTempDb))
        .Replace("/*RUNTIME_IN_PERIOD*/", RuntimeInPeriod);

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
