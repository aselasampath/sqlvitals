using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Permissions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// The first-run permission check (#177) on a real server: the sysadmin has everything, a bare
/// login has nothing, and the GRANT script the check writes gives it all back.
/// </summary>
[Collection(SqlServerTestGroup.Name)]
public class PermissionCheckTests(SqlServerFixture server)
{
    [SqlServerFact]
    [Covers(nameof(IServerRepository.CheckPermissionsAsync))]
    public async Task TheSysadmin_HasEveryPermission()
    {
        var report = PermissionCheck.Evaluate(await server.NewRepository().CheckPermissionsAsync());

        Assert.True(report.AllGranted, Describe(report));
        Assert.True(report.Probe.LoginIsOwn);
        Assert.Equal(SqlServerFixture.Database, report.Probe.Database);
        Assert.Equal(AzureKind.None, report.Probe.Azure);
    }

    [SqlServerFact]
    public async Task ABareLogin_MissesThemAll_AndTheGrantScriptGivesThemBack()
    {
        var login = $"sqlvitals_it_{Guid.NewGuid():N}"[..24];
        const string password = "Bare-login-1!";
        var admin = new SqlConnectionStringBuilder(server.Target.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(admin, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;");
        try
        {
            var bare = new SqlTarget(new SqlConnectionStringBuilder(admin)
            {
                UserID = login, Password = password, IntegratedSecurity = false, Pooling = false,
            }.ConnectionString);

            var before = PermissionCheck.Evaluate(await new ServerRepository(bare).CheckPermissionsAsync());
            Assert.Equal(login, before.Probe.Login);
            Assert.True(before.Probe.LoginIsOwn);
            // Agent's tables count on Express too: the check judges them not needed there.
            Assert.Contains(PermissionArea.ServerState, before.Missing.Select(r => r.Area));
            Assert.Contains(PermissionArea.BackupHistory, before.Missing.Select(r => r.Area));
            Assert.Contains(PermissionArea.Definitions, before.Missing.Select(r => r.Area));

            // What a DBA would paste into SSMS, run as the sysadmin.
            await ExecuteAsync(admin, before.GrantScript!);

            var after = PermissionCheck.Evaluate(await new ServerRepository(bare).CheckPermissionsAsync());
            Assert.True(after.AllGranted, Describe(after) + Environment.NewLine + before.GrantScript);
        }
        finally
        {
            await ExecuteAsync(admin, $"""
                USE [msdb];
                IF DATABASE_PRINCIPAL_ID(N'{login}') IS NOT NULL DROP USER [{login}];
                USE [master];
                DROP LOGIN [{login}];
                """);
        }
    }

    private static string Describe(PermissionReport report) =>
        string.Join("; ", report.Results.Select(r => $"{r.Area}={r.State} {string.Join(",", r.MissingTables)}"));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync();
    }
}
