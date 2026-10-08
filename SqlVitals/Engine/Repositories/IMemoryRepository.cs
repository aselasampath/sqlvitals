using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Memory grants, clerks and counters, and the memory KPI snapshot (#47).</summary>
public interface IMemoryRepository
{
    Task<(IEnumerable<MemoryGrant> Grants, IEnumerable<MemoryClerk> Clerks, IEnumerable<MemoryCounter> Counters)> GetMemoryGrantsAsync(CancellationToken cancellationToken = default);

    // ── Memory KPI snapshot (for trend chart + KPI strip) ────────────
    Task<MemorySnapshot> GetMemorySnapshotAsync(CancellationToken cancellationToken = default);
}
