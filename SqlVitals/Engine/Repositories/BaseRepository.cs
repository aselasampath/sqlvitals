using System.Data;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// What every area's repository shares: a connection to its <see cref="SqlTarget"/>, the query
/// time limit and isolation level, and errors wrapped with the query's name.
/// </summary>
public abstract class BaseRepository(SqlTarget target)
{
    private readonly int  _timeoutSec = target.CommandTimeoutSeconds;
    private readonly bool _useNoLock  = target.UseNoLock;

    protected IDbConnection CreateConnection() =>
        new SqlConnection(target.ConnectionString);

    // ── Version-dependent columns ─────────────────────────────────────
    // Newer versions add columns to catalog views and DMVs, and naming a missing one fails the
    // whole query with error 207. A column's presence is asked of sys.all_columns once per
    // connection: the major version can't always tell (some arrived in a service pack).

    /// <summary>1 when the view has the column, else 0. Both names are constants, never user input.</summary>
    internal static string HasColumnSql(string view, string column) =>
        SqlScripts.Get("Server/HasColumn").Replace("/*VIEW*/", view).Replace("/*COLUMN*/", column);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _columns = new();

    /// <summary>Whether the server's view has the column, asked once per repository instance (one per connection).</summary>
    protected async Task<bool> HasColumnAsync(IDbConnection conn, string view, string column)
    {
        var key = view + "." + column;
        if (_columns.TryGetValue(key, out var known))
            return known;

        var row = await QFirst(conn, HasColumnSql(view, column));
        return _columns[key] = row is not null && Convert.ToInt32(row.HasColumn) == 1;
    }

    protected async Task<IEnumerable<dynamic>> Q(
        IDbConnection conn,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        [CallerMemberName] string caller = "")
    {
        var effectiveSql = _useNoLock
            ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" + sql
            : sql;
        try
        {
            return await conn.QueryAsync(effectiveSql, param, commandTimeout: commandTimeout ?? _timeoutSec);
        }
        catch (Exception ex) when (ex is not WaitStatsException)
        {
            throw new WaitStatsException(GetType().Name, caller, ex);
        }
    }

    /// <summary>
    /// Executes a non-query statement. Unlike <see cref="Q"/> this does not prepend the
    /// isolation-level hint, which is invalid ahead of DDL such as CREATE EVENT SESSION.
    /// </summary>
    protected async Task<int> Exec(
        IDbConnection conn,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        [CallerMemberName] string caller = "")
    {
        try
        {
            return await conn.ExecuteAsync(sql, param, commandTimeout: commandTimeout ?? _timeoutSec);
        }
        catch (Exception ex) when (ex is not WaitStatsException)
        {
            throw new WaitStatsException(GetType().Name, caller, ex);
        }
    }

    protected async Task<dynamic?> QFirst(
        IDbConnection conn,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        [CallerMemberName] string caller = "")
    {
        var effectiveSql = _useNoLock
            ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" + sql
            : sql;
        try
        {
            return await conn.QueryFirstOrDefaultAsync(effectiveSql, param, commandTimeout: commandTimeout ?? _timeoutSec);
        }
        catch (Exception ex) when (ex is not WaitStatsException)
        {
            throw new WaitStatsException(GetType().Name, caller, ex);
        }
    }
}
