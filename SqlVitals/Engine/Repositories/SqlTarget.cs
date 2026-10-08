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

    /// <summary>Reads run under READ UNCOMMITTED, so a monitoring query never waits on the workload's locks.</summary>
    public bool UseNoLock { get; init; } = true;

    /// <summary>The Extended Events session SP Trace creates and drops on the server.</summary>
    public string TraceSessionName { get; init; } = DefaultTraceSessionName;

    // The connection string holds the password: never in a log or an exception's message.
    public override string ToString() => $"SqlTarget {{ CommandTimeoutSeconds = {CommandTimeoutSeconds}, UseNoLock = {UseNoLock} }}";
}
