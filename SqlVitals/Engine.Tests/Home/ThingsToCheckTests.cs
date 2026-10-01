using SqlVitals.Engine.Home;

namespace SqlVitals.Engine.Tests.Home;

/// <summary>What to check when a server can't be reached (#130), for the way its name is written.</summary>
public sealed class ThingsToCheckTests
{
    [Theory]
    [InlineData("sql01",                   "sql01",     null,       null)]
    [InlineData(" sql01\\SQLEXPRESS ",     "sql01",     "SQLEXPRESS", null)]
    [InlineData("sql01,14330",             "sql01",     null,       "14330")]
    [InlineData("tcp:sql01\\BI,1500",      "sql01",     "BI",       "1500")]
    [InlineData(".\\SQLEXPRESS",           "localhost", "SQLEXPRESS", null)]
    [InlineData("(local)",                 "localhost", null,       null)]
    public void Parse_FindsTheHostInstanceAndPort(string server, string host, string? instance, string? port)
    {
        Assert.Equal((host, instance, port), ThingsToCheck.Parse(server));
    }

    [Fact]
    public void ADefaultInstance_IsCheckedOnPort1433()
    {
        Assert.Equal(
        [
            "The SQL Server service on sql01 is running.",
            "This PC can reach sql01 on port 1433 (firewall, VPN).",
            "The server name in the connection is still right.",
        ], ThingsToCheck.For("sql01", "A network-related or instance-specific error occurred"));
    }

    [Fact]
    public void ANamedInstance_WithoutAPort_NeedsTheSqlBrowser()
    {
        var items = ThingsToCheck.For("sql01\\SQLEXPRESS", null);

        Assert.Contains(items, i => i.Contains("UDP 1434"));
        Assert.Contains("The SQL Server Browser service is running on sql01, so the SQLEXPRESS instance's port can be found.", items);
    }

    [Fact]
    public void AzureSql_IsAboutItsFirewall_NotAService()
    {
        var items = ThingsToCheck.For("asarnd01.database.windows.net", null);

        Assert.StartsWith("The firewall of the Azure SQL server asarnd01.database.windows.net", items[0]);
        Assert.DoesNotContain(items, i => i.Contains("service on"));
    }

    [Fact]
    public void AFailedLogin_PutsTheLoginFirst()
    {
        var items = ThingsToCheck.For("sql01", "Login failed for user 'sqlvitals'.");

        Assert.StartsWith("The login and password are still right", items[0]);
    }
}
