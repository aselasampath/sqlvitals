using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Indexes and statistics (#47).</summary>
public interface IIndexRepository
{
    Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> GetIndexHealthAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000, CancellationToken cancellationToken = default);
    Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync(CancellationToken cancellationToken = default);
}
