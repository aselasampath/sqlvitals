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

    // ── Azure SQL Database's resource figures (#234) ─────────────────────

    [Fact]
    public void AzureSqlDatabase_ReadsCpuDataIoLogWriteAndTheDtuLimit_FromOneRow()
    {
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb: true);

        // One OUTER APPLY of the latest row: the figures are from the same 15 seconds.
        Assert.Equal(2, sql.Split("sys.dm_db_resource_stats").Length);
        Assert.Contains("ORDER BY end_time DESC) AS rs", sql);
        Assert.Contains("rs.avg_cpu_percent AS SqlCpuUtilizationPct", sql);
        Assert.Contains("rs.avg_data_io_percent AS AzureDataIoPct", sql);
        Assert.Contains("rs.avg_log_write_percent AS AzureLogWritePct", sql);
        Assert.Contains("rs.dtu_limit AS AzureDtuLimit", sql);
    }

    [Fact]
    public void OtherEditions_HaveNullAzureFigures_AndNoRow()
    {
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb: false);

        Assert.Contains("CAST(NULL AS DECIMAL(5,2)) AS AzureDataIoPct", sql);
        Assert.Contains("CAST(NULL AS DECIMAL(5,2)) AS AzureLogWritePct", sql);
        Assert.Contains("CAST(NULL AS INT) AS AzureDtuLimit", sql);
        Assert.DoesNotContain("rs.", sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheAzurePlaceholdersAreAlwaysReplaced(bool isAzureSqlDb)
    {
        var sql = LiveMetricsRepository.LiveMetricsSql(isAzureSqlDb);

        Assert.DoesNotContain("/*AZURE_COLUMNS*/", sql);
        Assert.DoesNotContain("/*AZURE_APPLY*/", sql);
    }
}
