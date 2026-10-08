using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Every area's repository behind one <see cref="IWaitStatsRepository"/> (#47): it holds one of each
/// for its connection, and hands each call to the area's. It runs no SQL itself.
/// </summary>
public class WaitStatsRepository(SqlTarget target) : IWaitStatsRepository
{
    private readonly ServerRepository _server = new(target);

    private readonly WaitsRepository _waits = new(target);

    // Each of these remembers which columns its server has, so one instance per connection.
    private readonly MemoryRepository _memory = new(target);

    private readonly IndexRepository _indexes = new(target);

    private readonly ResourceQueryRepository _resourceQueries = new(target);

    private readonly StorageRepository _storage = new(target);

    private readonly SessionRepository _sessions = new(target);

    // Caches the engine edition, so one instance per connection.
    private readonly LiveMetricsRepository _liveMetrics = new(target);

    private readonly PlanCacheHealthRepository _planHealth = new(target);

    private readonly TempDbRepository _tempDb = new(target);

    // Stateful across polls (dedup window + previous DMV snapshot), so it must be a single
    // long-lived instance rather than created per call.
    private readonly SpTraceRepository _spTrace = new(target);

    // Caches the engine edition, so one instance per connection.
    private readonly HistoryRepository _history = new(target);

    private readonly QueryRegressionRepository _regressions = new(target);

    // Remembers which Query Store views and columns the server has, so one instance per connection.
    private readonly QueryStoreRepository _queryStore = new(target);

    private readonly DeadlockRepository _deadlocks = new(target);

    private readonly AgentJobRepository _agentJobs = new(target);

    private readonly BackupRepository _backups = new(target);

    // Remembers which volume each file is on, so one instance per connection.
    private readonly FileIoRepository _fileIo = new(target);

    private readonly ConfigurationRepository _configuration = new(target);

    private readonly SecurityAuditRepository _securityAudit = new(target);

    // ── Server (delegated to ServerRepository) ────────────────────────
    public Task<bool> IsAzureSqlDatabaseAsync() => _server.IsAzureSqlDatabaseAsync();

    public Task<bool> SupportsOnlineIndexRebuildAsync() => _server.SupportsOnlineIndexRebuildAsync();

    // ── Waits (delegated to WaitsRepository) ──────────────────────────
    public Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync() => _waits.GetCumulativeWaitsAsync();

    public Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync() => _waits.GetActiveWaitsAsync();

    public Task<SignalVsResourceWait?> GetSignalVsResourceAsync() => _waits.GetSignalVsResourceAsync();

    public Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync() => _waits.GetTopWaitTypesAsync();

    // ── Memory (delegated to MemoryRepository) ────────────────────────
    public Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)> GetMemoryGrantsAsync() =>
        _memory.GetMemoryGrantsAsync();

    public Task<MemorySnapshot> GetMemorySnapshotAsync() => _memory.GetMemorySnapshotAsync();

    // ── Indexes and statistics (delegated to IndexRepository) ─────────
    public Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> GetIndexHealthAsync() => _indexes.GetIndexHealthAsync();

    public Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync() => _indexes.GetIndexUsagePatternsAsync();

    public Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000) =>
        _indexes.GetIndexFragmentationAsync(minFragmentationPercent, minPageCount);

    public Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync() => _indexes.GetStaleStatisticsAsync();

    // ── Resource queries (delegated to ResourceQueryRepository) ───────
    public Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
                 IEnumerable<ResourceIntensiveQuery> ByCpu,
                 IEnumerable<ResourceIntensiveQuery> ByHighestReads)> GetResourceIntensiveQueriesAsync(int topN = 20) =>
        _resourceQueries.GetResourceIntensiveQueriesAsync(topN);

    public Task<string?> GetObjectQueryPlanAsync(string objectName) => _resourceQueries.GetObjectQueryPlanAsync(objectName);

    public Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync() => _resourceQueries.GetImplicitConversionsAsync();

    // ── Storage (delegated to StorageRepository) ──────────────────────
    public Task<(
        IEnumerable<ServerConfigParam> Config,
        IEnumerable<DatabaseFile> Files,
        IEnumerable<TempDbFileSummary> TempDbFiles,
        IEnumerable<TableStorage> TopTables
    )> GetDatabaseStorageAsync() => _storage.GetDatabaseStorageAsync();

    // ── Sessions (delegated to SessionRepository) ─────────────────────
    public Task<IEnumerable<ProcessNode>> GetProcessesAsync() => _sessions.GetProcessesAsync();

    public Task<SessionPlan> GetSessionPlanAsync(int sessionId) => _sessions.GetSessionPlanAsync(sessionId);

    public Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync() => _sessions.GetApplicationConnectionsAsync();

    // ── Live metrics and Perfmon (delegated to LiveMetricsRepository) ─
    public Task<LiveMetricSnapshot> GetLiveMetricsAsync() => _liveMetrics.GetLiveMetricsAsync();

    public Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync() => _liveMetrics.GetPerfmonCountersAsync();

    // ── TempDB (delegated to TempDbRepository) ────────────────────────
    public Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync() =>
        _tempDb.GetTempDbPressureAsync();

    // ── Plan Cache Health (delegates to PlanCacheHealthRepository) ────
    public Task<(
        SqlVitals.Engine.Models.PlanCacheHealthSummary Summary,
        IEnumerable<SqlVitals.Engine.Models.MultiPlanQuery> MultiPlanQueries,
        IEnumerable<SqlVitals.Engine.Models.KeyLookupQuery> KeyLookupQueries,
        IEnumerable<SqlVitals.Engine.Models.CardinalityMisestimate> CardinalityMisestimates,
        IEnumerable<SqlVitals.Engine.Models.SkewedParallelQuery> SkewedParallelQueries,
        IEnumerable<SqlVitals.Engine.Models.PlanGuide> PlanGuides
    )> GetPlanCacheHealthAsync() => _planHealth.GetPlanCacheHealthAsync();

    // ── Live SP trace (delegated to SpTraceRepository) ────────────────
    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StartTraceAsync(SqlVitals.Engine.Models.SpTraceOptions options)
        => _spTrace.StartTraceAsync(options);

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StopTraceAsync()
        => _spTrace.StopTraceAsync();

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> GetTraceStatusAsync()
        => _spTrace.GetTraceStatusAsync();

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpTraceEvent>> PollTraceEventsAsync()
        => _spTrace.PollTraceEventsAsync();

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> PollProcedureStatsAsync()
        => _spTrace.PollProcedureStatsAsync();

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> GetProcedureStatsTotalsAsync(int topN = 25)
        => _spTrace.GetProcedureStatsTotalsAsync(topN);

    // ── Monitoring history (delegated to HistoryRepository) ───────────
    public Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries)
        => _history.GetHistoryDetailAsync(queriesExecutedSince, maxQueries);

    public Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength)
        => _history.GetQueryTextsAsync(queryHashes, maxLength);

    // ── Query regressions (delegated to QueryRegressionRepository) ────
    public Task<QueryStoreRegressionStats> GetQueryStoreRegressionStatsAsync(Regressions.RegressionWindows windows)
        => _regressions.GetQueryStoreRegressionStatsAsync(windows);

    public Task<IReadOnlyDictionary<long, string>> GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds)
        => _regressions.GetQueryStoreTextsAsync(queryIds);

    public Task<string?> GetQueryStorePlanAsync(long planId)
        => _regressions.GetQueryStorePlanAsync(planId);

    public Task<string?> GetCachedPlanForQueryHashAsync(string queryHash)
        => _regressions.GetCachedPlanForQueryHashAsync(queryHash);

    // ── Query Store (delegated to QueryStoreRepository) ───────────────
    public Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)> GetQueryStoreAsync()
        => _queryStore.GetQueryStoreAsync();

    public Task<QueryStoreStatus> GetQueryStoreStatusAsync()
        => _queryStore.GetQueryStoreStatusAsync();

    public Task<QueryStoreRows<QueryStoreQueryRow>> GetQueryStoreTopQueriesAsync(
        QueryStore.QueryStorePeriod period, QueryStore.QueryStoreMetric metric, QueryStore.QueryStoreStatistic statistic, int top)
        => _queryStore.GetQueryStoreTopQueriesAsync(period, metric, statistic, top);

    public Task<QueryStoreRows<QueryStoreSlot>> GetQueryStoreOverTimeAsync(QueryStore.QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin)
        => _queryStore.GetQueryStoreOverTimeAsync(period, slotLength, origin);

    public Task<QueryStoreRows<QueryStoreVariationRow>> GetQueryStoreHighVariationAsync(
        QueryStore.QueryStorePeriod period, QueryStore.QueryStoreMetric metric, int minExecutions, int top)
        => _queryStore.GetQueryStoreHighVariationAsync(period, metric, minExecutions, top);

    public Task<QueryStoreRows<QueryStoreWaitCategory>> GetQueryStoreWaitCategoriesAsync(QueryStore.QueryStorePeriod period)
        => _queryStore.GetQueryStoreWaitCategoriesAsync(period);

    public Task<QueryStoreRows<QueryStoreWaitQuery>> GetQueryStoreWaitQueriesAsync(QueryStore.QueryStorePeriod period, int category, int top)
        => _queryStore.GetQueryStoreWaitQueriesAsync(period, category, top);

    public Task<QueryStoreForcedPlans> GetQueryStoreForcedPlansAsync(QueryStore.QueryStorePeriod period)
        => _queryStore.GetQueryStoreForcedPlansAsync(period);

    public Task<QueryStoreQueryDetails> GetQueryStoreQueryDetailsAsync(long queryId, QueryStore.QueryStorePeriod period)
        => _queryStore.GetQueryStoreQueryDetailsAsync(queryId, period);

    // ── Deadlock history (delegated to DeadlockRepository) ────────────
    public Task<Deadlocks.DeadlockHistory> GetDeadlockHistoryAsync()
        => _deadlocks.GetDeadlockHistoryAsync();

    // ── SQL Agent jobs (delegated to AgentJobRepository) ──────────────
    public Task<AgentJobs.AgentJobList> GetAgentJobsAsync()
        => _agentJobs.GetAgentJobsAsync();

    public Task<IReadOnlyList<AgentJobs.JobRun>> GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince)
        => _agentJobs.GetAgentJobRunsAsync(jobId, runningSince);

    // ── Backups and RPO (delegated to BackupRepository) ───────────────
    public Task<Backups.BackupList> GetBackupStatusAsync(Backups.BackupRpo rpo)
        => _backups.GetBackupStatusAsync(rpo);

    public Task<IReadOnlyList<Backups.BackupHistoryEntry>> GetBackupHistoryAsync(string databaseName, DateTime createDate)
        => _backups.GetBackupHistoryAsync(databaseName, createDate);

    // ── File I/O latency (delegated to FileIoRepository) ──────────────
    public Task<FileIo.FileIoReading> ReadFileIoAsync()
        => _fileIo.ReadFileIoAsync();

    // ── Configuration checks (delegated to ConfigurationRepository) ───
    public Task<ConfigChecks.ConfigCheckList> GetConfigurationChecksAsync()
        => _configuration.GetConfigurationChecksAsync();

    // ── Security audit (delegated to SecurityAuditRepository) ─────────
    public Task<Security.SecurityFindingList> GetSecurityAuditAsync()
        => _securityAudit.GetSecurityAuditAsync();
}
