using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>
/// The Query Store page's SQL (#111). There is no SQL Server in the tests, so each variant is parsed
/// with ScriptDom under SQL Server 2016's and 2022's grammar, and checked to name SQL Server 2017+
/// and 2022+ columns and views only when asked.
/// </summary>
public class QueryStoreSqlTests
{
    // BaseRepository puts this ahead of every query.
    private const string IsolationPrefix = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";

    private static IEnumerable<(string Name, string Sql)> Every2016Variant()
    {
        yield return ("Features", QueryStoreRepository.FeaturesSql);
        yield return ("Health 2016", QueryStoreRepository.HealthSql(false));
        yield return ("Top 20 legacy 2016", QueryStoreRepository.QueryStoreTopQueriesSql(false));
        foreach (var m in QueryStoreMetrics.Available(false))
            foreach (var s in Enum.GetValues<QueryStoreStatistic>())
                yield return ($"Top {m.Metric} {s}", QueryStoreRepository.TopQueriesSql(m.Metric, s));
        yield return ("Over time 2016", QueryStoreRepository.OverTimeSql(false, false));
        foreach (var m in QueryStoreMetrics.VariationMetrics)
            yield return ($"Variation {m}", QueryStoreRepository.HighVariationSql(m));
        yield return ("Forced 2016", QueryStoreRepository.ForcedPlansSql(false));
        yield return ("Query info", QueryStoreRepository.QueryInfoSql);
        yield return ("Query plans", QueryStoreRepository.QueryPlansSql);
        yield return ("Query intervals 2016", QueryStoreRepository.QueryIntervalsSql(false, false));
    }

    private static IEnumerable<(string Name, string Sql)> Every2017Variant()
    {
        yield return ("Health 2017", QueryStoreRepository.HealthSql(true));
        foreach (var s in Enum.GetValues<QueryStoreStatistic>())
        {
            yield return ($"Top LogUsed {s}", QueryStoreRepository.TopQueriesSql(QueryStoreMetric.LogUsed, s));
            yield return ($"Top TempDbUsed {s}", QueryStoreRepository.TopQueriesSql(QueryStoreMetric.TempDbUsed, s));
        }
        yield return ("Over time 2017", QueryStoreRepository.OverTimeSql(true, true));
        yield return ("Wait categories", QueryStoreRepository.WaitCategoriesSql);
        yield return ("Wait queries", QueryStoreRepository.WaitQueriesSql);
        yield return ("Forced 2017", QueryStoreRepository.ForcedPlansSql(true));
        yield return ("Query intervals 2017", QueryStoreRepository.QueryIntervalsSql(true, true));
    }

    private static IEnumerable<(string Name, string Sql)> Every2022Variant()
    {
        yield return ("Top 20 legacy 2022", QueryStoreRepository.QueryStoreTopQueriesSql(true));
        yield return ("Hints", QueryStoreRepository.HintsSql);
    }

    private static IEnumerable<(string Name, string Sql)> EveryVariant() =>
        Every2016Variant().Concat(Every2017Variant()).Concat(Every2022Variant());

    public static TheoryData<string> Parsers => new() { "TSql130", "TSql160" };

    [Theory]
    [MemberData(nameof(Parsers))]
    public void Every_variant_parses(string grammar)
    {
        TSqlParser parser = grammar == "TSql130" ? new TSql130Parser(true) : new TSql160Parser(true);
        foreach (var (name, sql) in EveryVariant())
        {
            parser.Parse(new StringReader(IsolationPrefix + sql), out var errors);
            Assert.True(errors.Count == 0, $"{name} ({grammar}): {string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}"))}");
        }
    }

    [Fact]
    public void Every_placeholder_is_replaced()
    {
        foreach (var (name, sql) in EveryVariant())
            Assert.False(sql.Contains("/*"), $"{name} still has a placeholder");
    }

    [Fact]
    public void Sql_server_2016_variants_name_no_2017_or_2022_column_or_view()
    {
        // As references: a column after its table's alias, a view after "sys.". The legacy top 20
        // keeps plan_type_desc as an alias for N'Compiled Plan'.
        string[] newer =
        [
            "_log_bytes_used", "_tempdb_space_used", "sys.query_store_wait_stats", " wait_stats_capture_mode_desc",
            "p.plan_forcing_type_desc", "p.plan_type_desc", "sys.query_store_query_hints",
        ];
        foreach (var (name, sql) in Every2016Variant().Where(v => v.Name != "Features"))
            foreach (var column in newer)
                Assert.False(sql.Contains(column), $"{name} names {column}");
    }

    [Fact]
    public void The_2017_columns_are_named_when_asked()
    {
        Assert.Contains("wait_stats_capture_mode_desc AS WaitStatsCaptureMode", QueryStoreRepository.HealthSql(true));
        Assert.Contains("rs.avg_log_bytes_used", QueryStoreRepository.TopQueriesSql(QueryStoreMetric.LogUsed, QueryStoreStatistic.Total));
        Assert.Contains("rs.stdev_tempdb_space_used", QueryStoreRepository.TopQueriesSql(QueryStoreMetric.TempDbUsed, QueryStoreStatistic.StdDev));
        Assert.Contains("rs.avg_log_bytes_used", QueryStoreRepository.OverTimeSql(true, true));
        Assert.Contains("rs.avg_tempdb_space_used", QueryStoreRepository.QueryIntervalsSql(true, true));
        Assert.Contains("p.plan_forcing_type_desc", QueryStoreRepository.ForcedPlansSql(true));
        Assert.Contains("N'MANUAL'", QueryStoreRepository.ForcedPlansSql(false));
    }

    [Fact]
    public void Only_the_hints_query_reads_the_2022_hints_view()
    {
        foreach (var (name, sql) in Every2016Variant().Concat(Every2017Variant()).Where(v => v.Name != "Features"))
            Assert.False(sql.Contains("query_store_query_hints"), $"{name} reads the hints");
        Assert.Contains("FROM sys.query_store_query_hints h", QueryStoreRepository.HintsSql);
    }

    [Fact]
    public void The_features_probe_asks_sys_all_columns_for_each_view_and_column()
    {
        var sql = QueryStoreRepository.FeaturesSql;

        Assert.Contains("OBJECT_ID(N'sys.query_store_wait_stats') AND name = N'wait_category'", sql);
        Assert.Contains("OBJECT_ID(N'sys.query_store_plan') AND name = N'plan_forcing_type_desc'", sql);
        Assert.Contains("OBJECT_ID(N'sys.query_store_query_hints') AND name = N'query_hint_text'", sql);
        Assert.Contains("OBJECT_ID(N'sys.query_store_runtime_stats') AND name = N'avg_tempdb_space_used'", sql);
    }

    [Fact]
    public void Periods_count_whole_intervals_by_their_start_on_the_servers_clock()
    {
        foreach (var (name, sql) in EveryVariant().Where(v => v.Sql.Contains("@periodFrom")))
        {
            Assert.Contains("ISNULL(@fromUtc, DATEADD(MINUTE, -@minutes, @periodTo))", sql);
            Assert.Contains("i.start_time >= @periodFrom AND i.start_time < @periodTo", sql);
            Assert.False(sql.Contains("last_execution_time >="), $"{name} filters on the last execution");
        }
    }

    [Fact]
    public void Top_queries_are_one_row_per_query_over_all_its_plans()
    {
        var sql = QueryStoreRepository.TopQueriesSql(QueryStoreMetric.Duration, QueryStoreStatistic.Total);

        Assert.Contains("GROUP BY p.query_id, rs.plan_id", sql);
        Assert.Contains("COUNT(*)            AS plans", sql);
        Assert.Contains("SELECT TOP (@top) *", sql);
        Assert.Contains("rs.execution_type <> 0", sql);   // Aborted and Exception count as failed
        Assert.Contains("ORDER BY n DESC, plan_id DESC", sql);   // the most-used plan
    }

    [Theory]
    [InlineData(QueryStoreStatistic.Total,   "SELECT TOP (@top) *, m_sum AS metric_value")]
    [InlineData(QueryStoreStatistic.Average, "m_sum / NULLIF(n, 0) AS metric_value")]
    [InlineData(QueryStoreStatistic.Max,     "SELECT TOP (@top) *, m_max AS metric_value")]
    [InlineData(QueryStoreStatistic.StdDev,  "SQRT(CASE WHEN m_sum_sq / NULLIF(n, 0) - SQUARE(m_sum / NULLIF(n, 0)) > 0")]
    public void Each_statistic_ranks_by_its_own_figure(QueryStoreStatistic statistic, string expected) =>
        Assert.Contains(expected, QueryStoreRepository.TopQueriesSql(QueryStoreMetric.Cpu, statistic));

    [Fact]
    public void Executions_rank_by_the_count_whatever_the_statistic() =>
        Assert.Contains("CAST(n AS float) AS metric_value",
            QueryStoreRepository.TopQueriesSql(QueryStoreMetric.Executions, QueryStoreStatistic.StdDev));

    [Fact]
    public void High_variation_ranks_by_the_combined_std_dev_with_enough_executions()
    {
        var sql = QueryStoreRepository.HighVariationSql(QueryStoreMetric.Duration);

        Assert.Contains("SUM(rs.count_executions * (SQUARE(rs.stdev_duration) + SQUARE(rs.avg_duration)))", sql);
        Assert.Contains("HAVING SUM(n) >= @minExecutions", sql);
        Assert.Contains("ORDER BY m_stdev DESC", sql);
    }

    [Fact]
    public void Over_time_puts_each_interval_in_one_slot() =>
        Assert.Contains("DATEDIFF(MINUTE, @origin, i.start_time) / @slotMinutes AS slot", QueryStoreRepository.OverTimeSql(false, false));
}
