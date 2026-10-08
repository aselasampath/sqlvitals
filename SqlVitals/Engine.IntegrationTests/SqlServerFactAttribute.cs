namespace SqlVitals.Engine.IntegrationTests;

/// <summary>Where the tests' SQL Server is, from the environment.</summary>
internal static class TestServer
{
    /// <summary>The variable that holds the connection string: a login that is sysadmin there.</summary>
    public const string Variable = "SQLVITALS_TEST_SERVER";

    /// <summary>The connection string; null when no server is set up, and the tests are skipped.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// On GitHub Actions a missing server is a broken workflow, not a reason to skip: the tests
    /// run and fail, rather than pass without having read anything.
    /// </summary>
    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A test that runs SQL: skipped on a machine with no <see cref="TestServer.Variable"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (TestServer.ConnectionString is null && !TestServer.Required)
            Skip = $"No SQL Server: set {TestServer.Variable} to a connection string whose login is sysadmin.";
    }
}

/// <summary>
/// The repository methods a test runs. <see cref="RepositoryCoverageTests"/> checks that every
/// method of <see cref="Repositories.IWaitStatsRepository"/> is named by one.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CoversAttribute(params string[] methods) : Attribute
{
    public IReadOnlyList<string> Methods { get; } = methods;
}
