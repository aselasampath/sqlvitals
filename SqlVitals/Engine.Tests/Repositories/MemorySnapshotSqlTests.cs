using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class MemorySnapshotSqlTests
{
    [Fact]
    public void WithTheColumn_ReadsTheMemoryModel()
    {
        // 2012 SP4, 2016 SP1 and later, Managed Instance.
        var sql = WaitStatsRepository.MemorySnapshotSql(isAzureSqlDb: false, hasMemoryModel: true);

        Assert.Contains("osi.sql_memory_model_desc", sql);
        Assert.Contains("sys.dm_os_sys_memory", sql);
    }

    [Fact]
    public void WithoutTheColumn_ShowsNotAvailable()
    {
        // 2016 RTM and unpatched 2012/2014: naming the column would fail the whole query (error 207).
        var sql = WaitStatsRepository.MemorySnapshotSql(isAzureSqlDb: false, hasMemoryModel: false);

        Assert.DoesNotContain("memory_model", sql);
        Assert.Contains("ISNULL(MAX(N'N/A'), N'N/A')", sql);
        Assert.Contains("sys.dm_os_sys_memory", sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AzureSqlDatabase_ReadsCountersOnly(bool hasMemoryModel)
    {
        // Azure SQL Database doesn't expose sys.dm_os_sys_memory or the host's memory model.
        var sql = WaitStatsRepository.MemorySnapshotSql(isAzureSqlDb: true, hasMemoryModel);

        Assert.DoesNotContain("sys.dm_os_sys_memory", sql);
        Assert.DoesNotContain("sys.dm_os_sys_info", sql);
        Assert.DoesNotContain("memory_model", sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePlaceholderIsAlwaysReplaced(bool hasMemoryModel)
    {
        Assert.DoesNotContain("/*MEMORY_MODEL*/", WaitStatsRepository.MemorySnapshotSql(false, hasMemoryModel));
    }

    [Fact]
    public void TheMemoryModelIsNeverGroupedBy()
    {
        // Grouping by the column would name it again, even on the N/A query.
        var sql = WaitStatsRepository.MemorySnapshotSql(false, hasMemoryModel: true);
        var groupBy = sql[sql.IndexOf("GROUP BY", StringComparison.Ordinal)..];

        Assert.DoesNotContain("memory_model", groupBy);
    }

    [Fact]
    public void TheProbeChecksForTheRealColumnName()
    {
        // The column is sql_memory_model_desc; memory_model_desc doesn't exist on any version.
        Assert.Contains("N'sql_memory_model_desc'", WaitStatsRepository.MemorySnapshotProbeSql);
        Assert.Contains("OBJECT_ID(N'sys.dm_os_sys_info')", WaitStatsRepository.MemorySnapshotProbeSql);
        Assert.Contains("AS HasMemoryModel", WaitStatsRepository.MemorySnapshotProbeSql);
        Assert.DoesNotContain("ProductMajorVersion", WaitStatsRepository.MemorySnapshotProbeSql);
    }
}
