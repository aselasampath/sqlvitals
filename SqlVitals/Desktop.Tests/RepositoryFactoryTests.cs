using Microsoft.Extensions.Configuration;
using SqlVitals.Desktop.Services;

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

        RepositoryFactory.Create(connection, commandTimeoutSeconds: 60, out var resolved);

        Assert.Equal(connection.ConnectionString, resolved);
    }

    [Fact]
    public void NoConnection_UsesTheFallbackFromTheFile()
    {
        RepositoryFactory.Create(null, commandTimeoutSeconds: 0, out var resolved);

        Assert.Equal("", resolved);
    }
}
