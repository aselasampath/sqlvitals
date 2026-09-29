using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.QueryStore;

/// <summary>The Query details window's plans grid: each plan's executions in the period, summed over its intervals (#111).</summary>
public static class QueryStorePlanSummaries
{
    /// <summary>
    /// One row per plan used in the period, plus a forced plan that wasn't used (so a forced plan
    /// that never runs shows up). The most executed first.
    /// </summary>
    public static IReadOnlyList<QueryStorePlanSummary> From(
        IReadOnlyList<QueryStorePlanInterval> intervals, IReadOnlyList<QueryStorePlanState> plans)
    {
        var states = plans.ToDictionary(p => p.PlanId);

        var used = intervals.GroupBy(i => i.PlanId).Select(g =>
        {
            var totals = g.Aggregate(QueryStoreTotals.Zero, (sum, i) => sum.Add(i.Totals));
            states.TryGetValue(g.Key, out var state);
            return new QueryStorePlanSummary(
                g.Key,
                g.Min(i => i.FirstExecutionUtc ?? i.StartUtc),
                g.Max(i => i.LastExecutionUtc ?? i.StartUtc),
                totals.Executions,
                totals.Average(QueryStoreMetric.Duration),
                totals.Average(QueryStoreMetric.Cpu),
                totals.Average(QueryStoreMetric.LogicalReads),
                g.Sum(i => i.Failed),
                state?.IsForced ?? false,
                state?.ForceFailures ?? 0,
                state?.LastForceFailureReason);
        }).ToList();

        var usedIds = used.Select(u => u.PlanId).ToHashSet();
        var forcedUnused = plans.Where(p => p.IsForced && !usedIds.Contains(p.PlanId)).Select(p =>
            new QueryStorePlanSummary(p.PlanId, null, null, 0, null, null, null, 0, true, p.ForceFailures, p.LastForceFailureReason));

        return used.Concat(forcedUnused)
                   .OrderByDescending(s => s.Executions)
                   .ThenByDescending(s => s.PlanId)
                   .ToList();
    }
}
