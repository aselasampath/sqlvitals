using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>The plan cache's costliest queries, an object's plan, and implicit conversions (#47).</summary>
public interface IResourceQueryRepository
{
    // ── Resource-intensive queries ───────────────────────────────────
    Task<(IEnumerable<ResourceIntensiveQuery> ByReads,
          IEnumerable<ResourceIntensiveQuery> ByCpu,
          IEnumerable<ResourceIntensiveQuery> ByHighestReads)> GetResourceIntensiveQueriesAsync(int topN = 20, CancellationToken cancellationToken = default);
    Task<string?> GetObjectQueryPlanAsync(string objectName, CancellationToken cancellationToken = default);
    Task<IEnumerable<ImplicitConversion>> GetImplicitConversionsAsync(CancellationToken cancellationToken = default);
}
