using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>
/// Columns newer versions added (#109): each query names one only where the server has it, since
/// naming a missing column fails the whole query with error 207.
/// </summary>
public class VersionColumnsSqlTests
{
    [Fact]
    public void The_column_probe_asks_sys_all_columns_for_that_view_and_column()
    {
        var sql = WaitStatsRepository.HasColumnSql("sys.dm_exec_requests", "dop");

        Assert.Contains("FROM sys.all_columns", sql);
        Assert.Contains("OBJECT_ID(N'sys.dm_exec_requests')", sql);
        Assert.Contains("name = N'dop'", sql);
        Assert.Contains("AS HasColumn", sql);
    }

    // ── Active waits: sys.dm_exec_requests.dop, SQL Server 2016+ ──────────

    [Fact]
    public void Active_waits_read_dop_where_the_server_has_it()
    {
        Assert.Contains("r.dop AS DegreeOfParallelism", WaitStatsRepository.ActiveWaitsSql(hasDop: true));
    }

    [Fact]
    public void Active_waits_before_2016_read_a_dop_of_0()
    {
        var sql = WaitStatsRepository.ActiveWaitsSql(hasDop: false);

        Assert.DoesNotContain("r.dop", sql);
        Assert.Contains("0 AS DegreeOfParallelism", sql);
    }

    // ── Stale statistics: sys.stats.is_incremental, SQL Server 2014+ ──────

    [Fact]
    public void Stale_statistics_read_is_incremental_where_the_server_has_it()
    {
        Assert.Contains("s.is_incremental AS IsIncremental", WaitStatsRepository.StaleStatisticsSql(hasIncremental: true));
    }

    [Fact]
    public void Stale_statistics_before_2014_are_never_incremental()
    {
        var sql = WaitStatsRepository.StaleStatisticsSql(hasIncremental: false);

        Assert.DoesNotContain("is_incremental", sql);
        Assert.Contains("CAST(0 AS BIT) AS IsIncremental", sql);
    }

    // ── Query Store: sys.query_store_plan.plan_type_desc, SQL Server 2022+ ─

    [Fact]
    public void Query_store_reads_the_plan_type_where_the_server_has_it()
    {
        Assert.Contains("p.plan_type_desc AS plan_type_desc", WaitStatsRepository.QueryStoreTopQueriesSql(hasPlanType: true));
    }

    [Fact]
    public void Query_store_before_2022_calls_every_plan_a_compiled_plan()
    {
        var sql = WaitStatsRepository.QueryStoreTopQueriesSql(hasPlanType: false);

        Assert.DoesNotContain("p.plan_type_desc", sql);
        Assert.Contains("N'Compiled Plan' AS plan_type_desc", sql);
    }

    // ── DB Storage: sys.databases.is_query_store_on, SQL Server 2016+ ─────

    [Fact]
    public void Database_files_read_query_store_on_where_the_server_has_it()
    {
        Assert.Contains("CAST(d.is_query_store_on", WaitStatsRepository.DatabaseFilesSql(hasQueryStore: true));
    }

    [Fact]
    public void Database_files_before_2016_have_query_store_off()
    {
        var sql = WaitStatsRepository.DatabaseFilesSql(hasQueryStore: false);

        Assert.DoesNotContain("is_query_store_on", sql);
        Assert.Contains("CAST(0        AS BIT) AS QueryStoreOn", sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_placeholder_is_replaced(bool has)
    {
        foreach (var sql in new[]
                 {
                     WaitStatsRepository.ActiveWaitsSql(has),
                     WaitStatsRepository.StaleStatisticsSql(has),
                     WaitStatsRepository.QueryStoreTopQueriesSql(has),
                     WaitStatsRepository.DatabaseFilesSql(has),
                 })
            Assert.DoesNotContain("/*", sql);
    }

    // ── Query Store read-only reason ──────────────────────────────────────

    [Fact]
    public void A_readonly_reason_of_0_is_no_reason()
    {
        Assert.Null(WaitStatsRepository.DescribeReadOnlyReason(0));
    }

    [Theory]
    [InlineData(1,      "the database is read-only")]
    [InlineData(8,      "the database is a secondary replica")]
    [InlineData(65536,  "Query Store reached MAX_STORAGE_SIZE_MB")]
    [InlineData(524288, "the database reached its disk size limit")]
    public void A_readonly_reason_is_described_in_words(int reason, string expected)
    {
        Assert.Equal(expected, WaitStatsRepository.DescribeReadOnlyReason(reason));
    }

    [Fact]
    public void Several_readonly_reasons_are_all_described_and_unknown_bits_are_kept()
    {
        // readonly_reason is a bit map, and an int: reading it as a string failed the Query Store page.
        var text = WaitStatsRepository.DescribeReadOnlyReason(1 | 65536 | 16);

        Assert.Equal("the database is read-only; Query Store reached MAX_STORAGE_SIZE_MB; reason code 16", text);
    }
}
