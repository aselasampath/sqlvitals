using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;


public interface IWaitStatsRepository : ITempDbRepository, IPlanCacheHealthRepository, ISpTraceRepository, IHistoryRepository
{
    // ── Engine edition detection ──────────────────────────────────────
    Task<bool> IsAzureSqlDatabaseAsync();
    Task<bool> SupportsOnlineIndexRebuildAsync();

    Task<IEnumerable<WaitStatCumulative>>  GetCumulativeWaitsAsync();

    Task<IEnumerable<ActiveWait>>          GetActiveWaitsAsync();
    Task<SignalVsResourceWait?>            GetSignalVsResourceAsync();
    Task<IEnumerable<TopWaitType>>         GetTopWaitTypesAsync();

    // ── New counters ────────────────────────────────────────────────
    Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)>   GetMemoryGrantsAsync();

    Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)>                                     GetQueryStoreAsync();
    Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)>                                       GetIndexHealthAsync();
    Task<(IEnumerable<PlanCacheTypeSummary> Summary, IEnumerable<PlanCacheCounter> Counters, IEnumerable<PlanCacheTopQuery> TopQueries)> GetPlanCacheAsync();

    // ── Resource-intensive queries ───────────────────────────────────
    Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
          IEnumerable<ResourceIntensiveQuery> ByCpu,
          IEnumerable<ResourceIntensiveQuery> ByHighestReads)> GetResourceIntensiveQueriesAsync(int topN = 20);
    Task<string?> GetObjectQueryPlanAsync(string objectName);
    Task<IEnumerable<IndexUsagePattern>>     GetIndexUsagePatternsAsync();
    Task<IEnumerable<IndexFragmentation>>    GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000);
    Task<IEnumerable<ImplicitConversion>>    GetImplicitConversionsAsync();
    Task<IEnumerable<StaleStatistic>>        GetStaleStatisticsAsync();

    // ── Database storage & configuration ───────────────────────────
    Task<(
        IEnumerable<ServerConfigParam>  Config,
        IEnumerable<DatabaseFile>       Files,
        IEnumerable<TempDbFileSummary>  TempDbFiles,
        IEnumerable<TableStorage>       TopTables
    )> GetDatabaseStorageAsync();

    // ── Process map ─────────────────────────────────────────────────
    Task<IEnumerable<ProcessNode>> GetProcessesAsync();

    // ── Live metrics dashboard ───────────────────────────────────────
    Task<LiveMetricSnapshot> GetLiveMetricsAsync();

    // ── Application connections ──────────────────────────────────────
    Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync();

    // ── Perfmon counters (live DMV snapshot) ─────────────────────────
    Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync();

    // ── Memory KPI snapshot (for trend chart + KPI strip) ────────────
    Task<MemorySnapshot> GetMemorySnapshotAsync();
}
