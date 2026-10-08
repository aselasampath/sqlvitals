using Microsoft.Extensions.Configuration;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// appsettings.json now ships from Desktop, not from Engine as a web host (#165): it is still
/// copied beside the exe, holds what the repositories read, and a saved connection overrides it.
/// </summary>
public sealed class RepositoryFactoryTests
{
    private static IConfiguration Shipped() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    [Fact]
    public void TheShippedFile_HoldsTheDefaultsTheRepositoriesRead()
    {
        var config = Shipped();

        Assert.Equal("", config.GetConnectionString("SqlServer"));
        Assert.Equal(30, config.GetValue<int>("QuerySettings:CommandTimeoutSeconds"));
        Assert.True(config.GetValue<bool>("QuerySettings:UseNoLock"));
        Assert.Equal("SqlVitals_SpTrace", config["TraceSettings:SessionName"]);
    }

    [Fact]
    public void TheShippedFile_HasNoWebHostSettings()
    {
        var sections = Shipped().GetChildren().Select(s => s.Key);

        Assert.Equal(["ConnectionStrings", "QuerySettings", "TraceSettings"], sections.Order());
    }

    [Fact]
    public void ASavedConnection_OverridesTheFallbackConnectionString()
    {
        var connection = ConnectionSettingsServiceTests.Connection();

        var target = new RepositoryFactory(Shipped()).TargetFor(connection, commandTimeoutSeconds: 60);

        Assert.Equal(connection.ConnectionString, target.ConnectionString);
        Assert.Equal(60, target.CommandTimeoutSeconds);
    }

    [Fact]
    public void NoConnection_UsesTheFallbackFromTheFile()
    {
        var target = new RepositoryFactory(Shipped()).TargetFor(null, commandTimeoutSeconds: 0);

        Assert.Equal("", target.ConnectionString);
    }

    // ── The target the repositories read (#166) ──────────────────────────

    [Fact]
    public void TheTarget_CarriesTheFilesDefaults_WhenSettingsLeaveThemAlone()
    {
        var target = new RepositoryFactory(Shipped()).TargetFor(ConnectionSettingsServiceTests.Connection(), commandTimeoutSeconds: 0);

        Assert.Equal(30, target.CommandTimeoutSeconds);
        Assert.True(target.UseNoLock);
        Assert.Equal("SqlVitals_SpTrace", target.TraceSessionName);
    }

    [Fact]
    public void TheTarget_FollowsAnEditedFile()
    {
        var edited = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"]          = "Server=fallback;Integrated Security=true",
            ["QuerySettings:CommandTimeoutSeconds"]  = "45",
            ["QuerySettings:UseNoLock"]              = "false",
            ["TraceSettings:SessionName"]            = "OurTrace",
        }).Build();

        var target = new RepositoryFactory(edited).TargetFor(null, commandTimeoutSeconds: 0);

        Assert.Equal(new SqlTarget("Server=fallback;Integrated Security=true", 45) { UseNoLock = false, TraceSessionName = "OurTrace" }, target);
    }

    [Fact]
    public void TheTarget_NeverPrintsTheConnectionString()
    {
        var target = new SqlTarget("Server=sql01;User ID=sa;Password=s3cret;");

        Assert.DoesNotContain("s3cret", target.ToString());
        Assert.DoesNotContain("sql01", target.ToString());
    }

    [Fact]
    public void TheFactory_BuildsTheRepositoriesForATarget() =>
        Assert.IsType<WaitStatsRepository>(new RepositoryFactory(Shipped()).Create(new SqlTarget("")));
}
