using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Indexes and statistics (#47).</summary>
public interface IIndexRepository
{
    Task<(IEnumerable<MissingIndex> Missing, IEnumerable<UnusedIndex> Unused)> GetIndexHealthAsync();
    Task<IEnumerable<IndexUsagePattern>> GetIndexUsagePatternsAsync();
    Task<IEnumerable<IndexFragmentation>> GetIndexFragmentationAsync(double minFragmentationPercent = 10, long minPageCount = 1000);
    Task<IEnumerable<StaleStatistic>> GetStaleStatisticsAsync();
}
