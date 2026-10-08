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

    /// <summary>The token each method was last called with (#167), by its name.</summary>
    public IReadOnlyDictionary<string, CancellationToken> Tokens => _tokens;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationToken> _tokens = new();

    private T Answered<T>(string method, T answer, CancellationToken cancellationToken)
    {
        _tokens[method] = cancellationToken;
        Called?.Invoke(method);
        return answer;
    }

    private T Unanswered<T>(string method, CancellationToken cancellationToken) where T : Task
    {
        _tokens[method] = cancellationToken;
        Called?.Invoke(method);
        if (!Unreachable)
            throw new NotSupportedException($"{method} isn't faked in this test.");
        // T is Task<TResult>: Task.FromException<TResult>.
        return (T)typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                              .MakeGenericMethod(typeof(T).GetGenericArguments()[0])
                              .Invoke(null, [new InvalidOperationException("No server in tests.")])!;
    }

    public Func<Task<AgentJobList>>? GetAgentJobsAsync { get; set; }
    Task<AgentJobList> IAgentJobRepository.GetAgentJobsAsync(CancellationToken cancellationToken) =>
        GetAgentJobsAsync is { } answer ? Answered(nameof(GetAgentJobsAsync), answer(), cancellationToken) : Unanswered<Task<AgentJobList>>(nameof(GetAgentJobsAsync), cancellationToken);

    public Func<Guid, DateTime?, Task<IReadOnlyList<JobRun>>>? GetAgentJobRunsAsync { get; set; }
    Task<IReadOnlyList<JobRun>> IAgentJobRepository.GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince, CancellationToken cancellationToken) =>
        GetAgentJobRunsAsync is { } answer ? Answered(nameof(GetAgentJobRunsAsync), answer(jobId, runningSince), cancellationToken) : Unanswered<Task<IReadOnlyList<JobRun>>>(nameof(GetAgentJobRunsAsync), cancellationToken);

    public Func<BackupRpo, Task<BackupList>>? GetBackupStatusAsync { get; set; }
    Task<BackupList> IBackupRepository.GetBackupStatusAsync(BackupRpo rpo, CancellationToken cancellationToken) =>
        GetBackupStatusAsync is { } answer ? Answered(nameof(GetBackupStatusAsync), answer(rpo), cancellationToken) : Unanswered<Task<BackupList>>(nameof(GetBackupStatusAsync), cancellationToken);

    public Func<string, DateTime, Task<IReadOnlyList<BackupHistoryEntry>>>? GetBackupHistoryAsync { get; set; }
    Task<IReadOnlyList<BackupHistoryEntry>> IBackupRepository.GetBackupHistoryAsync(string databaseName, DateTime createDate, CancellationToken cancellationToken) =>
        GetBackupHistoryAsync is { } answer ? Answered(nameof(GetBackupHistoryAsync), answer(databaseName, createDate), cancellationToken) : Unanswered<Task<IReadOnlyList<BackupHistoryEntry>>>(nameof(GetBackupHistoryAsync), cancellationToken);

    public Func<Task<ConfigCheckList>>? GetConfigurationChecksAsync { get; set; }
    Task<ConfigCheckList> IConfigurationRepository.GetConfigurationChecksAsync(CancellationToken cancellationToken) =>
        GetConfigurationChecksAsync is { } answer ? Answered(nameof(GetConfigurationChecksAsync), answer(), cancellationToken) : Unanswered<Task<ConfigCheckList>>(nameof(GetConfigurationChecksAsync), cancellationToken);

    public Func<Task<DeadlockHistory>>? GetDeadlockHistoryAsync { get; set; }
    Task<DeadlockHistory> IDeadlockRepository.GetDeadlockHistoryAsync(CancellationToken cancellationToken) =>
        GetDeadlockHistoryAsync is { } answer ? Answered(nameof(GetDeadlockHistoryAsync), answer(), cancellationToken) : Unanswered<Task<DeadlockHistory>>(nameof(GetDeadlockHistoryAsync), cancellationToken);

    public Func<Task<FileIoReading>>? ReadFileIoAsync { get; set; }
    Task<FileIoReading> IFileIoRepository.ReadFileIoAsync(CancellationToken cancellationToken) =>
        ReadFileIoAsync is { } answer ? Answered(nameof(ReadFileIoAsync), answer(), cancellationToken) : Unanswered<Task<FileIoReading>>(nameof(ReadFileIoAsync), cancellationToken);

    public Func<DateTime?, int, Task<HistoryDetailSnapshot>>? GetHistoryDetailAsync { get; set; }
    Task<HistoryDetailSnapshot> IHistoryRepository.GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken) =>
        GetHistoryDetailAsync is { } answer ? Answered(nameof(GetHistoryDetailAsync), answer(queriesExecutedSince, maxQueries), cancellationToken) : Unanswered<Task<HistoryDetailSnapshot>>(nameof(GetHistoryDetailAsync), cancellationToken);

    public Func<IReadOnlyCollection<string>, int, Task<IReadOnlyList<QueryTextInfo>>>? GetQueryTextsAsync { get; set; }
    Task<IReadOnlyList<QueryTextInfo>> IHistoryRepository.GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken) =>
        GetQueryTextsAsync is { } answer ? Answered(nameof(GetQueryTextsAsync), answer(queryHashes, maxLength), cancellationToken) : Unanswered<Task<IReadOnlyList<QueryTextInfo>>>(nameof(GetQueryTextsAsync), cancellationToken);

    public Func<Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)>>? GetIndexHealthAsync { get; set; }
    Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> IIndexRepository.GetIndexHealthAsync(CancellationToken cancellationToken) =>
        GetIndexHealthAsync is { } answer ? Answered(nameof(GetIndexHealthAsync), answer(), cancellationToken) : Unanswered<Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)>>(nameof(GetIndexHealthAsync), cancellationToken);

    public Func<Task<IEnumerable<IndexUsagePattern>>>? GetIndexUsagePatternsAsync { get; set; }
    Task<IEnumerable<IndexUsagePattern>> IIndexRepository.GetIndexUsagePatternsAsync(CancellationToken cancellationToken) =>
        GetIndexUsagePatternsAsync is { } answer ? Answered(nameof(GetIndexUsagePatternsAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<IndexUsagePattern>>>(nameof(GetIndexUsagePatternsAsync), cancellationToken);

    public Func<double, long, Task<IEnumerable<IndexFragmentation>>>? GetIndexFragmentationAsync { get; set; }
    Task<IEnumerable<IndexFragmentation>> IIndexRepository.GetIndexFragmentationAsync(double minFragmentationPercent, long minPageCount, CancellationToken cancellationToken) =>
        GetIndexFragmentationAsync is { } answer ? Answered(nameof(GetIndexFragmentationAsync), answer(minFragmentationPercent, minPageCount), cancellationToken) : Unanswered<Task<IEnumerable<IndexFragmentation>>>(nameof(GetIndexFragmentationAsync), cancellationToken);

    public Func<Task<IEnumerable<StaleStatistic>>>? GetStaleStatisticsAsync { get; set; }
    Task<IEnumerable<StaleStatistic>> IIndexRepository.GetStaleStatisticsAsync(CancellationToken cancellationToken) =>
        GetStaleStatisticsAsync is { } answer ? Answered(nameof(GetStaleStatisticsAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<StaleStatistic>>>(nameof(GetStaleStatisticsAsync), cancellationToken);

    public Func<Task<LiveMetricSnapshot>>? GetLiveMetricsAsync { get; set; }
    Task<LiveMetricSnapshot> ILiveMetricsRepository.GetLiveMetricsAsync(CancellationToken cancellationToken) =>
        GetLiveMetricsAsync is { } answer ? Answered(nameof(GetLiveMetricsAsync), answer(), cancellationToken) : Unanswered<Task<LiveMetricSnapshot>>(nameof(GetLiveMetricsAsync), cancellationToken);

    public Func<Task<IEnumerable<PerfmonCounter>>>? GetPerfmonCountersAsync { get; set; }
    Task<IEnumerable<PerfmonCounter>> ILiveMetricsRepository.GetPerfmonCountersAsync(CancellationToken cancellationToken) =>
        GetPerfmonCountersAsync is { } answer ? Answered(nameof(GetPerfmonCountersAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<PerfmonCounter>>>(nameof(GetPerfmonCountersAsync), cancellationToken);

    public Func<Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)>>? GetMemoryGrantsAsync { get; set; }
    Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)> IMemoryRepository.GetMemoryGrantsAsync(CancellationToken cancellationToken) =>
        GetMemoryGrantsAsync is { } answer ? Answered(nameof(GetMemoryGrantsAsync), answer(), cancellationToken) : Unanswered<Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)>>(nameof(GetMemoryGrantsAsync), cancellationToken);

    public Func<Task<MemorySnapshot>>? GetMemorySnapshotAsync { get; set; }
    Task<MemorySnapshot> IMemoryRepository.GetMemorySnapshotAsync(CancellationToken cancellationToken) =>
        GetMemorySnapshotAsync is { } answer ? Answered(nameof(GetMemorySnapshotAsync), answer(), cancellationToken) : Unanswered<Task<MemorySnapshot>>(nameof(GetMemorySnapshotAsync), cancellationToken);

    public Func<Task<( PlanCacheHealthSummary Summary, IEnumerable<MultiPlanQuery> MultiPlanQueries, IEnumerable<KeyLookupQuery> KeyLookupQueries, IEnumerable<CardinalityMisestimate> CardinalityMisestimates, IEnumerable<SkewedParallelQuery> SkewedParallelQueries, IEnumerable<PlanGuide> PlanGuides )>>? GetPlanCacheHealthAsync { get; set; }
    Task<( PlanCacheHealthSummary Summary, IEnumerable<MultiPlanQuery> MultiPlanQueries, IEnumerable<KeyLookupQuery> KeyLookupQueries, IEnumerable<CardinalityMisestimate> CardinalityMisestimates, IEnumerable<SkewedParallelQuery> SkewedParallelQueries, IEnumerable<PlanGuide> PlanGuides )> IPlanCacheHealthRepository.GetPlanCacheHealthAsync(CancellationToken cancellationToken) =>
        GetPlanCacheHealthAsync is { } answer ? Answered(nameof(GetPlanCacheHealthAsync), answer(), cancellationToken) : Unanswered<Task<( PlanCacheHealthSummary Summary, IEnumerable<MultiPlanQuery> MultiPlanQueries, IEnumerable<KeyLookupQuery> KeyLookupQueries, IEnumerable<CardinalityMisestimate> CardinalityMisestimates, IEnumerable<SkewedParallelQuery> SkewedParallelQueries, IEnumerable<PlanGuide> PlanGuides )>>(nameof(GetPlanCacheHealthAsync), cancellationToken);

    public Func<RegressionWindows, Task<QueryStoreRegressionStats>>? GetQueryStoreRegressionStatsAsync { get; set; }
    Task<QueryStoreRegressionStats> IQueryRegressionRepository.GetQueryStoreRegressionStatsAsync(RegressionWindows windows, CancellationToken cancellationToken) =>
        GetQueryStoreRegressionStatsAsync is { } answer ? Answered(nameof(GetQueryStoreRegressionStatsAsync), answer(windows), cancellationToken) : Unanswered<Task<QueryStoreRegressionStats>>(nameof(GetQueryStoreRegressionStatsAsync), cancellationToken);

    public Func<IReadOnlyCollection<long>, Task<IReadOnlyDictionary<long, string>>>? GetQueryStoreTextsAsync { get; set; }
    Task<IReadOnlyDictionary<long, string>> IQueryRegressionRepository.GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds, CancellationToken cancellationToken) =>
        GetQueryStoreTextsAsync is { } answer ? Answered(nameof(GetQueryStoreTextsAsync), answer(queryIds), cancellationToken) : Unanswered<Task<IReadOnlyDictionary<long, string>>>(nameof(GetQueryStoreTextsAsync), cancellationToken);

    public Func<long, Task<string?>>? GetQueryStorePlanAsync { get; set; }
    Task<string?> IQueryRegressionRepository.GetQueryStorePlanAsync(long planId, CancellationToken cancellationToken) =>
        GetQueryStorePlanAsync is { } answer ? Answered(nameof(GetQueryStorePlanAsync), answer(planId), cancellationToken) : Unanswered<Task<string?>>(nameof(GetQueryStorePlanAsync), cancellationToken);

    public Func<string, Task<string?>>? GetCachedPlanForQueryHashAsync { get; set; }
    Task<string?> IQueryRegressionRepository.GetCachedPlanForQueryHashAsync(string queryHash, CancellationToken cancellationToken) =>
        GetCachedPlanForQueryHashAsync is { } answer ? Answered(nameof(GetCachedPlanForQueryHashAsync), answer(queryHash), cancellationToken) : Unanswered<Task<string?>>(nameof(GetCachedPlanForQueryHashAsync), cancellationToken);

    public Func<Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)>>? GetQueryStoreAsync { get; set; }
    Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)> IQueryStoreRepository.GetQueryStoreAsync(CancellationToken cancellationToken) =>
        GetQueryStoreAsync is { } answer ? Answered(nameof(GetQueryStoreAsync), answer(), cancellationToken) : Unanswered<Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)>>(nameof(GetQueryStoreAsync), cancellationToken);

    public Func<Task<QueryStoreStatus>>? GetQueryStoreStatusAsync { get; set; }
    Task<QueryStoreStatus> IQueryStoreRepository.GetQueryStoreStatusAsync(CancellationToken cancellationToken) =>
        GetQueryStoreStatusAsync is { } answer ? Answered(nameof(GetQueryStoreStatusAsync), answer(), cancellationToken) : Unanswered<Task<QueryStoreStatus>>(nameof(GetQueryStoreStatusAsync), cancellationToken);

    public Func<QueryStorePeriod, QueryStoreMetric, QueryStoreStatistic, int, Task<QueryStoreRows<QueryStoreQueryRow>>>? GetQueryStoreTopQueriesAsync { get; set; }
    Task<QueryStoreRows<QueryStoreQueryRow>> IQueryStoreRepository.GetQueryStoreTopQueriesAsync(QueryStorePeriod period, QueryStoreMetric metric, QueryStoreStatistic statistic, int top, CancellationToken cancellationToken) =>
        GetQueryStoreTopQueriesAsync is { } answer ? Answered(nameof(GetQueryStoreTopQueriesAsync), answer(period, metric, statistic, top), cancellationToken) : Unanswered<Task<QueryStoreRows<QueryStoreQueryRow>>>(nameof(GetQueryStoreTopQueriesAsync), cancellationToken);

    public Func<QueryStorePeriod, TimeSpan, DateTimeOffset, Task<QueryStoreRows<QueryStoreSlot>>>? GetQueryStoreOverTimeAsync { get; set; }
    Task<QueryStoreRows<QueryStoreSlot>> IQueryStoreRepository.GetQueryStoreOverTimeAsync(QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin, CancellationToken cancellationToken) =>
        GetQueryStoreOverTimeAsync is { } answer ? Answered(nameof(GetQueryStoreOverTimeAsync), answer(period, slotLength, origin), cancellationToken) : Unanswered<Task<QueryStoreRows<QueryStoreSlot>>>(nameof(GetQueryStoreOverTimeAsync), cancellationToken);

    public Func<QueryStorePeriod, QueryStoreMetric, int, int, Task<QueryStoreRows<QueryStoreVariationRow>>>? GetQueryStoreHighVariationAsync { get; set; }
    Task<QueryStoreRows<QueryStoreVariationRow>> IQueryStoreRepository.GetQueryStoreHighVariationAsync(QueryStorePeriod period, QueryStoreMetric metric, int minExecutions, int top, CancellationToken cancellationToken) =>
        GetQueryStoreHighVariationAsync is { } answer ? Answered(nameof(GetQueryStoreHighVariationAsync), answer(period, metric, minExecutions, top), cancellationToken) : Unanswered<Task<QueryStoreRows<QueryStoreVariationRow>>>(nameof(GetQueryStoreHighVariationAsync), cancellationToken);

    public Func<QueryStorePeriod, Task<QueryStoreRows<QueryStoreWaitCategory>>>? GetQueryStoreWaitCategoriesAsync { get; set; }
    Task<QueryStoreRows<QueryStoreWaitCategory>> IQueryStoreRepository.GetQueryStoreWaitCategoriesAsync(QueryStorePeriod period, CancellationToken cancellationToken) =>
        GetQueryStoreWaitCategoriesAsync is { } answer ? Answered(nameof(GetQueryStoreWaitCategoriesAsync), answer(period), cancellationToken) : Unanswered<Task<QueryStoreRows<QueryStoreWaitCategory>>>(nameof(GetQueryStoreWaitCategoriesAsync), cancellationToken);

    public Func<QueryStorePeriod, int, int, Task<QueryStoreRows<QueryStoreWaitQuery>>>? GetQueryStoreWaitQueriesAsync { get; set; }
    Task<QueryStoreRows<QueryStoreWaitQuery>> IQueryStoreRepository.GetQueryStoreWaitQueriesAsync(QueryStorePeriod period, int category, int top, CancellationToken cancellationToken) =>
        GetQueryStoreWaitQueriesAsync is { } answer ? Answered(nameof(GetQueryStoreWaitQueriesAsync), answer(period, category, top), cancellationToken) : Unanswered<Task<QueryStoreRows<QueryStoreWaitQuery>>>(nameof(GetQueryStoreWaitQueriesAsync), cancellationToken);

    public Func<QueryStorePeriod, Task<QueryStoreForcedPlans>>? GetQueryStoreForcedPlansAsync { get; set; }
    Task<QueryStoreForcedPlans> IQueryStoreRepository.GetQueryStoreForcedPlansAsync(QueryStorePeriod period, CancellationToken cancellationToken) =>
        GetQueryStoreForcedPlansAsync is { } answer ? Answered(nameof(GetQueryStoreForcedPlansAsync), answer(period), cancellationToken) : Unanswered<Task<QueryStoreForcedPlans>>(nameof(GetQueryStoreForcedPlansAsync), cancellationToken);

    public Func<long, QueryStorePeriod, Task<QueryStoreQueryDetails>>? GetQueryStoreQueryDetailsAsync { get; set; }
    Task<QueryStoreQueryDetails> IQueryStoreRepository.GetQueryStoreQueryDetailsAsync(long queryId, QueryStorePeriod period, CancellationToken cancellationToken) =>
        GetQueryStoreQueryDetailsAsync is { } answer ? Answered(nameof(GetQueryStoreQueryDetailsAsync), answer(queryId, period), cancellationToken) : Unanswered<Task<QueryStoreQueryDetails>>(nameof(GetQueryStoreQueryDetailsAsync), cancellationToken);

    public Func<int, Task<(IEnumerable<ResourceIntensiveQuery> ByReads, IEnumerable<ResourceIntensiveQuery> ByCpu, IEnumerable<ResourceIntensiveQuery> ByHighestReads)>>? GetResourceIntensiveQueriesAsync { get; set; }
    Task<(IEnumerable<ResourceIntensiveQuery> ByReads, IEnumerable<ResourceIntensiveQuery> ByCpu, IEnumerable<ResourceIntensiveQuery> ByHighestReads)> IResourceQueryRepository.GetResourceIntensiveQueriesAsync(int topN, CancellationToken cancellationToken) =>
        GetResourceIntensiveQueriesAsync is { } answer ? Answered(nameof(GetResourceIntensiveQueriesAsync), answer(topN), cancellationToken) : Unanswered<Task<(IEnumerable<ResourceIntensiveQuery> ByReads, IEnumerable<ResourceIntensiveQuery> ByCpu, IEnumerable<ResourceIntensiveQuery> ByHighestReads)>>(nameof(GetResourceIntensiveQueriesAsync), cancellationToken);

    public Func<string, Task<string?>>? GetObjectQueryPlanAsync { get; set; }
    Task<string?> IResourceQueryRepository.GetObjectQueryPlanAsync(string objectName, CancellationToken cancellationToken) =>
        GetObjectQueryPlanAsync is { } answer ? Answered(nameof(GetObjectQueryPlanAsync), answer(objectName), cancellationToken) : Unanswered<Task<string?>>(nameof(GetObjectQueryPlanAsync), cancellationToken);

    public Func<Task<IEnumerable<ImplicitConversion>>>? GetImplicitConversionsAsync { get; set; }
    Task<IEnumerable<ImplicitConversion>> IResourceQueryRepository.GetImplicitConversionsAsync(CancellationToken cancellationToken) =>
        GetImplicitConversionsAsync is { } answer ? Answered(nameof(GetImplicitConversionsAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<ImplicitConversion>>>(nameof(GetImplicitConversionsAsync), cancellationToken);

    public Func<Task<SecurityFindingList>>? GetSecurityAuditAsync { get; set; }
    Task<SecurityFindingList> ISecurityAuditRepository.GetSecurityAuditAsync(CancellationToken cancellationToken) =>
        GetSecurityAuditAsync is { } answer ? Answered(nameof(GetSecurityAuditAsync), answer(), cancellationToken) : Unanswered<Task<SecurityFindingList>>(nameof(GetSecurityAuditAsync), cancellationToken);

    public Func<Task<ServerCapabilities>>? GetCapabilitiesAsync { get; set; }
    Task<ServerCapabilities> IServerRepository.GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        GetCapabilitiesAsync is { } answer ? Answered(nameof(GetCapabilitiesAsync), answer(), cancellationToken) : Unanswered<Task<ServerCapabilities>>(nameof(GetCapabilitiesAsync), cancellationToken);

    public Func<Task<bool>>? IsAzureSqlDatabaseAsync { get; set; }
    Task<bool> IServerRepository.IsAzureSqlDatabaseAsync(CancellationToken cancellationToken) =>
        IsAzureSqlDatabaseAsync is { } answer ? Answered(nameof(IsAzureSqlDatabaseAsync), answer(), cancellationToken) : Unanswered<Task<bool>>(nameof(IsAzureSqlDatabaseAsync), cancellationToken);

    public Func<Task<bool>>? SupportsOnlineIndexRebuildAsync { get; set; }
    Task<bool> IServerRepository.SupportsOnlineIndexRebuildAsync(CancellationToken cancellationToken) =>
        SupportsOnlineIndexRebuildAsync is { } answer ? Answered(nameof(SupportsOnlineIndexRebuildAsync), answer(), cancellationToken) : Unanswered<Task<bool>>(nameof(SupportsOnlineIndexRebuildAsync), cancellationToken);

    public Func<Task<IEnumerable<ProcessNode>>>? GetProcessesAsync { get; set; }
    Task<IEnumerable<ProcessNode>> ISessionRepository.GetProcessesAsync(CancellationToken cancellationToken) =>
        GetProcessesAsync is { } answer ? Answered(nameof(GetProcessesAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<ProcessNode>>>(nameof(GetProcessesAsync), cancellationToken);

    public Func<int, Task<SessionPlan>>? GetSessionPlanAsync { get; set; }
    Task<SessionPlan> ISessionRepository.GetSessionPlanAsync(int sessionId, CancellationToken cancellationToken) =>
        GetSessionPlanAsync is { } answer ? Answered(nameof(GetSessionPlanAsync), answer(sessionId), cancellationToken) : Unanswered<Task<SessionPlan>>(nameof(GetSessionPlanAsync), cancellationToken);

    public Func<Task<IEnumerable<ApplicationConnection>>>? GetApplicationConnectionsAsync { get; set; }
    Task<IEnumerable<ApplicationConnection>> ISessionRepository.GetApplicationConnectionsAsync(CancellationToken cancellationToken) =>
        GetApplicationConnectionsAsync is { } answer ? Answered(nameof(GetApplicationConnectionsAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<ApplicationConnection>>>(nameof(GetApplicationConnectionsAsync), cancellationToken);

    public Func<SpTraceOptions, Task<TraceSessionStatus>>? StartTraceAsync { get; set; }
    Task<TraceSessionStatus> ISpTraceRepository.StartTraceAsync(SpTraceOptions options, CancellationToken cancellationToken) =>
        StartTraceAsync is { } answer ? Answered(nameof(StartTraceAsync), answer(options), cancellationToken) : Unanswered<Task<TraceSessionStatus>>(nameof(StartTraceAsync), cancellationToken);

    public Func<Task<TraceSessionStatus>>? StopTraceAsync { get; set; }
    Task<TraceSessionStatus> ISpTraceRepository.StopTraceAsync(CancellationToken cancellationToken) =>
        StopTraceAsync is { } answer ? Answered(nameof(StopTraceAsync), answer(), cancellationToken) : Unanswered<Task<TraceSessionStatus>>(nameof(StopTraceAsync), cancellationToken);

    public Func<Task<TraceSessionStatus>>? GetTraceStatusAsync { get; set; }
    Task<TraceSessionStatus> ISpTraceRepository.GetTraceStatusAsync(CancellationToken cancellationToken) =>
        GetTraceStatusAsync is { } answer ? Answered(nameof(GetTraceStatusAsync), answer(), cancellationToken) : Unanswered<Task<TraceSessionStatus>>(nameof(GetTraceStatusAsync), cancellationToken);

    public Func<Task<IReadOnlyList<SpTraceEvent>>>? PollTraceEventsAsync { get; set; }
    Task<IReadOnlyList<SpTraceEvent>> ISpTraceRepository.PollTraceEventsAsync(CancellationToken cancellationToken) =>
        PollTraceEventsAsync is { } answer ? Answered(nameof(PollTraceEventsAsync), answer(), cancellationToken) : Unanswered<Task<IReadOnlyList<SpTraceEvent>>>(nameof(PollTraceEventsAsync), cancellationToken);

    public Func<Task<IReadOnlyList<SpAggregateRow>>>? PollProcedureStatsAsync { get; set; }
    Task<IReadOnlyList<SpAggregateRow>> ISpTraceRepository.PollProcedureStatsAsync(CancellationToken cancellationToken) =>
        PollProcedureStatsAsync is { } answer ? Answered(nameof(PollProcedureStatsAsync), answer(), cancellationToken) : Unanswered<Task<IReadOnlyList<SpAggregateRow>>>(nameof(PollProcedureStatsAsync), cancellationToken);

    public Func<int, Task<IReadOnlyList<SpAggregateRow>>>? GetProcedureStatsTotalsAsync { get; set; }
    Task<IReadOnlyList<SpAggregateRow>> ISpTraceRepository.GetProcedureStatsTotalsAsync(int topN, CancellationToken cancellationToken) =>
        GetProcedureStatsTotalsAsync is { } answer ? Answered(nameof(GetProcedureStatsTotalsAsync), answer(topN), cancellationToken) : Unanswered<Task<IReadOnlyList<SpAggregateRow>>>(nameof(GetProcedureStatsTotalsAsync), cancellationToken);

    public Func<Task<( IEnumerable<ServerConfigParam> Config, IEnumerable<DatabaseFile> Files, IEnumerable<TempDbFileSummary> TempDbFiles, IEnumerable<TableStorage> TopTables )>>? GetDatabaseStorageAsync { get; set; }
    Task<( IEnumerable<ServerConfigParam> Config, IEnumerable<DatabaseFile> Files, IEnumerable<TempDbFileSummary> TempDbFiles, IEnumerable<TableStorage> TopTables )> IStorageRepository.GetDatabaseStorageAsync(CancellationToken cancellationToken) =>
        GetDatabaseStorageAsync is { } answer ? Answered(nameof(GetDatabaseStorageAsync), answer(), cancellationToken) : Unanswered<Task<( IEnumerable<ServerConfigParam> Config, IEnumerable<DatabaseFile> Files, IEnumerable<TempDbFileSummary> TempDbFiles, IEnumerable<TableStorage> TopTables )>>(nameof(GetDatabaseStorageAsync), cancellationToken);

    public Func<Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)>>? GetTempDbPressureAsync { get; set; }
    Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> ITempDbRepository.GetTempDbPressureAsync(CancellationToken cancellationToken) =>
        GetTempDbPressureAsync is { } answer ? Answered(nameof(GetTempDbPressureAsync), answer(), cancellationToken) : Unanswered<Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)>>(nameof(GetTempDbPressureAsync), cancellationToken);

    public Func<Task<IEnumerable<WaitStatCumulative>>>? GetCumulativeWaitsAsync { get; set; }
    Task<IEnumerable<WaitStatCumulative>> IWaitsRepository.GetCumulativeWaitsAsync(CancellationToken cancellationToken) =>
        GetCumulativeWaitsAsync is { } answer ? Answered(nameof(GetCumulativeWaitsAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<WaitStatCumulative>>>(nameof(GetCumulativeWaitsAsync), cancellationToken);

    public Func<Task<IEnumerable<ActiveWait>>>? GetActiveWaitsAsync { get; set; }
    Task<IEnumerable<ActiveWait>> IWaitsRepository.GetActiveWaitsAsync(CancellationToken cancellationToken) =>
        GetActiveWaitsAsync is { } answer ? Answered(nameof(GetActiveWaitsAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<ActiveWait>>>(nameof(GetActiveWaitsAsync), cancellationToken);

    public Func<Task<SignalVsResourceWait?>>? GetSignalVsResourceAsync { get; set; }
    Task<SignalVsResourceWait?> IWaitsRepository.GetSignalVsResourceAsync(CancellationToken cancellationToken) =>
        GetSignalVsResourceAsync is { } answer ? Answered(nameof(GetSignalVsResourceAsync), answer(), cancellationToken) : Unanswered<Task<SignalVsResourceWait?>>(nameof(GetSignalVsResourceAsync), cancellationToken);

    public Func<Task<IEnumerable<TopWaitType>>>? GetTopWaitTypesAsync { get; set; }
    Task<IEnumerable<TopWaitType>> IWaitsRepository.GetTopWaitTypesAsync(CancellationToken cancellationToken) =>
        GetTopWaitTypesAsync is { } answer ? Answered(nameof(GetTopWaitTypesAsync), answer(), cancellationToken) : Unanswered<Task<IEnumerable<TopWaitType>>>(nameof(GetTopWaitTypesAsync), cancellationToken);
}
