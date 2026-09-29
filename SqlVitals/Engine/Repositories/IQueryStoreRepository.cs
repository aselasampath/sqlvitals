using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// The server reads behind the Query Store page (#111): the connection's database only, on demand,
/// read only. Rows are grouped and ranked on the server, so only the top N come back. A tab whose
/// view or column this version lacks gets its rows empty with the reason, never an error.
/// </summary>
public interface IQueryStoreRepository
{
    /// <summary>Query Store's settings and the top 20 queries by CPU in the last 24 hours, for Export for AI.</summary>
    Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)> GetQueryStoreAsync();

    /// <summary>The database, Query Store's settings (null before SQL Server 2016) and what this version has.</summary>
    Task<QueryStoreStatus> GetQueryStoreStatusAsync();

    /// <summary>The top queries in the period by a metric and statistic, one row per query over all its plans.</summary>
    Task<QueryStoreRows<QueryStoreQueryRow>> GetQueryStoreTopQueriesAsync(
        QueryStorePeriod period, QueryStoreMetric metric, QueryStoreStatistic statistic, int top);

    /// <summary>The database's totals per time slot (see <see cref="QueryStoreTimeSlots"/>); slots with nothing recorded are left out.</summary>
    Task<QueryStoreRows<QueryStoreSlot>> GetQueryStoreOverTimeAsync(QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin);

    /// <summary>The queries whose executions differ most in a metric, by combined std dev, with at least <paramref name="minExecutions"/>.</summary>
    Task<QueryStoreRows<QueryStoreVariationRow>> GetQueryStoreHighVariationAsync(
        QueryStorePeriod period, QueryStoreMetric metric, int minExecutions, int top);

    /// <summary>Total wait time per wait category, largest first (SQL Server 2017+ and Azure SQL).</summary>
    Task<QueryStoreRows<QueryStoreWaitCategory>> GetQueryStoreWaitCategoriesAsync(QueryStorePeriod period);

    /// <summary>The queries that waited longest in one wait category.</summary>
    Task<QueryStoreRows<QueryStoreWaitQuery>> GetQueryStoreWaitQueriesAsync(QueryStorePeriod period, int category, int top);

    /// <summary>Every forced plan as it is now, with its executions in the period, and the Query Store hints where the version has them.</summary>
    Task<QueryStoreForcedPlans> GetQueryStoreForcedPlansAsync(QueryStorePeriod period);

    /// <summary>One query's text, its plans, and each plan's executions per interval in the period.</summary>
    Task<QueryStoreQueryDetails> GetQueryStoreQueryDetailsAsync(long queryId, QueryStorePeriod period);
}
