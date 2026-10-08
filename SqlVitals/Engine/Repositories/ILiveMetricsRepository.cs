using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>The live metrics dashboard and the Perfmon counters (#47).</summary>
public interface ILiveMetricsRepository
{
    // ── Live metrics dashboard ───────────────────────────────────────
    Task<LiveMetricSnapshot> GetLiveMetricsAsync();

    // ── Perfmon counters (live DMV snapshot) ─────────────────────────
    Task<IEnumerable<PerfmonCounter>> GetPerfmonCountersAsync();
}
