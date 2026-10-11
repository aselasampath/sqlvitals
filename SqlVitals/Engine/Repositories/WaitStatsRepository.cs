using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Every area's repository behind one <see cref="IWaitStatsRepository"/> (#47): it holds one of each
/// for its connection, and hands each call to the area's. It runs no SQL itself. They share one
/// read of the server's capabilities (#167).
/// </summary>
public class WaitStatsRepository(SqlTarget target, ServerCapabilitiesCache capabilities) : IWaitStatsRepository
{
    public WaitStatsRepository(SqlTarget target) : this(target, new ServerCapabilitiesCache())
    {
    }

    private readonly ServerRepository _server = new(target, capabilities);

    private readonly WaitsRepository _waits = new(target, capabilities);

    // Each of these remembers which columns its server has, so one instance per connection.
    private readonly MemoryRepository _memory = new(target, capabilities);

    private readonly IndexRepository _indexes = new(target, capabilities);

    private readonly ResourceQueryRepository _resourceQueries = new(target, capabilities);

    private readonly StorageRepository _storage = new(target, capabilities);

    private readonly SessionRepository _sessions = new(target, capabilities);

    // Caches the engine edition, so one instance per connection.
    private readonly LiveMetricsRepository _liveMetrics = new(target, capabilities);

    private readonly PlanCacheHealthRepository _planHealth = new(target, capabilities);

    private readonly TempDbRepository _tempDb = new(target, capabilities);

    // Stateful across polls (dedup window + previous DMV snapshot), so it must be a single
    // long-lived instance rather than created per call.
    private readonly SpTraceRepository _spTrace = new(target, capabilities);

    // Caches the engine edition, so one instance per connection.
    private readonly HistoryRepository _history = new(target, capabilities);

    private readonly QueryRegressionRepository _regressions = new(target, capabilities);

    // Remembers which Query Store views and columns the server has, so one instance per connection.
    private readonly QueryStoreRepository _queryStore = new(target, capabilities);

    private readonly DeadlockRepository _deadlocks = new(target, capabilities);

    private readonly AgentJobRepository _agentJobs = new(target, capabilities);

    private readonly BackupRepository _backups = new(target, capabilities);

    private readonly ServerChecksRepository _serverChecks = new(target, capabilities);

    // Remembers which volume each file is on, so one instance per connection.
    private readonly FileIoRepository _fileIo = new(target, capabilities);

    private readonly ConfigurationRepository _configuration = new(target, capabilities);

    private readonly SecurityAuditRepository _securityAudit = new(target, capabilities);

    // ── Server (delegated to ServerRepository) ────────────────────────
    public Task<ServerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _server.GetCapabilitiesAsync(cancellationToken: cancellationToken);

    public Task<bool> IsAzureSqlDatabaseAsync(CancellationToken cancellationToken = default) => _server.IsAzureSqlDatabaseAsync(cancellationToken: cancellationToken);

    public Task<bool> SupportsOnlineIndexRebuildAsync(CancellationToken cancellationToken = default) => _server.SupportsOnlineIndexRebuildAsync(cancellationToken: cancellationToken);

    // ── Waits (delegated to WaitsRepository) ──────────────────────────
    public Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync(CancellationToken cancellationToken = default) => _waits.GetCumulativeWaitsAsync(cancellationToken: cancellationToken);

    public Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync(CancellationToken cancellationToken = default) => _waits.GetActiveWaitsAsync(cancellationToken: cancellationToken);

    public Task<SignalVsResourceWait?> GetSignalVsResourceAsync(CancellationToken cancellationToken = default) => _waits.GetSignalVsResourceAsync(cancellationToken: cancellationToken);

    public Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync(CancellationToken cancellationToken = default) => _waits.GetTopWaitTypesAsync(cancellationToken: cancellationToken);

    // ── Memory (delegated to MemoryRepository) ────────────────────────
    public Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)> GetMemoryGrantsAsync(CancellationToken cancellationToken = default) =>
        _memory.GetMemoryGrantsAsync(cancellationToken: cancellationToken);

    public Task<MemorySnapshot> GetMemorySnapshotAsync(CancellationToken cancellationToken = default) => _memory.GetMemorySnapshotAsync(cancellationToken: cancellationToken);

    // ── Indexes and statistics (delegated to IndexRepository) ─────────
    public Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> GetIndexHealthAsync(CancellationToken cancellationToken = default) => _indexes.GetIndexHealthAsync(cancellationToken: cancellationToken);

    public Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync(CancellationToken cancellationToken = default) => _indexes.GetIndexUsagePatternsAsync(cancellationToken: cancellationToken);

    public Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000, CancellationToken cancellationToken = default) =>
        _indexes.GetIndexFragmentationAsync(minFragmentationPercent, minPageCount, cancellationToken: cancellationToken);

    public Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync(CancellationToken cancellationToken = default) => _indexes.GetStaleStatisticsAsync(cancellationToken: cancellationToken);

    // ── Resource queries (delegated to ResourceQueryRepository) ───────
    public Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
                 IEnumerable<ResourceIntensiveQuery> ByCpu,
                 IEnumerable<ResourceIntensiveQuery> ByHighestReads)> GetResourceIntensiveQueriesAsync(int topN = 20, CancellationToken cancellationToken = default) =>
        _resourceQueries.GetResourceIntensiveQueriesAsync(topN, cancellationToken: cancellationToken);

    public Task<string?> GetObjectQueryPlanAsync(string objectName, CancellationToken cancellationToken = default) => _resourceQueries.GetObjectQueryPlanAsync(objectName, cancellationToken: cancellationToken);

    public Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync(CancellationToken cancellationToken = default) => _resourceQueries.GetImplicitConversionsAsync(cancellationToken: cancellationToken);

    // ── Storage (delegated to StorageRepository) ──────────────────────
    public Task<(
        IEnumerable<ServerConfigParam> Config,
        IEnumerable<DatabaseFile> Files,
        IEnumerable<TempDbFileSummary> TempDbFiles,
        IEnumerable<TableStorage> TopTables
    )> GetDatabaseStorageAsync(CancellationToken cancellationToken = default) => _storage.GetDatabaseStorageAsync(cancellationToken: cancellationToken);

    // ── Sessions (delegated to SessionRepository) ─────────────────────
    public Task<IEnumerable<ProcessNode>> GetProcessesAsync(CancellationToken cancellationToken = default) => _sessions.GetProcessesAsync(cancellationToken: cancellationToken);

    public Task<SessionPlan> GetSessionPlanAsync(int sessionId, CancellationToken cancellationToken = default) => _sessions.GetSessionPlanAsync(sessionId, cancellationToken: cancellationToken);

    public Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync(CancellationToken cancellationToken = default) => _sessions.GetApplicationConnectionsAsync(cancellationToken: cancellationToken);

    // ── Live metrics and Perfmon (delegated to LiveMetricsRepository) ─
    public Task<LiveMetricSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken = default) => _liveMetrics.GetLiveMetricsAsync(cancellationToken: cancellationToken);

    public Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync(CancellationToken cancellationToken = default) => _liveMetrics.GetPerfmonCountersAsync(cancellationToken: cancellationToken);

    // ── TempDB (delegated to TempDbRepository) ────────────────────────
    public Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync(CancellationToken cancellationToken = default) =>
        _tempDb.GetTempDbPressureAsync(cancellationToken: cancellationToken);

    // ── Plan Cache Health (delegates to PlanCacheHealthRepository) ────
    public Task<(
        SqlVitals.Engine.Models.PlanCacheHealthSummary Summary,
        IEnumerable<SqlVitals.Engine.Models.MultiPlanQuery> MultiPlanQueries,
        IEnumerable<SqlVitals.Engine.Models.KeyLookupQuery> KeyLookupQueries,
        IEnumerable<SqlVitals.Engine.Models.CardinalityMisestimate> CardinalityMisestimates,
        IEnumerable<SqlVitals.Engine.Models.SkewedParallelQuery> SkewedParallelQueries,
        IEnumerable<SqlVitals.Engine.Models.PlanGuide> PlanGuides
    )> GetPlanCacheHealthAsync(CancellationToken cancellationToken = default) => _planHealth.GetPlanCacheHealthAsync(cancellationToken: cancellationToken);

    // ── Live SP trace (delegated to SpTraceRepository) ────────────────
    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StartTraceAsync(SqlVitals.Engine.Models.SpTraceOptions options, CancellationToken cancellationToken = default)
        => _spTrace.StartTraceAsync(options, cancellationToken: cancellationToken);

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StopTraceAsync(CancellationToken cancellationToken = default)
        => _spTrace.StopTraceAsync(cancellationToken: cancellationToken);

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> GetTraceStatusAsync(CancellationToken cancellationToken = default)
        => _spTrace.GetTraceStatusAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpTraceEvent>> PollTraceEventsAsync(CancellationToken cancellationToken = default)
        => _spTrace.PollTraceEventsAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> PollProcedureStatsAsync(CancellationToken cancellationToken = default)
        => _spTrace.PollProcedureStatsAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> GetProcedureStatsTotalsAsync(int topN = 25, CancellationToken cancellationToken = default)
        => _spTrace.GetProcedureStatsTotalsAsync(topN, cancellationToken: cancellationToken);

    // ── Monitoring history (delegated to HistoryRepository) ───────────
    public Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken = default)
        => _history.GetHistoryDetailAsync(queriesExecutedSince, maxQueries, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken = default)
        => _history.GetQueryTextsAsync(queryHashes, maxLength, cancellationToken: cancellationToken);

    // ── Query regressions (delegated to QueryRegressionRepository) ────
    public Task<QueryStoreRegressionStats> GetQueryStoreRegressionStatsAsync(Regressions.RegressionWindows windows, CancellationToken cancellationToken = default)
        => _regressions.GetQueryStoreRegressionStatsAsync(windows, cancellationToken: cancellationToken);

    public Task<IReadOnlyDictionary<long, string>> GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds, CancellationToken cancellationToken = default)
        => _regressions.GetQueryStoreTextsAsync(queryIds, cancellationToken: cancellationToken);

    public Task<string?> GetQueryStorePlanAsync(long planId, CancellationToken cancellationToken = default)
        => _regressions.GetQueryStorePlanAsync(planId, cancellationToken: cancellationToken);

    public Task<string?> GetCachedPlanForQueryHashAsync(string queryHash, CancellationToken cancellationToken = default)
        => _regressions.GetCachedPlanForQueryHashAsync(queryHash, cancellationToken: cancellationToken);

    // ── Query Store (delegated to QueryStoreRepository) ───────────────
    public Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)> GetQueryStoreAsync(CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreAsync(cancellationToken: cancellationToken);

    public Task<QueryStoreStatus> GetQueryStoreStatusAsync(CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreStatusAsync(cancellationToken: cancellationToken);

    public Task<QueryStoreRows<QueryStoreQueryRow>> GetQueryStoreTopQueriesAsync(
        QueryStore.QueryStorePeriod period, QueryStore.QueryStoreMetric metric, QueryStore.QueryStoreStatistic statistic, int top, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreTopQueriesAsync(period, metric, statistic, top, cancellationToken: cancellationToken);

    public Task<QueryStoreRows<QueryStoreSlot>> GetQueryStoreOverTimeAsync(QueryStore.QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreOverTimeAsync(period, slotLength, origin, cancellationToken: cancellationToken);

    public Task<QueryStoreRows<QueryStoreVariationRow>> GetQueryStoreHighVariationAsync(
        QueryStore.QueryStorePeriod period, QueryStore.QueryStoreMetric metric, int minExecutions, int top, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreHighVariationAsync(period, metric, minExecutions, top, cancellationToken: cancellationToken);

    public Task<QueryStoreRows<QueryStoreWaitCategory>> GetQueryStoreWaitCategoriesAsync(QueryStore.QueryStorePeriod period, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreWaitCategoriesAsync(period, cancellationToken: cancellationToken);

    public Task<QueryStoreRows<QueryStoreWaitQuery>> GetQueryStoreWaitQueriesAsync(QueryStore.QueryStorePeriod period, int category, int top, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreWaitQueriesAsync(period, category, top, cancellationToken: cancellationToken);

    public Task<QueryStoreForcedPlans> GetQueryStoreForcedPlansAsync(QueryStore.QueryStorePeriod period, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreForcedPlansAsync(period, cancellationToken: cancellationToken);

    public Task<QueryStoreQueryDetails> GetQueryStoreQueryDetailsAsync(long queryId, QueryStore.QueryStorePeriod period, CancellationToken cancellationToken = default)
        => _queryStore.GetQueryStoreQueryDetailsAsync(queryId, period, cancellationToken: cancellationToken);

    // ── Deadlock history (delegated to DeadlockRepository) ────────────
    public Task<Deadlocks.DeadlockHistory> GetDeadlockHistoryAsync(CancellationToken cancellationToken = default)
        => _deadlocks.GetDeadlockHistoryAsync(cancellationToken: cancellationToken);

    // ── SQL Agent jobs (delegated to AgentJobRepository) ──────────────
    public Task<AgentJobs.AgentJobList> GetAgentJobsAsync(CancellationToken cancellationToken = default)
        => _agentJobs.GetAgentJobsAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<AgentJobs.JobRun>> GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince, CancellationToken cancellationToken = default)
        => _agentJobs.GetAgentJobRunsAsync(jobId, runningSince, cancellationToken: cancellationToken);

    // ── Backups and RPO (delegated to BackupRepository) ───────────────
    public Task<Backups.BackupList> GetBackupStatusAsync(Backups.BackupRpo rpo, CancellationToken cancellationToken = default)
        => _backups.GetBackupStatusAsync(rpo, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<Backups.BackupHistoryEntry>> GetBackupHistoryAsync(string databaseName, DateTime createDate, CancellationToken cancellationToken = default)
        => _backups.GetBackupHistoryAsync(databaseName, createDate, cancellationToken: cancellationToken);

    // ── Slow health checks (delegated to ServerChecksRepository) ──────
    public Task<Monitoring.ServerCheckFigures> GetServerChecksAsync(Monitoring.ServerChecksRequest request, CancellationToken cancellationToken = default)
        => _serverChecks.GetServerChecksAsync(request, cancellationToken: cancellationToken);

    // ── File I/O latency (delegated to FileIoRepository) ──────────────
    public Task<FileIo.FileIoReading> ReadFileIoAsync(CancellationToken cancellationToken = default)
        => _fileIo.ReadFileIoAsync(cancellationToken: cancellationToken);

    // ── Configuration checks (delegated to ConfigurationRepository) ───
    public Task<ConfigChecks.ConfigCheckList> GetConfigurationChecksAsync(CancellationToken cancellationToken = default)
        => _configuration.GetConfigurationChecksAsync(cancellationToken: cancellationToken);

    // ── Security audit (delegated to SecurityAuditRepository) ─────────
    public Task<Security.SecurityFindingList> GetSecurityAuditAsync(CancellationToken cancellationToken = default)
        => _securityAudit.GetSecurityAuditAsync(cancellationToken: cancellationToken);
}
