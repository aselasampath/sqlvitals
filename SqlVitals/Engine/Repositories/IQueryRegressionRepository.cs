using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Engine.Repositories;

/// <summary>The server reads behind the Query Regressions page (see Engine/Regressions).</summary>
public interface IQueryRegressionRepository
{
    /// <summary>
    /// Per query, plan and period, the work Query Store recorded for the connection's database in
    /// both periods of <paramref name="windows"/>, on the server's clock. Only queries that ran in
    /// the recent period are returned. Not available (and nothing read) when Query Store is off.
    /// </summary>
    Task<QueryStoreRegressionStats> GetQueryStoreRegressionStatsAsync(RegressionWindows windows);

    /// <summary>Statement text of each Query Store query_id given, by id.</summary>
    Task<IReadOnlyDictionary<long, string>> GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds);

    /// <summary>A plan's showplan XML from Query Store; null when it has been removed.</summary>
    Task<string?> GetQueryStorePlanAsync(long planId);

    /// <summary>
    /// The showplan XML of the most recently used cached plan for a query_hash ("0x…"); null
    /// when none is in the plan cache any more.
    /// </summary>
    Task<string?> GetCachedPlanForQueryHashAsync(string queryHash);
}
