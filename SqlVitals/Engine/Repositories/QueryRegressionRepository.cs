using System.Text.RegularExpressions;
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
        const string stateSql = """
            SELECT DB_NAME() AS DatabaseName, actual_state_desc AS ActualState
            FROM sys.database_query_store_options
            """;

        // Each Query Store interval counts in the period its start falls in, so none is counted
        // twice. The CTE's recent rows pick which queries are worth reading at all.
        const string statsSql = """
            DECLARE @now          datetimeoffset = SYSDATETIMEOFFSET();
            DECLARE @recentFrom   datetimeoffset = DATEADD(MINUTE, -@recentMinutes,        @now);
            DECLARE @baselineFrom datetimeoffset = DATEADD(MINUTE, -@baselineStartMinutes, @now);
            DECLARE @baselineTo   datetimeoffset = DATEADD(MINUTE, -@baselineEndMinutes,   @now);

            ;WITH s AS (
                SELECT p.query_id, rs.plan_id,
                       CASE WHEN i.start_time >= @recentFrom THEN 1 ELSE 0 END AS is_recent,
                       rs.count_executions, rs.avg_duration, rs.avg_cpu_time
                FROM sys.query_store_runtime_stats_interval i
                JOIN sys.query_store_runtime_stats rs ON rs.runtime_stats_interval_id = i.runtime_stats_interval_id
                JOIN sys.query_store_plan p           ON p.plan_id = rs.plan_id
                JOIN sys.query_store_query q          ON q.query_id = p.query_id
                WHERE q.is_internal_query = 0
                  AND (i.start_time >= @recentFrom
                       OR (i.start_time >= @baselineFrom AND i.start_time < @baselineTo))
            )
            SELECT query_id AS QueryId, plan_id AS PlanId, is_recent AS IsRecent,
                   SUM(count_executions)                AS Executions,
                   SUM(avg_duration * count_executions) AS DurationUs,
                   SUM(avg_cpu_time * count_executions) AS CpuUs
            FROM s
            WHERE query_id IN (SELECT query_id FROM s WHERE is_recent = 1)
            GROUP BY query_id, plan_id, is_recent
            """;

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

        const string sql = """
            SELECT q.query_id AS QueryId, qt.query_sql_text AS QueryText
            FROM sys.query_store_query q
            JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
            WHERE q.query_id IN @queryIds
            """;

        using var conn = CreateConnection();
        return (await Q(conn, sql, new { queryIds }))
            .ToDictionary(r => (long)Convert.ToInt64(r.QueryId), r => r.QueryText is DBNull ? string.Empty : (string)r.QueryText);
    }

    public async Task<string?> GetQueryStorePlanAsync(long planId)
    {
        const string sql = """
            SELECT query_plan AS QueryPlan FROM sys.query_store_plan WHERE plan_id = @planId
            """;

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { planId });
        return r is null || r.QueryPlan is DBNull ? null : (string)r.QueryPlan;
    }

    public async Task<string?> GetCachedPlanForQueryHashAsync(string queryHash)
    {
        // Checked here so a malformed hash is "no plan" rather than a conversion error.
        if (!QueryHashPattern().IsMatch(queryHash))
            return null;

        const string sql = """
            SELECT TOP 1 CAST(qp.query_plan AS NVARCHAR(MAX)) AS QueryPlan
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) qp
            WHERE qs.query_hash = CONVERT(binary(8), @queryHash, 1) AND qp.query_plan IS NOT NULL
            ORDER BY qs.last_execution_time DESC
            """;

        using var conn = CreateConnection();
        var r = await QFirst(conn, sql, new { queryHash });
        return r is null || r.QueryPlan is DBNull ? null : (string)r.QueryPlan;
    }

    [GeneratedRegex("^0x[0-9A-Fa-f]{16}$")]
    private static partial Regex QueryHashPattern();
}
