using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>Builds a saved connection's repositories (#166): the container's one way to reach a server.</summary>
public interface IRepositoryFactory
{
    /// <summary>
    /// The server a connection reads: its connection string and the command timeout over the
    /// defaults in appsettings.json. With no connection, the file's connection string (usually empty).
    /// </summary>
    /// <param name="commandTimeoutSeconds">Settings' timeout; 0 or less keeps the file's.</param>
    SqlTarget TargetFor(ConnectionSettings? connection, int commandTimeoutSeconds);

    /// <summary>The repositories for one server.</summary>
    IWaitStatsRepository Create(SqlTarget target);
}

/// <summary>
/// <see cref="IRepositoryFactory"/> on top of appsettings.json, read once when the app starts: the
/// fallback connection string, the command timeout, READ UNCOMMITTED and SP Trace's session name.
/// </summary>
public sealed class RepositoryFactory(IConfiguration appSettings) : IRepositoryFactory
{
    private readonly SqlTarget _defaults = new(
        appSettings.GetConnectionString("SqlServer") ?? string.Empty,
        appSettings.GetValue("QuerySettings:CommandTimeoutSeconds", SqlTarget.DefaultCommandTimeoutSeconds))
    {
        UseNoLock        = appSettings.GetValue("QuerySettings:UseNoLock", true),
        TraceSessionName = appSettings["TraceSettings:SessionName"] ?? SqlTarget.DefaultTraceSessionName,
    };

    /// <summary>appsettings.json beside the exe.</summary>
    public static IConfiguration ReadAppSettings() =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    public SqlTarget TargetFor(ConnectionSettings? connection, int commandTimeoutSeconds) =>
        _defaults with
        {
            ConnectionString      = connection?.ConnectionString is { } saved && !string.IsNullOrWhiteSpace(saved) ? saved : _defaults.ConnectionString,
            CommandTimeoutSeconds = commandTimeoutSeconds > 0 ? commandTimeoutSeconds : _defaults.CommandTimeoutSeconds,
        };

    public IWaitStatsRepository Create(SqlTarget target) => new WaitStatsRepository(target);

    /// <summary>
    /// Identifies everything a repository is built from, so callers can tell whether an edited
    /// connection needs a new one.
    /// </summary>
    public static string Fingerprint(ConnectionSettings? connection, int commandTimeoutSeconds) =>
        connection is null ? string.Empty : $"{connection.Id}|{connection.ConnectionString}|{commandTimeoutSeconds}";
}
