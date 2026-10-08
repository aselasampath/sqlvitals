namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Every area's repository in one (#47), for the pages that still take the whole of it. Each area
/// has its own interface and class; <see cref="WaitStatsRepository"/> hands each call to it.
/// </summary>
public interface IWaitStatsRepository : ITempDbRepository, IPlanCacheHealthRepository, ISpTraceRepository, IHistoryRepository,
                                        IQueryRegressionRepository, IDeadlockRepository, IAgentJobRepository,
                                        IBackupRepository, IFileIoRepository, IConfigurationRepository,
                                        ISecurityAuditRepository, IQueryStoreRepository,
                                        IServerRepository, IWaitsRepository, IMemoryRepository, IIndexRepository,
                                        IResourceQueryRepository, IStorageRepository, ISessionRepository, ILiveMetricsRepository
{
}
