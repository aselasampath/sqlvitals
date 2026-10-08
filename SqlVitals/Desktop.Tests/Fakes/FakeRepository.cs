using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;
using SqlVitals.Engine.Security;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>
/// An <see cref="IWaitStatsRepository"/> that answers each call with the function set for it
/// (#168): <c>new FakeRepository { GetProcessesAsync = () => Task.FromResult(…) }</c>. Each
/// function has its method's parameters and return type, so a renamed method or a changed
/// signature is a compile error here, not a fake that quietly stops answering.
/// </summary>
internal sealed class FakeRepository : IWaitStatsRepository
{
    /// <summary>
    /// What a call without a function does: false throws <see cref="NotSupportedException"/>
    /// (the test didn't expect it); true fails its task, as with a server that can't be reached.
    /// </summary>
    public bool Unreachable { get; init; }

    /// <summary>Told each method's name as it's called, answered or not.</summary>
    public Action<string>? Called { get; init; }

    private T Answered<T>(string method, T answer)
    {
        Called?.Invoke(method);
        return answer;
    }

    private T Unanswered<T>(string method) where T : Task
    {
        Called?.Invoke(method);
        if (!Unreachable)
            throw new NotSupportedException($"{method} isn't faked in this test.");
        // T is Task<TResult>: Task.FromException<TResult>.
        return (T)typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                              .MakeGenericMethod(typeof(T).GetGenericArguments()[0])
                              .Invoke(null, [new InvalidOperationException("No server in tests.")])!;
    }

    public Func<Task<AgentJobList>>? GetAgentJobsAsync { get; set; }
    Task<AgentJobList> IAgentJobRepository.GetAgentJobsAsync() =>
        GetAgentJobsAsync is { } answer ? Answered(nameof(GetAgentJobsAsync), answer()) : Unanswered<Task<AgentJobList>>(nameof(GetAgentJobsAsync));

    public Func<Guid, DateTime?, Task<IReadOnlyList<JobRun>>>? GetAgentJobRunsAsync { get; set; }
    Task<IReadOnlyList<JobRun>> IAgentJobRepository.GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince) =>
        GetAgentJobRunsAsync is { } answer ? Answered(nameof(GetAgentJobRunsAsync), answer(jobId, runningSince)) : Unanswered<Task<IReadOnlyList<JobRun>>>(nameof(GetAgentJobRunsAsync));

    public Func<BackupRpo, Task<BackupList>>? GetBackupStatusAsync { get; set; }
    Task<BackupList> IBackupRepository.GetBackupStatusAsync(BackupRpo rpo) =>
        GetBackupStatusAsync is { } answer ? Answered(nameof(GetBackupStatusAsync), answer(rpo)) : Unanswered<Task<BackupList>>(nameof(GetBackupStatusAsync));

    public Func<string, DateTime, Task<IReadOnlyList<BackupHistoryEntry>>>? GetBackupHistoryAsync { get; set; }
    Task<IReadOnlyList<BackupHistoryEntry>> IBackupRepository.GetBackupHistoryAsync(string databaseName, DateTime createDate) =>
        GetBackupHistoryAsync is { } answer ? Answered(nameof(GetBackupHistoryAsync), answer(databaseName, createDate)) : Unanswered<Task<IReadOnlyList<BackupHistoryEntry>>>(nameof(GetBackupHistoryAsync));

    public Func<Task<ConfigCheckList>>? GetConfigurationChecksAsync { get; set; }
    Task<ConfigCheckList> IConfigurationRepository.GetConfigurationChecksAsync() =>
        GetConfigurationChecksAsync is { } answer ? Answered(nameof(GetConfigurationChecksAsync), answer()) : Unanswered<Task<ConfigCheckList>>(nameof(GetConfigurationChecksAsync));

    public Func<Task<DeadlockHistory>>? GetDeadlockHistoryAsync { get; set; }
    Task<DeadlockHistory> IDeadlockRepository.GetDeadlockHistoryAsync() =>
        GetDeadlockHistoryAsync is { } answer ? Answered(nameof(GetDeadlockHistoryAsync), answer()) : Unanswered<Task<DeadlockHistory>>(nameof(GetDeadlockHistoryAsync));

    public Func<Task<FileIoReading>>? ReadFileIoAsync { get; set; }
    Task<FileIoReading> IFileIoRepository.ReadFileIoAsync() =>
        ReadFileIoAsync is { } answer ? Answered(nameof(ReadFileIoAsync), answer()) : Unanswered<Task<FileIoReading>>(nameof(ReadFileIoAsync));

    public Func<DateTime?, int, Task<HistoryDetailSnapshot>>? GetHistoryDetailAsync { get; set; }
    Task<HistoryDetailSnapshot> IHistoryRepository.GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries) =>
        GetHistoryDetailAsync is { } answer ? Answered(nameof(GetHistoryDetailAsync), answer(queriesExecutedSince, maxQueries)) : Unanswered<Task<HistoryDetailSnapshot>>(nameof(GetHistoryDetailAsync));

    public Func<IReadOnlyCollection<string>, int, Task<IReadOnlyList<QueryTextInfo>>>? GetQueryTextsAsync { get; set; }
    Task<IReadOnlyList<QueryTextInfo>> IHistoryRepository.GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength) =>
        GetQueryTextsAsync is { } answer ? Answered(nameof(GetQueryTextsAsync), answer(queryHashes, maxLength)) : Unanswered<Task<IReadOnlyList<QueryTextInfo>>>(nameof(GetQueryTextsAsync));

    public Func<Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)>>? GetIndexHealthAsync { get; set; }
    Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> IIndexRepository.GetIndexHealthAsync() =>
        GetIndexHealthAsync is { } answer ? Answered(nameof(GetIndexHealthAsync), answer()) : Unanswered<Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)>>(nameof(GetIndexHealthAsync));

    public Func<Task<IEnumerable<IndexUsagePattern>>>? GetIndexUsagePatternsAsync { get; set; }
    Task<IEnumerable<IndexUsagePattern>> IIndexRepository.GetIndexUsagePatternsAsync() =>
        GetIndexUsagePatternsAsync is { } answer ? Answered(nameof(GetIndexUsagePatternsAsync), answer()) : Unanswered<Task<IEnumerable<IndexUsagePattern>>>(nameof(GetIndexUsagePatternsAsync));

    public Func<double, long, Task<IEnumerable<IndexFragmentation>>>? GetIndexFragmentationAsync { get; set; }
    Task<IEnumerable<IndexFragmentation>> IIndexRepository.GetIndexFragmentationAsync(double minFragmentationPercent, long minPageCount) =>
        GetIndexFragmentationAsync is { } answer ? Answered(nameof(GetIndexFragmentationAsync), answer(minFragmentationPercent, minPageCount)) : Unanswered<Task<IEnumerable<IndexFragmentation>>>(nameof(GetIndexFragmentationAsync));

    public Func<Task<IEnumerable<StaleStatistic>>>? GetStaleStatisticsAsync { get; set; }
    Task<IEnumerable<StaleStatistic>> IIndexRepository.GetStaleStatisticsAsync() =>
        GetStaleStatisticsAsync is { } answer ? Answered(nameof(GetStaleStatisticsAsync), answer()) : Unanswered<Task<IEnumerable<StaleStatistic>>>(nameof(GetStaleStatisticsAsync));

    public Func<Task<LiveMetricSnapshot>>? GetLiveMetricsAsync { get; set; }
    Task<LiveMetricSnapshot> ILiveMetricsRepository.GetLiveMetricsAsync() =>
        GetLiveMetricsAsync is { } answer ? Answered(nameof(GetLiveMetricsAsync), answer()) : Unanswered<Task<LiveMetricSnapshot>>(nameof(GetLiveMetricsAsync));

    public Func<Task<IEnumerable<PerfmonCounter>>>? GetPerfmonCountersAsync { get; set; }
    Task<IEnumerable<PerfmonCounter>> ILiveMetricsRepository.GetPerfmonCountersAsync() =>
        GetPerfmonCountersAsync is { } answer ? Answered(nameof(GetPerfmonCountersAsync), answer()) : Unanswered<Task<IEnumerable<PerfmonCounter>>>(nameof(GetPerfmonCountersAsync));

    public Func<Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)>>? GetMemoryGrantsAsync { get; set; }
    Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)> IMemoryRepository.GetMemoryGrantsAsync() =>
        GetMemoryGrantsAsync is { } answer ? Answered(nameof(GetMemoryGrantsAsync), answer()) : Unanswered<Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)>>(nameof(GetMemoryGrantsAsync));

    public Func<Task<MemorySnapshot>>? GetMemorySnapshotAsync { get; set; }
    Task<MemorySnapshot> IMemoryRepository.GetMemorySnapshotAsync() =>
        GetMemorySnapshotAsync is { } answer ? Answered(nameof(GetMemorySnapshotAsync), answer()) : Unanswered<Task<MemorySnapshot>>(nameof(GetMemorySnapshotAsync));

    public Func<Task<( PlanCacheHealthSummary Summary, IEnumerable<MultiPlanQuery> MultiPlanQueries, IEnumerable<KeyLookupQuery> KeyLookupQueries, IEnumerable<CardinalityMisestimate> CardinalityMisestimates, IEnumerable<SkewedParallelQuery> SkewedParallelQueries, IEnumerable<PlanGuide> PlanGuides )>>? GetPlanCacheHealthAsync { get; set; }
    Task<( PlanCacheHealthSummary Summary, IEnumerable<MultiPlanQuery> MultiPlanQueries, IEnumerable<KeyLookupQuery> KeyLookupQueries, IEnumerable<CardinalityMisestimate> CardinalityMisestimates, IEnumerable<SkewedParallelQuery> SkewedParallelQueries, IEnumerable<PlanGuide> PlanGuides )> IPlanCacheHealthRepository.GetPlanCacheHealthAsync() =>
        GetPlanCacheHealthAsync is { } answer ? Answered(nameof(GetPlanCacheHealthAsync), answer()) : Unanswered<Task<( PlanCacheHealthSummary Summary, IEnumerable<MultiPlanQuery> MultiPlanQueries, IEnumerable<KeyLookupQuery> KeyLookupQueries, IEnumerable<CardinalityMisestimate> CardinalityMisestimates, IEnumerable<SkewedParallelQuery> SkewedParallelQueries, IEnumerable<PlanGuide> PlanGuides )>>(nameof(GetPlanCacheHealthAsync));

    public Func<RegressionWindows, Task<QueryStoreRegressionStats>>? GetQueryStoreRegressionStatsAsync { get; set; }
    Task<QueryStoreRegressionStats> IQueryRegressionRepository.GetQueryStoreRegressionStatsAsync(RegressionWindows windows) =>
        GetQueryStoreRegressionStatsAsync is { } answer ? Answered(nameof(GetQueryStoreRegressionStatsAsync), answer(windows)) : Unanswered<Task<QueryStoreRegressionStats>>(nameof(GetQueryStoreRegressionStatsAsync));

    public Func<IReadOnlyCollection<long>, Task<IReadOnlyDictionary<long, string>>>? GetQueryStoreTextsAsync { get; set; }
    Task<IReadOnlyDictionary<long, string>> IQueryRegressionRepository.GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds) =>
        GetQueryStoreTextsAsync is { } answer ? Answered(nameof(GetQueryStoreTextsAsync), answer(queryIds)) : Unanswered<Task<IReadOnlyDictionary<long, string>>>(nameof(GetQueryStoreTextsAsync));

    public Func<long, Task<string?>>? GetQueryStorePlanAsync { get; set; }
    Task<string?> IQueryRegressionRepository.GetQueryStorePlanAsync(long planId) =>
        GetQueryStorePlanAsync is { } answer ? Answered(nameof(GetQueryStorePlanAsync), answer(planId)) : Unanswered<Task<string?>>(nameof(GetQueryStorePlanAsync));

    public Func<string, Task<string?>>? GetCachedPlanForQueryHashAsync { get; set; }
    Task<string?> IQueryRegressionRepository.GetCachedPlanForQueryHashAsync(string queryHash) =>
        GetCachedPlanForQueryHashAsync is { } answer ? Answered(nameof(GetCachedPlanForQueryHashAsync), answer(queryHash)) : Unanswered<Task<string?>>(nameof(GetCachedPlanForQueryHashAsync));

    public Func<Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)>>? GetQueryStoreAsync { get; set; }
    Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)> IQueryStoreRepository.GetQueryStoreAsync() =>
        GetQueryStoreAsync is { } answer ? Answered(nameof(GetQueryStoreAsync), answer()) : Unanswered<Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)>>(nameof(GetQueryStoreAsync));

    public Func<Task<QueryStoreStatus>>? GetQueryStoreStatusAsync { get; set; }
    Task<QueryStoreStatus> IQueryStoreRepository.GetQueryStoreStatusAsync() =>
        GetQueryStoreStatusAsync is { } answer ? Answered(nameof(GetQueryStoreStatusAsync), answer()) : Unanswered<Task<QueryStoreStatus>>(nameof(GetQueryStoreStatusAsync));

    public Func<QueryStorePeriod, QueryStoreMetric, QueryStoreStatistic, int, Task<QueryStoreRows<QueryStoreQueryRow>>>? GetQueryStoreTopQueriesAsync { get; set; }
    Task<QueryStoreRows<QueryStoreQueryRow>> IQueryStoreRepository.GetQueryStoreTopQueriesAsync(QueryStorePeriod period, QueryStoreMetric metric, QueryStoreStatistic statistic, int top) =>
        GetQueryStoreTopQueriesAsync is { } answer ? Answered(nameof(GetQueryStoreTopQueriesAsync), answer(period, metric, statistic, top)) : Unanswered<Task<QueryStoreRows<QueryStoreQueryRow>>>(nameof(GetQueryStoreTopQueriesAsync));

    public Func<QueryStorePeriod, TimeSpan, DateTimeOffset, Task<QueryStoreRows<QueryStoreSlot>>>? GetQueryStoreOverTimeAsync { get; set; }
    Task<QueryStoreRows<QueryStoreSlot>> IQueryStoreRepository.GetQueryStoreOverTimeAsync(QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin) =>
        GetQueryStoreOverTimeAsync is { } answer ? Answered(nameof(GetQueryStoreOverTimeAsync), answer(period, slotLength, origin)) : Unanswered<Task<QueryStoreRows<QueryStoreSlot>>>(nameof(GetQueryStoreOverTimeAsync));

    public Func<QueryStorePeriod, QueryStoreMetric, int, int, Task<QueryStoreRows<QueryStoreVariationRow>>>? GetQueryStoreHighVariationAsync { get; set; }
    Task<QueryStoreRows<QueryStoreVariationRow>> IQueryStoreRepository.GetQueryStoreHighVariationAsync(QueryStorePeriod period, QueryStoreMetric metric, int minExecutions, int top) =>
        GetQueryStoreHighVariationAsync is { } answer ? Answered(nameof(GetQueryStoreHighVariationAsync), answer(period, metric, minExecutions, top)) : Unanswered<Task<QueryStoreRows<QueryStoreVariationRow>>>(nameof(GetQueryStoreHighVariationAsync));

    public Func<QueryStorePeriod, Task<QueryStoreRows<QueryStoreWaitCategory>>>? GetQueryStoreWaitCategoriesAsync { get; set; }
    Task<QueryStoreRows<QueryStoreWaitCategory>> IQueryStoreRepository.GetQueryStoreWaitCategoriesAsync(QueryStorePeriod period) =>
        GetQueryStoreWaitCategoriesAsync is { } answer ? Answered(nameof(GetQueryStoreWaitCategoriesAsync), answer(period)) : Unanswered<Task<QueryStoreRows<QueryStoreWaitCategory>>>(nameof(GetQueryStoreWaitCategoriesAsync));

    public Func<QueryStorePeriod, int, int, Task<QueryStoreRows<QueryStoreWaitQuery>>>? GetQueryStoreWaitQueriesAsync { get; set; }
    Task<QueryStoreRows<QueryStoreWaitQuery>> IQueryStoreRepository.GetQueryStoreWaitQueriesAsync(QueryStorePeriod period, int category, int top) =>
        GetQueryStoreWaitQueriesAsync is { } answer ? Answered(nameof(GetQueryStoreWaitQueriesAsync), answer(period, category, top)) : Unanswered<Task<QueryStoreRows<QueryStoreWaitQuery>>>(nameof(GetQueryStoreWaitQueriesAsync));

    public Func<QueryStorePeriod, Task<QueryStoreForcedPlans>>? GetQueryStoreForcedPlansAsync { get; set; }
    Task<QueryStoreForcedPlans> IQueryStoreRepository.GetQueryStoreForcedPlansAsync(QueryStorePeriod period) =>
        GetQueryStoreForcedPlansAsync is { } answer ? Answered(nameof(GetQueryStoreForcedPlansAsync), answer(period)) : Unanswered<Task<QueryStoreForcedPlans>>(nameof(GetQueryStoreForcedPlansAsync));

    public Func<long, QueryStorePeriod, Task<QueryStoreQueryDetails>>? GetQueryStoreQueryDetailsAsync { get; set; }
    Task<QueryStoreQueryDetails> IQueryStoreRepository.GetQueryStoreQueryDetailsAsync(long queryId, QueryStorePeriod period) =>
        GetQueryStoreQueryDetailsAsync is { } answer ? Answered(nameof(GetQueryStoreQueryDetailsAsync), answer(queryId, period)) : Unanswered<Task<QueryStoreQueryDetails>>(nameof(GetQueryStoreQueryDetailsAsync));

    public Func<int, Task<(IEnumerable<ResourceIntensiveQuery> ByReads, IEnumerable<ResourceIntensiveQuery> ByCpu, IEnumerable<ResourceIntensiveQuery> ByHighestReads)>>? GetResourceIntensiveQueriesAsync { get; set; }
    Task<(IEnumerable<ResourceIntensiveQuery> ByReads, IEnumerable<ResourceIntensiveQuery> ByCpu, IEnumerable<ResourceIntensiveQuery> ByHighestReads)> IResourceQueryRepository.GetResourceIntensiveQueriesAsync(int topN) =>
        GetResourceIntensiveQueriesAsync is { } answer ? Answered(nameof(GetResourceIntensiveQueriesAsync), answer(topN)) : Unanswered<Task<(IEnumerable<ResourceIntensiveQuery> ByReads, IEnumerable<ResourceIntensiveQuery> ByCpu, IEnumerable<ResourceIntensiveQuery> ByHighestReads)>>(nameof(GetResourceIntensiveQueriesAsync));

    public Func<string, Task<string?>>? GetObjectQueryPlanAsync { get; set; }
    Task<string?> IResourceQueryRepository.GetObjectQueryPlanAsync(string objectName) =>
        GetObjectQueryPlanAsync is { } answer ? Answered(nameof(GetObjectQueryPlanAsync), answer(objectName)) : Unanswered<Task<string?>>(nameof(GetObjectQueryPlanAsync));

    public Func<Task<IEnumerable<ImplicitConversion>>>? GetImplicitConversionsAsync { get; set; }
    Task<IEnumerable<ImplicitConversion>> IResourceQueryRepository.GetImplicitConversionsAsync() =>
        GetImplicitConversionsAsync is { } answer ? Answered(nameof(GetImplicitConversionsAsync), answer()) : Unanswered<Task<IEnumerable<ImplicitConversion>>>(nameof(GetImplicitConversionsAsync));

    public Func<Task<SecurityFindingList>>? GetSecurityAuditAsync { get; set; }
    Task<SecurityFindingList> ISecurityAuditRepository.GetSecurityAuditAsync() =>
        GetSecurityAuditAsync is { } answer ? Answered(nameof(GetSecurityAuditAsync), answer()) : Unanswered<Task<SecurityFindingList>>(nameof(GetSecurityAuditAsync));

    public Func<Task<bool>>? IsAzureSqlDatabaseAsync { get; set; }
    Task<bool> IServerRepository.IsAzureSqlDatabaseAsync() =>
        IsAzureSqlDatabaseAsync is { } answer ? Answered(nameof(IsAzureSqlDatabaseAsync), answer()) : Unanswered<Task<bool>>(nameof(IsAzureSqlDatabaseAsync));

    public Func<Task<bool>>? SupportsOnlineIndexRebuildAsync { get; set; }
    Task<bool> IServerRepository.SupportsOnlineIndexRebuildAsync() =>
        SupportsOnlineIndexRebuildAsync is { } answer ? Answered(nameof(SupportsOnlineIndexRebuildAsync), answer()) : Unanswered<Task<bool>>(nameof(SupportsOnlineIndexRebuildAsync));

    public Func<Task<IEnumerable<ProcessNode>>>? GetProcessesAsync { get; set; }
    Task<IEnumerable<ProcessNode>> ISessionRepository.GetProcessesAsync() =>
        GetProcessesAsync is { } answer ? Answered(nameof(GetProcessesAsync), answer()) : Unanswered<Task<IEnumerable<ProcessNode>>>(nameof(GetProcessesAsync));

    public Func<int, Task<SessionPlan>>? GetSessionPlanAsync { get; set; }
    Task<SessionPlan> ISessionRepository.GetSessionPlanAsync(int sessionId) =>
        GetSessionPlanAsync is { } answer ? Answered(nameof(GetSessionPlanAsync), answer(sessionId)) : Unanswered<Task<SessionPlan>>(nameof(GetSessionPlanAsync));

    public Func<Task<IEnumerable<ApplicationConnection>>>? GetApplicationConnectionsAsync { get; set; }
    Task<IEnumerable<ApplicationConnection>> ISessionRepository.GetApplicationConnectionsAsync() =>
        GetApplicationConnectionsAsync is { } answer ? Answered(nameof(GetApplicationConnectionsAsync), answer()) : Unanswered<Task<IEnumerable<ApplicationConnection>>>(nameof(GetApplicationConnectionsAsync));

    public Func<SpTraceOptions, Task<TraceSessionStatus>>? StartTraceAsync { get; set; }
    Task<TraceSessionStatus> ISpTraceRepository.StartTraceAsync(SpTraceOptions options) =>
        StartTraceAsync is { } answer ? Answered(nameof(StartTraceAsync), answer(options)) : Unanswered<Task<TraceSessionStatus>>(nameof(StartTraceAsync));

    public Func<Task<TraceSessionStatus>>? StopTraceAsync { get; set; }
    Task<TraceSessionStatus> ISpTraceRepository.StopTraceAsync() =>
        StopTraceAsync is { } answer ? Answered(nameof(StopTraceAsync), answer()) : Unanswered<Task<TraceSessionStatus>>(nameof(StopTraceAsync));

    public Func<Task<TraceSessionStatus>>? GetTraceStatusAsync { get; set; }
    Task<TraceSessionStatus> ISpTraceRepository.GetTraceStatusAsync() =>
        GetTraceStatusAsync is { } answer ? Answered(nameof(GetTraceStatusAsync), answer()) : Unanswered<Task<TraceSessionStatus>>(nameof(GetTraceStatusAsync));

    public Func<Task<IReadOnlyList<SpTraceEvent>>>? PollTraceEventsAsync { get; set; }
    Task<IReadOnlyList<SpTraceEvent>> ISpTraceRepository.PollTraceEventsAsync() =>
        PollTraceEventsAsync is { } answer ? Answered(nameof(PollTraceEventsAsync), answer()) : Unanswered<Task<IReadOnlyList<SpTraceEvent>>>(nameof(PollTraceEventsAsync));

    public Func<Task<IReadOnlyList<SpAggregateRow>>>? PollProcedureStatsAsync { get; set; }
    Task<IReadOnlyList<SpAggregateRow>> ISpTraceRepository.PollProcedureStatsAsync() =>
        PollProcedureStatsAsync is { } answer ? Answered(nameof(PollProcedureStatsAsync), answer()) : Unanswered<Task<IReadOnlyList<SpAggregateRow>>>(nameof(PollProcedureStatsAsync));

    public Func<int, Task<IReadOnlyList<SpAggregateRow>>>? GetProcedureStatsTotalsAsync { get; set; }
    Task<IReadOnlyList<SpAggregateRow>> ISpTraceRepository.GetProcedureStatsTotalsAsync(int topN) =>
        GetProcedureStatsTotalsAsync is { } answer ? Answered(nameof(GetProcedureStatsTotalsAsync), answer(topN)) : Unanswered<Task<IReadOnlyList<SpAggregateRow>>>(nameof(GetProcedureStatsTotalsAsync));

    public Func<Task<( IEnumerable<ServerConfigParam> Config, IEnumerable<DatabaseFile> Files, IEnumerable<TempDbFileSummary> TempDbFiles, IEnumerable<TableStorage> TopTables )>>? GetDatabaseStorageAsync { get; set; }
    Task<( IEnumerable<ServerConfigParam> Config, IEnumerable<DatabaseFile> Files, IEnumerable<TempDbFileSummary> TempDbFiles, IEnumerable<TableStorage> TopTables )> IStorageRepository.GetDatabaseStorageAsync() =>
        GetDatabaseStorageAsync is { } answer ? Answered(nameof(GetDatabaseStorageAsync), answer()) : Unanswered<Task<( IEnumerable<ServerConfigParam> Config, IEnumerable<DatabaseFile> Files, IEnumerable<TempDbFileSummary> TempDbFiles, IEnumerable<TableStorage> TopTables )>>(nameof(GetDatabaseStorageAsync));

    public Func<Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)>>? GetTempDbPressureAsync { get; set; }
    Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> ITempDbRepository.GetTempDbPressureAsync() =>
        GetTempDbPressureAsync is { } answer ? Answered(nameof(GetTempDbPressureAsync), answer()) : Unanswered<Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)>>(nameof(GetTempDbPressureAsync));

    public Func<Task<IEnumerable<WaitStatCumulative>>>? GetCumulativeWaitsAsync { get; set; }
    Task<IEnumerable<WaitStatCumulative>> IWaitsRepository.GetCumulativeWaitsAsync() =>
        GetCumulativeWaitsAsync is { } answer ? Answered(nameof(GetCumulativeWaitsAsync), answer()) : Unanswered<Task<IEnumerable<WaitStatCumulative>>>(nameof(GetCumulativeWaitsAsync));

    public Func<Task<IEnumerable<ActiveWait>>>? GetActiveWaitsAsync { get; set; }
    Task<IEnumerable<ActiveWait>> IWaitsRepository.GetActiveWaitsAsync() =>
        GetActiveWaitsAsync is { } answer ? Answered(nameof(GetActiveWaitsAsync), answer()) : Unanswered<Task<IEnumerable<ActiveWait>>>(nameof(GetActiveWaitsAsync));

    public Func<Task<SignalVsResourceWait?>>? GetSignalVsResourceAsync { get; set; }
    Task<SignalVsResourceWait?> IWaitsRepository.GetSignalVsResourceAsync() =>
        GetSignalVsResourceAsync is { } answer ? Answered(nameof(GetSignalVsResourceAsync), answer()) : Unanswered<Task<SignalVsResourceWait?>>(nameof(GetSignalVsResourceAsync));

    public Func<Task<IEnumerable<TopWaitType>>>? GetTopWaitTypesAsync { get; set; }
    Task<IEnumerable<TopWaitType>> IWaitsRepository.GetTopWaitTypesAsync() =>
        GetTopWaitTypesAsync is { } answer ? Answered(nameof(GetTopWaitTypesAsync), answer()) : Unanswered<Task<IEnumerable<TopWaitType>>>(nameof(GetTopWaitTypesAsync));
}
