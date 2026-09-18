using System.Data;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

public abstract class BaseRepository(IConfiguration configuration)
{
    private readonly int  _timeoutSec  = configuration.GetValue<int> ("QuerySettings:CommandTimeoutSeconds", 30);
    private readonly bool _useNoLock   = configuration.GetValue<bool>("QuerySettings:UseNoLock",             true);

    protected IDbConnection CreateConnection() =>
        new SqlConnection(configuration.GetConnectionString("SqlServer"));

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
