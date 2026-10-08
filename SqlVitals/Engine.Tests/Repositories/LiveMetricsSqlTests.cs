using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class LiveMetricsSqlTests
{
    [Fact]
    public void AzureSqlDatabase_ReadsCpuFromResourceStats()
    {
        // Azure SQL Database has no scheduler monitor records: CPU read 0 % there.
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb: true);

        Assert.Contains("sys.dm_db_resource_stats", sql);
        Assert.DoesNotContain("sys.dm_os_ring_buffers", sql);
        Assert.Contains("AS SqlCpuUtilizationPct", sql);
    }

    [Fact]
    public void OtherEditions_ReadCpuFromTheRingBuffer()
    {
        // sys.dm_db_resource_stats doesn't exist on SQL Server; naming it would fail the whole query.
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb: false);

        Assert.Contains("sys.dm_os_ring_buffers", sql);
        Assert.DoesNotContain("sys.dm_db_resource_stats", sql);
    }

    [Fact]
    public void TheCpuPlaceholderIsAlwaysReplaced()
    {
        Assert.DoesNotContain("/*CPU*/", LiveMetricsRepository.LiveMetricsSql(true));
        Assert.DoesNotContain("/*CPU*/", LiveMetricsRepository.LiveMetricsSql(false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parses_UnderSqlServer2012sAnd2022sGrammar(bool isAzureSqlDb)
    {
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb);
        foreach (TSqlParser parser in new TSqlParser[] { new TSql110Parser(true), new TSql160Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}")));
        }
    }

    [Fact]
    public void CountsTheUserTasksWaitingNow()
    {
        // The Overview's "Waiting tasks" headline (#143).
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb: false);

        Assert.Contains("sys.dm_os_waiting_tasks", sql);
        Assert.Contains("is_user_process = 1", sql);
        Assert.Contains("AS WaitingTasks", sql);
    }
}
