using Microsoft.Data.SqlClient;
using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// The link from a plan to Table Statistics (#244), on a plan the server really wrote: the names in its
/// operators and in OptimizerStatsUsage, on every version CI runs.
/// </summary>
[Collection(SqlServerTestGroup.Name)]
public class PlanTableTargetTests(SqlServerFixture server)
{
    [SqlServerFact]
    public async Task ARealPlan_NamesTheDatabaseTableAndStatistics()
    {
        await using var conn = new SqlConnection(server.Target.ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, """
            IF OBJECT_ID('dbo.PlanLinkOrders') IS NOT NULL DROP TABLE dbo.PlanLinkOrders;
            CREATE TABLE dbo.PlanLinkOrders (Id INT IDENTITY PRIMARY KEY, Status INT NOT NULL);
            INSERT dbo.PlanLinkOrders (Status) SELECT TOP (2000) ABS(CHECKSUM(NEWID())) % 50 FROM sys.all_columns;
            CREATE INDEX IX_PlanLinkOrders_Status ON dbo.PlanLinkOrders (Status);
            """);
        try
        {
            await ExecAsync(conn, "SET SHOWPLAN_XML ON;");
            string xml;
            await using (var command = new SqlCommand("SELECT Id FROM dbo.PlanLinkOrders WHERE Status = 3;", conn))
                xml = (string)(await command.ExecuteScalarAsync())!;
            await ExecAsync(conn, "SET SHOWPLAN_XML OFF;");

            var plan = ShowplanParser.Parse(xml);
            Assert.Null(plan.Error);
            var seek = plan.Statements.SelectMany(s => s.Root.Descendants()).First(o => o.ObjectTable == "PlanLinkOrders");
            var target = PlanTableTarget.For(plan, seek);

            Assert.NotNull(target);
            Assert.Equal(conn.Database, target.Database);
            Assert.Equal("dbo", target.Schema);
            Assert.Equal("PlanLinkOrders", target.Table);
            Assert.Equal("IX_PlanLinkOrders_Status", target.Statistics);

            // An operator with no table has no link.
            Assert.Null(PlanTableTarget.For(plan, plan.Statements[0].Root));
        }
        finally
        {
            await ExecAsync(conn, "IF OBJECT_ID('dbo.PlanLinkOrders') IS NOT NULL DROP TABLE dbo.PlanLinkOrders;");
        }
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var command = new SqlCommand(sql, conn);
        await command.ExecuteNonQueryAsync();
    }
}
