using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>Builds repositories for a saved connection, on top of appsettings.json.</summary>
public static class RepositoryFactory
{
    /// <summary>
    /// Builds config from appsettings.json overlaid with the given connection. With no connection
    /// the appsettings.json connection string (usually empty) is used as-is.
    /// </summary>
    public static IWaitStatsRepository Create(
        ConnectionSettings? connection, int commandTimeoutSeconds, out string resolvedConnectionString)
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false);

        var overrides = new Dictionary<string, string?>();

        var connectionString = connection?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(connectionString))
            overrides["ConnectionStrings:SqlServer"] = connectionString;

        if (commandTimeoutSeconds > 0)
            overrides["QuerySettings:CommandTimeoutSeconds"] = commandTimeoutSeconds.ToString();

        if (overrides.Count > 0)
            configBuilder.AddInMemoryCollection(overrides);

        var config = configBuilder.Build();
        resolvedConnectionString = config.GetConnectionString("SqlServer") ?? string.Empty;

        return new WaitStatsRepository(config);
    }

    /// <summary>
    /// Identifies everything a repository is built from, so callers can tell whether an edited
    /// connection needs a new one.
    /// </summary>
    public static string Fingerprint(ConnectionSettings? connection, int commandTimeoutSeconds) =>
        connection is null ? string.Empty : $"{connection.Id}|{connection.ConnectionString}|{commandTimeoutSeconds}";
}
