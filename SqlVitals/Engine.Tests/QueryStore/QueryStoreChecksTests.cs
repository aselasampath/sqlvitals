using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Tests.QueryStore;

public class QueryStoreChecksTests
{
    private static QueryStoreHealth Healthy(string db = "Sales") => new(
        db, "READ_WRITE", "READ_WRITE", 0, 400, 1000, 40, 900, 60, 30, "AUTO", "AUTO", "ON", 200,
        1234, 1500, 2, new DateTime(2026, 9, 2, 14, 0, 0, DateTimeKind.Utc), DateTime.Now);

    [Fact]
    public void A_well_set_query_store_has_no_findings()
    {
        var checks = QueryStoreChecks.Run(Healthy());

        Assert.Empty(checks);
        Assert.Equal(QueryStoreCheckLevel.Ok, QueryStoreChecks.Worst(checks));
    }

    [Fact]
    public void Off_is_a_warning_with_the_statement_that_turns_it_on()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { ActualState = "OFF", DesiredState = "OFF", CaptureMode = "ALL" }));

        Assert.Equal(QueryStoreCheckLevel.Warning, check.Level);
        Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE = ON;", check.Fix);
    }

    [Fact]
    public void Read_only_when_it_should_be_read_write_is_a_warning_with_the_reason()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with
        {
            ActualState = "READ_ONLY", ReadOnlyReasonCode = 65536, StorageUsedPct = 100, CurrentStorageSizeMB = 1000,
        }));

        Assert.Equal(QueryStoreCheckLevel.Warning, check.Level);
        Assert.Equal("Should be READ_WRITE but is READ_ONLY", check.Title);
        Assert.Contains("Query Store reached MAX_STORAGE_SIZE_MB", check.Reason);
        Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE (MAX_STORAGE_SIZE_MB = 2000, OPERATION_MODE = READ_WRITE);", check.Fix);
    }

    [Fact]
    public void Read_only_from_another_limit_only_needs_read_write_again()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { ActualState = "READ_ONLY", ReadOnlyReasonCode = 131072 }));

        Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE (OPERATION_MODE = READ_WRITE);", check.Fix);
    }

    [Fact]
    public void Read_only_because_the_database_is_a_secondary_is_only_info_with_no_fix()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { ActualState = "READ_ONLY", ReadOnlyReasonCode = 8 }));

        Assert.Equal(QueryStoreCheckLevel.Info, check.Level);
        Assert.Contains("secondary replica", check.Reason);
        Assert.Null(check.Fix);
    }

    [Fact]
    public void An_error_state_gives_the_consistency_check()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { ActualState = "ERROR" }));

        Assert.Equal(QueryStoreCheckLevel.Warning, check.Level);
        Assert.Contains("sp_query_store_consistency_check", check.Fix);
    }

    [Theory]
    [InlineData(90.0, false)]
    [InlineData(90.1, true)]
    public void Storage_over_90_percent_is_a_warning(double usedPct, bool warns)
    {
        var checks = QueryStoreChecks.Run(Healthy() with { StorageUsedPct = usedPct });

        Assert.Equal(warns, checks.Any(c => c.Title == "Storage nearly full"));
        if (warns)
            Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE (MAX_STORAGE_SIZE_MB = 2000);", checks.Single().Fix);
    }

    [Fact]
    public void Size_based_cleanup_off_is_a_warning()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { SizeCleanupMode = "OFF" }));

        Assert.Equal(QueryStoreCheckLevel.Warning, check.Level);
        Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE (SIZE_BASED_CLEANUP_MODE = AUTO);", check.Fix);
    }

    [Fact]
    public void Capture_mode_all_is_info()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { CaptureMode = "ALL" }));

        Assert.Equal(QueryStoreCheckLevel.Info, check.Level);
        Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE (QUERY_CAPTURE_MODE = AUTO);", check.Fix);
    }

    [Fact]
    public void Wait_stats_capture_off_is_info_and_sql_server_2016_says_nothing()
    {
        var check = Assert.Single(QueryStoreChecks.Run(Healthy() with { WaitStatsCaptureMode = "OFF" }));
        Assert.Equal(QueryStoreCheckLevel.Info, check.Level);
        Assert.Equal("ALTER DATABASE [Sales] SET QUERY_STORE (WAIT_STATS_CAPTURE_MODE = ON);", check.Fix);

        Assert.Empty(QueryStoreChecks.Run(Healthy() with { WaitStatsCaptureMode = null }));
    }

    [Fact]
    public void The_status_is_the_worst_finding()
    {
        var infoOnly = QueryStoreChecks.Run(Healthy() with { CaptureMode = "ALL", WaitStatsCaptureMode = "OFF" });
        Assert.Equal(QueryStoreCheckLevel.Info, QueryStoreChecks.Worst(infoOnly));

        var mixed = QueryStoreChecks.Run(Healthy() with { CaptureMode = "ALL", SizeCleanupMode = "OFF" });
        Assert.Equal(QueryStoreCheckLevel.Warning, QueryStoreChecks.Worst(mixed));
    }

    [Fact]
    public void The_database_name_is_quoted() =>
        Assert.StartsWith("ALTER DATABASE [we]]ird]", QueryStoreChecks.Run(Healthy("we]ird") with { CaptureMode = "ALL" }).Single().Fix);
}
