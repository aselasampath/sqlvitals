using SqlPulse.Engine.Models;

namespace SqlPulse.Engine.Repositories;

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
