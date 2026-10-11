using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// A repository whose every call also ends with a lifetime (#167): a page's, cancelled when the page
/// is left. A call's own token still cancels it; either one does. <see cref="StopTraceAsync"/> is
/// the exception: it is cleanup, so only its own token stops it.
/// </summary>
/// <param name="inner">The connection's repository.</param>
/// <param name="lifetime">The lifetime's token as it is now, read on every call.</param>
public sealed class LifetimeRepository(IWaitStatsRepository inner, Func<CancellationToken> lifetime) : IWaitStatsRepository
{
    /// <summary>The repository it hands each call to.</summary>
    public IWaitStatsRepository Inner => inner;

    // ── Server ────────────────────────────────────────────────────────────
    public Task<ServerCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetCapabilitiesAsync(cancellationToken: c), cancellationToken);

    public Task<bool> IsAzureSqlDatabaseAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.IsAzureSqlDatabaseAsync(cancellationToken: c), cancellationToken);

    public Task<bool> SupportsOnlineIndexRebuildAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.SupportsOnlineIndexRebuildAsync(cancellationToken: c), cancellationToken);

    // ── Waits ─────────────────────────────────────────────────────────────
    public Task<IEnumerable<WaitStatCumulative>> GetCumulativeWaitsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetCumulativeWaitsAsync(cancellationToken: c), cancellationToken);

    public Task<IEnumerable<ActiveWait>> GetActiveWaitsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetActiveWaitsAsync(cancellationToken: c), cancellationToken);

    public Task<SignalVsResourceWait?> GetSignalVsResourceAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetSignalVsResourceAsync(cancellationToken: c), cancellationToken);

    public Task<IEnumerable<TopWaitType>> GetTopWaitTypesAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetTopWaitTypesAsync(cancellationToken: c), cancellationToken);

    // ── Memory ────────────────────────────────────────────────────────────
    public Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)> GetMemoryGrantsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetMemoryGrantsAsync(cancellationToken: c), cancellationToken);

    public Task<MemorySnapshot> GetMemorySnapshotAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetMemorySnapshotAsync(cancellationToken: c), cancellationToken);

    // ── Indexes and statistics ────────────────────────────────────────────
    public Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> GetIndexHealthAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetIndexHealthAsync(cancellationToken: c), cancellationToken);

    public Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetIndexUsagePatternsAsync(cancellationToken: c), cancellationToken);

    public Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000, CancellationToken cancellationToken = default) =>
        Run(c => inner.GetIndexFragmentationAsync(minFragmentationPercent, minPageCount, cancellationToken: c), cancellationToken);

    public Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetStaleStatisticsAsync(cancellationToken: c), cancellationToken);

    // ── Resource queries ──────────────────────────────────────────────────
    public Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
                 IEnumerable<ResourceIntensiveQuery> ByCpu,
                 IEnumerable<ResourceIntensiveQuery> ByHighestReads)> GetResourceIntensiveQueriesAsync(int topN = 20, CancellationToken cancellationToken = default) =>
        Run(c => inner.GetResourceIntensiveQueriesAsync(topN, cancellationToken: c), cancellationToken);

    public Task<string?> GetObjectQueryPlanAsync(string objectName, CancellationToken cancellationToken = default) =>
        Run(c => inner.GetObjectQueryPlanAsync(objectName, cancellationToken: c), cancellationToken);

    public Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetImplicitConversionsAsync(cancellationToken: c), cancellationToken);

    // ── Storage ───────────────────────────────────────────────────────────
    public Task<(
        IEnumerable<ServerConfigParam> Config,
        IEnumerable<DatabaseFile> Files,
        IEnumerable<TempDbFileSummary> TempDbFiles,
        IEnumerable<TableStorage> TopTables
    )> GetDatabaseStorageAsync(CancellationToken cancellationToken = default) => Run(c => inner.GetDatabaseStorageAsync(cancellationToken: c), cancellationToken);

    // ── Sessions ──────────────────────────────────────────────────────────
    public Task<IEnumerable<ProcessNode>> GetProcessesAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetProcessesAsync(cancellationToken: c), cancellationToken);

    public Task<SessionPlan> GetSessionPlanAsync(int sessionId, CancellationToken cancellationToken = default) =>
        Run(c => inner.GetSessionPlanAsync(sessionId, cancellationToken: c), cancellationToken);

    public Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetApplicationConnectionsAsync(cancellationToken: c), cancellationToken);

    // ── Live metrics and Perfmon ──────────────────────────────────────────
    public Task<LiveMetricSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetLiveMetricsAsync(cancellationToken: c), cancellationToken);

    public Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetPerfmonCountersAsync(cancellationToken: c), cancellationToken);

    // ── TempDB ────────────────────────────────────────────────────────────
    public Task<(IEnumerable<TempDbFile> Files, IEnumerable<TempDbSession> Sessions, IEnumerable<TempDbCounter> Counters)> GetTempDbPressureAsync(CancellationToken cancellationToken = default) =>
        Run(c => inner.GetTempDbPressureAsync(cancellationToken: c), cancellationToken);

    // ── Plan Cache Health (delegates to PlanCacheHealthRepository) ────
    public Task<(
        SqlVitals.Engine.Models.PlanCacheHealthSummary Summary,
        IEnumerable<SqlVitals.Engine.Models.MultiPlanQuery> MultiPlanQueries,
        IEnumerable<SqlVitals.Engine.Models.KeyLookupQuery> KeyLookupQueries,
        IEnumerable<SqlVitals.Engine.Models.CardinalityMisestimate> CardinalityMisestimates,
        IEnumerable<SqlVitals.Engine.Models.SkewedParallelQuery> SkewedParallelQueries,
        IEnumerable<SqlVitals.Engine.Models.PlanGuide> PlanGuides
    )> GetPlanCacheHealthAsync(CancellationToken cancellationToken = default) => Run(c => inner.GetPlanCacheHealthAsync(cancellationToken: c), cancellationToken);

    // ── Live SP trace ─────────────────────────────────────────────────────
    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StartTraceAsync(SqlVitals.Engine.Models.SpTraceOptions options, CancellationToken cancellationToken = default)
        => Run(c => inner.StartTraceAsync(options, cancellationToken: c), cancellationToken);

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> StopTraceAsync(CancellationToken cancellationToken = default)
        => inner.StopTraceAsync(cancellationToken: cancellationToken);

    public Task<SqlVitals.Engine.Models.TraceSessionStatus> GetTraceStatusAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetTraceStatusAsync(cancellationToken: c), cancellationToken);

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpTraceEvent>> PollTraceEventsAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.PollTraceEventsAsync(cancellationToken: c), cancellationToken);

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> PollProcedureStatsAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.PollProcedureStatsAsync(cancellationToken: c), cancellationToken);

    public Task<IReadOnlyList<SqlVitals.Engine.Models.SpAggregateRow>> GetProcedureStatsTotalsAsync(int topN = 25, CancellationToken cancellationToken = default)
        => Run(c => inner.GetProcedureStatsTotalsAsync(topN, cancellationToken: c), cancellationToken);

    // ── Monitoring history ────────────────────────────────────────────────
    public Task<HistoryDetailSnapshot> GetHistoryDetailAsync(DateTime? queriesExecutedSince, int maxQueries, CancellationToken cancellationToken = default)
        => Run(c => inner.GetHistoryDetailAsync(queriesExecutedSince, maxQueries, cancellationToken: c), cancellationToken);

    public Task<IReadOnlyList<QueryTextInfo>> GetQueryTextsAsync(IReadOnlyCollection<string> queryHashes, int maxLength, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryTextsAsync(queryHashes, maxLength, cancellationToken: c), cancellationToken);

    // ── Query regressions ─────────────────────────────────────────────────
    public Task<QueryStoreRegressionStats> GetQueryStoreRegressionStatsAsync(Regressions.RegressionWindows windows, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreRegressionStatsAsync(windows, cancellationToken: c), cancellationToken);

    public Task<IReadOnlyDictionary<long, string>> GetQueryStoreTextsAsync(IReadOnlyCollection<long> queryIds, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreTextsAsync(queryIds, cancellationToken: c), cancellationToken);

    public Task<string?> GetQueryStorePlanAsync(long planId, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStorePlanAsync(planId, cancellationToken: c), cancellationToken);

    public Task<string?> GetCachedPlanForQueryHashAsync(string queryHash, CancellationToken cancellationToken = default)
        => Run(c => inner.GetCachedPlanForQueryHashAsync(queryHash, cancellationToken: c), cancellationToken);

    // ── Query Store ───────────────────────────────────────────────────────
    public Task<(QueryStoreHealth? Health, IEnumerable<QueryStoreTopQuery> TopQueries)> GetQueryStoreAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreAsync(cancellationToken: c), cancellationToken);

    public Task<QueryStoreStatus> GetQueryStoreStatusAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreStatusAsync(cancellationToken: c), cancellationToken);

    public Task<QueryStoreRows<QueryStoreQueryRow>> GetQueryStoreTopQueriesAsync(
        QueryStore.QueryStorePeriod period, QueryStore.QueryStoreMetric metric, QueryStore.QueryStoreStatistic statistic, int top, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreTopQueriesAsync(period, metric, statistic, top, cancellationToken: c), cancellationToken);

    public Task<QueryStoreRows<QueryStoreSlot>> GetQueryStoreOverTimeAsync(QueryStore.QueryStorePeriod period, TimeSpan slotLength, DateTimeOffset origin, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreOverTimeAsync(period, slotLength, origin, cancellationToken: c), cancellationToken);

    public Task<QueryStoreRows<QueryStoreVariationRow>> GetQueryStoreHighVariationAsync(
        QueryStore.QueryStorePeriod period, QueryStore.QueryStoreMetric metric, int minExecutions, int top, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreHighVariationAsync(period, metric, minExecutions, top, cancellationToken: c), cancellationToken);

    public Task<QueryStoreRows<QueryStoreWaitCategory>> GetQueryStoreWaitCategoriesAsync(QueryStore.QueryStorePeriod period, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreWaitCategoriesAsync(period, cancellationToken: c), cancellationToken);

    public Task<QueryStoreRows<QueryStoreWaitQuery>> GetQueryStoreWaitQueriesAsync(QueryStore.QueryStorePeriod period, int category, int top, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreWaitQueriesAsync(period, category, top, cancellationToken: c), cancellationToken);

    public Task<QueryStoreForcedPlans> GetQueryStoreForcedPlansAsync(QueryStore.QueryStorePeriod period, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreForcedPlansAsync(period, cancellationToken: c), cancellationToken);

    public Task<QueryStoreQueryDetails> GetQueryStoreQueryDetailsAsync(long queryId, QueryStore.QueryStorePeriod period, CancellationToken cancellationToken = default)
        => Run(c => inner.GetQueryStoreQueryDetailsAsync(queryId, period, cancellationToken: c), cancellationToken);

    // ── Deadlock history ──────────────────────────────────────────────────
    public Task<Deadlocks.DeadlockHistory> GetDeadlockHistoryAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetDeadlockHistoryAsync(cancellationToken: c), cancellationToken);

    // ── SQL Agent jobs ────────────────────────────────────────────────────
    public Task<AgentJobs.AgentJobList> GetAgentJobsAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetAgentJobsAsync(cancellationToken: c), cancellationToken);

    public Task<IReadOnlyList<AgentJobs.JobRun>> GetAgentJobRunsAsync(Guid jobId, DateTime? runningSince, CancellationToken cancellationToken = default)
        => Run(c => inner.GetAgentJobRunsAsync(jobId, runningSince, cancellationToken: c), cancellationToken);

    // ── Backups and RPO ───────────────────────────────────────────────────
    public Task<Backups.BackupList> GetBackupStatusAsync(Backups.BackupRpo rpo, CancellationToken cancellationToken = default)
        => Run(c => inner.GetBackupStatusAsync(rpo, cancellationToken: c), cancellationToken);

    public Task<IReadOnlyList<Backups.BackupHistoryEntry>> GetBackupHistoryAsync(string databaseName, DateTime createDate, CancellationToken cancellationToken = default)
        => Run(c => inner.GetBackupHistoryAsync(databaseName, createDate, cancellationToken: c), cancellationToken);

    // ── Slow health checks ────────────────────────────────────────────────
    public Task<Monitoring.ServerCheckFigures> GetServerChecksAsync(Monitoring.ServerChecksRequest request, CancellationToken cancellationToken = default)
        => Run(c => inner.GetServerChecksAsync(request, cancellationToken: c), cancellationToken);

    // ── File I/O latency ──────────────────────────────────────────────────
    public Task<FileIo.FileIoReading> ReadFileIoAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.ReadFileIoAsync(cancellationToken: c), cancellationToken);

    // ── Configuration checks ──────────────────────────────────────────────
    public Task<ConfigChecks.ConfigCheckList> GetConfigurationChecksAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetConfigurationChecksAsync(cancellationToken: c), cancellationToken);

    // ── Security audit ────────────────────────────────────────────────────
    public Task<Security.SecurityFindingList> GetSecurityAuditAsync(CancellationToken cancellationToken = default)
        => Run(c => inner.GetSecurityAuditAsync(cancellationToken: c), cancellationToken);

    // The call with the lifetime's token, linked with its own when it has one.
    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var bound = lifetime();
        if (!cancellationToken.CanBeCanceled)
            return await call(bound).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(bound, cancellationToken);
        return await call(linked.Token).ConfigureAwait(false);
    }
}
