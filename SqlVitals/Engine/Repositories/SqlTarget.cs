using Microsoft.Data.SqlClient;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// The server a connection's repositories read (#166): where it is, and how long a query may run.
/// The app builds one per connection from its saved settings; a repository needs nothing else.
/// </summary>
/// <param name="ConnectionString">The SqlClient connection string; empty when no server is set up.</param>
/// <param name="CommandTimeoutSeconds">How long one query may run before SqlClient gives up.</param>
public sealed record SqlTarget(string ConnectionString, int CommandTimeoutSeconds = SqlTarget.DefaultCommandTimeoutSeconds)
{
    /// <summary>A query's time limit when nothing sets one.</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>The Extended Events session SP Trace creates, when nothing names another.</summary>
    public const string DefaultTraceSessionName = "SqlVitals_SpTrace";

    /// <summary>
    /// How long monitoring's reads wait for a lock before they give up and skip the sample (#167).
    /// READ UNCOMMITTED takes no shared locks, but a query still waits on a schema change.
    /// </summary>
    public const int MonitoringLockTimeoutMilliseconds = 2000;

    /// <summary>
    /// Reads run under READ UNCOMMITTED: they take no shared locks, so they don't wait on the
    /// workload's row and page locks. They can still wait on a schema-modification lock.
    /// </summary>
    public bool UseNoLock { get; init; } = true;

    /// <summary>
    /// <c>SET LOCK_TIMEOUT</c> for every read (#167): -1, the default, waits up to the command
    /// timeout, as SQL Server does; monitoring sets <see cref="MonitoringLockTimeoutMilliseconds"/>.
    /// </summary>
    public int LockTimeoutMilliseconds { get; init; } = -1;

    /// <summary>The Extended Events session SP Trace creates and drops on the server.</summary>
    public string TraceSessionName { get; init; } = DefaultTraceSessionName;

    /// <summary>
    /// The login the connection signs in as, for the <c>GRANT</c> that fixes a missing permission
    /// (#167): DOMAIN\user with Windows authentication, else its User ID; null when it names none.
    /// </summary>
    public string? Login
    {
        get
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(ConnectionString);
                if (builder.IntegratedSecurity)
                    return string.IsNullOrEmpty(Environment.UserDomainName)
                        ? Environment.UserName
                        : $@"{Environment.UserDomainName}\{Environment.UserName}";
                return string.IsNullOrWhiteSpace(builder.UserID) ? null : builder.UserID;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    // The connection string holds the password: never in a log or an exception's message.
    public override string ToString() =>
        $"SqlTarget {{ CommandTimeoutSeconds = {CommandTimeoutSeconds}, UseNoLock = {UseNoLock}, LockTimeoutMilliseconds = {LockTimeoutMilliseconds} }}";
}
