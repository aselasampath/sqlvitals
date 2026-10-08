using System.Data;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// What every area's repository shares: a connection to its <see cref="SqlTarget"/>, the query
/// time limit, isolation level and lock timeout, the server's capabilities, cancellation, and
/// errors wrapped with the query's name.
/// </summary>
/// <param name="capabilities">Shared by one connection's repositories, so they are read once; a repository on its own reads its own.</param>
public abstract class BaseRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null)
{
    private readonly int  _timeoutSec = target.CommandTimeoutSeconds;
    private readonly bool _useNoLock  = target.UseNoLock;
    private readonly ServerCapabilitiesCache _capabilities = capabilities ?? new();

    /// <summary>Makes the connections in place of SqlClient: a test's fake that records each command.</summary>
    internal Func<IDbConnection>? Connect { get; init; }

    protected IDbConnection CreateConnection() =>
        Connect?.Invoke() ?? new SqlConnection(target.ConnectionString);

    /// <summary>
    /// What every read starts with: READ UNCOMMITTED when <see cref="SqlTarget.UseNoLock"/> (no shared
    /// locks), and how long it may wait for a lock it still needs, such as one held by a schema
    /// change (#167). Set on every read, since a pooled connection keeps the last one's.
    /// </summary>
    internal string ReadPrefix =>
        (_useNoLock ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" : "") +
        $"SET LOCK_TIMEOUT {target.LockTimeoutMilliseconds};\n";

    // ── What the server is (#167) ─────────────────────────────────────

    /// <summary>The server's edition, version and the login's permissions, read once per connection.</summary>
    protected Task<ServerCapabilities> CapabilitiesAsync(CancellationToken cancellationToken) =>
        _capabilities.GetAsync(ReadCapabilitiesAsync, cancellationToken);

    private async Task<ServerCapabilities> ReadCapabilitiesAsync()
    {
        using var conn = CreateConnection();
        var server = await QFirst(conn, SqlScripts.Get("Server/Capabilities"), CancellationToken.None)
                     ?? throw new InvalidOperationException("The server didn't say what it is.");

        // A login that can't list its permissions still reads what it can: the queries say what's missing.
        IEnumerable<string>? permissions;
        try { permissions = (await Q(conn, SqlScripts.Get("Server/Permissions"), CancellationToken.None)).Select(r => (string)r.PermissionName).ToList(); }
        catch (WaitStatsException) { permissions = null; }

        return ServerCapabilities.From(Convert.ToInt32(server.Edition), (string?)server.ProductVersion, permissions);
    }

    // ── Version-dependent columns ─────────────────────────────────────
    // Newer versions add columns to catalog views and DMVs, and naming a missing one fails the
    // whole query with error 207. A column's presence is asked of sys.all_columns once per
    // connection: the major version can't always tell (some arrived in a service pack).

    /// <summary>1 when the view has the column, else 0. Both names are constants, never user input.</summary>
    internal static string HasColumnSql(string view, string column) =>
        SqlScripts.Get("Server/HasColumn").Replace("/*VIEW*/", view).Replace("/*COLUMN*/", column);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _columns = new();

    /// <summary>Whether the server's view has the column, asked once per repository instance (one per connection).</summary>
    protected async Task<bool> HasColumnAsync(IDbConnection conn, string view, string column, CancellationToken cancellationToken)
    {
        var key = view + "." + column;
        if (_columns.TryGetValue(key, out var known))
            return known;

        var row = await QFirst(conn, HasColumnSql(view, column), cancellationToken);
        return _columns[key] = row is not null && Convert.ToInt32(row.HasColumn) == 1;
    }

    // ── Running a query ───────────────────────────────────────────────
    // Every query takes the caller's token: leaving a page, switching server or stopping
    // monitoring cancels it on the server too (#167). A cancelled query throws
    // OperationCanceledException, never WaitStatsException, so nobody reports it as an error.

    protected async Task<IEnumerable<dynamic>> Q(
        IDbConnection conn,
        string sql,
        CancellationToken cancellationToken,
        object? param = null,
        int? commandTimeout = null,
        [CallerMemberName] string caller = "")
    {
        try
        {
            return await conn.QueryAsync(Command(ReadPrefix + sql, param, commandTimeout, cancellationToken));
        }
        catch (Exception ex) when (ex is not WaitStatsException)
        {
            throw Failed(ex, caller, cancellationToken);
        }
    }

    /// <summary>
    /// Executes a non-query statement. Unlike <see cref="Q"/> this does not prepend the
    /// isolation-level hint, which is invalid ahead of DDL such as CREATE EVENT SESSION.
    /// </summary>
    protected async Task<int> Exec(
        IDbConnection conn,
        string sql,
        CancellationToken cancellationToken,
        object? param = null,
        int? commandTimeout = null,
        [CallerMemberName] string caller = "")
    {
        try
        {
            return await conn.ExecuteAsync(Command(sql, param, commandTimeout, cancellationToken));
        }
        catch (Exception ex) when (ex is not WaitStatsException)
        {
            throw Failed(ex, caller, cancellationToken);
        }
    }

    protected async Task<dynamic?> QFirst(
        IDbConnection conn,
        string sql,
        CancellationToken cancellationToken,
        object? param = null,
        int? commandTimeout = null,
        [CallerMemberName] string caller = "")
    {
        try
        {
            return await conn.QueryFirstOrDefaultAsync(Command(ReadPrefix + sql, param, commandTimeout, cancellationToken));
        }
        catch (Exception ex) when (ex is not WaitStatsException)
        {
            throw Failed(ex, caller, cancellationToken);
        }
    }

    private CommandDefinition Command(string sql, object? param, int? commandTimeout, CancellationToken cancellationToken) =>
        new(sql, param, commandTimeout: commandTimeout ?? _timeoutSec, cancellationToken: cancellationToken);

    // Cancelled: SqlClient cancels the command on the server and throws, as a SqlException
    // ("Operation cancelled by user") or an OperationCanceledException. Either way it is one.
    // A permission failure forgets the capabilities, so a GRANT made since is seen next time.
    private Exception Failed(Exception ex, string caller, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new OperationCanceledException($"[{GetType().Name}.{caller}] was cancelled.", ex, cancellationToken);

        var failed = new WaitStatsException(GetType().Name, caller, ex, target.Login);
        if (failed.Failure.IsPermission)
            _capabilities.Forget();
        return failed;
    }
}
