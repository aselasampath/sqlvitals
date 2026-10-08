using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Engine.Repositories;

public partial class QueryRegressionRepository(IConfiguration configuration) : BaseRepository(configuration), IQueryRegressionRepository
{
    // Reads every runtime-stats row of a week or more, which can take a while on a busy
    // database with a large Query Store.
    private const int ExtendedTimeoutSec = 120;

    public async Task<QueryStoreRegressionStats> GetQueryStoreRegressionStatsAsync(RegressionWindows windows)
    {
        var stateSql = SqlScripts.Get("QueryRegression/State");

        // Each Query Store interval counts in the period its start falls in, so none is counted
        // twice. The CTE's recent rows pick which queries are worth reading at all.
        var statsSql = SqlScripts.Get("QueryRegression/Stats");

        using var conn = CreateConnection();

        dynamic? state;
        try
        {
            state = await QFirst(conn, stateSql);
        }
        catch (WaitStatsException ex) when (ex.SqlErrorNumber == 208)
        {
            // SQL Server 2014 and earlier: no Query Store catalog views at all.
            return new QueryStoreRegressionStats(string.Empty, "NOT SUPPORTED", false, []);
        }

        var database    = state is null ? string.Empty : (string)state.DatabaseName;
        var actualState = state is null ? "OFF" : (string)state.ActualState;

        // READ_ONLY still holds everything recorded so far; OFF and ERROR can't be relied on.
        if (actualState is not ("READ_WRITE" or "READ_ONLY"))
            return new QueryStoreRegressionStats(database, actualState, false, []);

        var rows = await Q(conn, statsSql, new
        {
            recentMinutes        = (int)Math.Ceiling(windows.Recent.TotalMinutes),
            baselineStartMinutes = (int)Math.Ceiling(windows.BaselineStartAgo.TotalMinutes),
            baselineEndMinutes   = (int)Math.Ceiling(windows.BaselineEndAgo.TotalMinutes),
        }, ExtendedTimeoutSec);

        var stats = rows.Select(r => new QueryPeriodStats(
            Convert.ToInt64(r.QueryId).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(r.IsRecent) == 1,
            Convert.ToInt64(r.PlanId),
            Convert.ToInt64(r.Executions),
            Convert.ToDouble(r.DurationUs),
            Convert.ToDouble(r.CpuUs))).ToList();

        return new QueryStoreRegressionStats(database, actualState, true, stats);
    }

    public async Task<IReadOnlyDictionary<long, string>> GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds)
    {
        if (queryIds.Count == 0)
            return new Dictionary<long, string>();

        var sql = SqlScripts.Get("QueryRegression/QueryStoreTexts");

        using var conn = CreateConnection();
        return (await Q(conn, sql, new { queryIds }))
            .ToDictionary(r => (long)Convert.ToInt64(r.QueryId), r => r.QueryText is DBNull ? string.Empty : (string)r.QueryText);
    }

    public async Task<string?> GetQueryStorePlanAsync(long planId)
    {
        var sql = SqlScripts.Get("QueryRegression/QueryStorePlan");

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { planId });
        return r is null || r.QueryPlan is DBNull ? null : (string)r.QueryPlan;
    }

    public async Task<string?> GetCachedPlanForQueryHashAsync(string queryHash)
    {
        // Checked here so a malformed hash is "no plan" rather than a conversion error.
        if (!QueryHashPattern().IsMatch(queryHash))
            return null;

        var sql = SqlScripts.Get("QueryRegression/CachedPlanForQueryHash");

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { queryHash });
        return r is null || r.QueryPlan is DBNull ? null : (string)r.QueryPlan;
    }

    [GeneratedRegex("^0x[0-9A-Fa-f]{16}$")]
    private static partial Regex QueryHashPattern();
}
