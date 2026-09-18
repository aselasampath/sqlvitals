using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

public interface IPlanCacheHealthRepository
{
    Task<(
        PlanCacheHealthSummary                  Summary,
        IEnumerable<MultiPlanQuery>             MultiPlanQueries,
        IEnumerable<KeyLookupQuery>             KeyLookupQueries,
        IEnumerable<CardinalityMisestimate>     CardinalityMisestimates,
        IEnumerable<SkewedParallelQuery>        SkewedParallelQueries,
        IEnumerable<PlanGuide>                  PlanGuides
    )> GetPlanCacheHealthAsync();
}
