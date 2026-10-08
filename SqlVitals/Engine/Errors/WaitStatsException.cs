using Microsoft.Data.SqlClient;

namespace SqlVitals.Engine.Errors;

/// <summary>
/// Wraps any exception thrown during a repository query, tagging it with the
/// originating component and method so the error message pinpoints the exact
/// query without requiring a stack trace.
///
/// Format:  [Component.Method] underlying message
/// Example: [WaitStatsRepository.GetCumulativeWaitsAsync] Timeout expired.
///
/// Search the codebase for the Component.Method value to jump straight to the
/// query that failed.
/// </summary>
public sealed class WaitStatsException : Exception
{
    /// <summary>Class that owns the failing query, e.g. "WaitStatsRepository".</summary>
    public string Component { get; }

    /// <summary>Method that issued the query, e.g. "GetCumulativeWaitsAsync".</summary>
    public string Operation { get; }

    /// <summary>
    /// SQL Server error number when the root cause is a SqlException (e.g. 4060 = DB not found,
    /// -2 = timeout, 18456 = login failed). 0 when not a SQL error.
    /// </summary>
    public int SqlErrorNumber { get; }

    /// <summary>Short tag suitable for display and code-search: "Component.Operation".</summary>
    public string ErrorTag => $"{Component}.{Operation}";

    /// <summary>The failure classified (#167): a missing permission, a lock timeout, a timeout, or other.</summary>
    public SqlFailure Failure { get; }

    /// <summary>The login the query ran as, for the <c>GRANT</c> that fixes a permission failure; null when unknown.</summary>
    public string? Login { get; }

    /// <summary>The script that fixes a permission failure; null for any other.</summary>
    public string? FixScript => Failure.FixScript(Login);

    public WaitStatsException(string component, string operation, Exception inner, string? login = null)
        : this(component, operation, inner,
               inner is SqlException sql ? SqlFailure.Classify(sql.Number, sql.Message) : SqlFailure.None, login)
    {
    }

    /// <summary>A failure already classified: one a host read another way, or a test's.</summary>
    public WaitStatsException(string component, string operation, Exception inner, SqlFailure failure, string? login = null)
        : base($"[{component}.{operation}] {inner.Message}", inner)
    {
        Component      = component;
        Operation      = operation;
        SqlErrorNumber = inner is SqlException sql ? sql.Number : failure.Number;
        Failure        = failure;
        Login          = login;
    }
}
