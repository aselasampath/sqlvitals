using SqlVitals.Engine.ConfigChecks;

namespace SqlVitals.Engine.Repositories;

/// <summary>The server's and databases' configuration, checked against best practice (#42).</summary>
public interface IConfigurationRepository
{
    /// <summary>
    /// Reads the settings and runs <see cref="ConfigurationChecks"/> on them. Never throws for a
    /// login without VIEW SERVER STATE: the checks that need it say so instead.
    /// </summary>
    Task<ConfigCheckList> GetConfigurationChecksAsync();
}
