using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class LiveMetricsSqlTests
{
    [Fact]
    public void AzureSqlDatabase_ReadsCpuFromResourceStats()
    {
        // Azure SQL Database has no scheduler monitor records: CPU read 0 % there.
        var sql = WaitStatsRepository.LiveMetricsSql(isAzureSqlDb: true);

        Assert.Contains("sys.dm_db_resource_stats", sql);
        Assert.DoesNotContain("sys.dm_os_ring_buffers", sql);
        Assert.Contains("AS SqlCpuUtilizationPct", sql);
    }

    [Fact]
    public void OtherEditions_ReadCpuFromTheRingBuffer()
    {
        // sys.dm_db_resource_stats doesn't exist on SQL Server; naming it would fail the whole query.
        var sql = WaitStatsRepository.LiveMetricsSql(isAzureSqlDb: false);

        Assert.Contains("sys.dm_os_ring_buffers", sql);
        Assert.DoesNotContain("sys.dm_db_resource_stats", sql);
    }

    [Fact]
    public void TheCpuPlaceholderIsAlwaysReplaced()
    {
        Assert.DoesNotContain("/*CPU*/", WaitStatsRepository.LiveMetricsSql(true));
        Assert.DoesNotContain("/*CPU*/", WaitStatsRepository.LiveMetricsSql(false));
    }
}
