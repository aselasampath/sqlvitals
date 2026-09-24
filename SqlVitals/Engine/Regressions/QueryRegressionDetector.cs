using System.Globalization;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Regressions;

/// <summary>
/// When a query counts as regressed: its average duration or CPU per execution rose by more than
/// <see cref="ThresholdPct"/> percent, with at least <see cref="MinExecutions"/> executions in
/// each period so one odd run can't flag it.
/// </summary>
public sealed record RegressionCriteria(double ThresholdPct, long MinExecutions)
{
    public const double DefaultThresholdPct  = 50;
    public const long   DefaultMinExecutions = 5;

    public const double MaxThresholdPct  = 100_000;
    public const long   MaxMinExecutions = 1_000_000;

    /// <summary>
    /// A metric whose recent average is under this many milliseconds is never flagged: going
    /// from 0.1 ms to 0.3 ms is +200 % but nothing a DBA needs to look at.
    /// </summary>
    public const double MinRecentAverageMs = 1;

    public static RegressionCriteria Default { get; } = new(DefaultThresholdPct, DefaultMinExecutions);

    /// <summary>A saved threshold, or the default when it is missing or out of range.</summary>
    public static double NormalizeThreshold(double pct) =>
        double.IsFinite(pct) && pct > 0 && pct <= MaxThresholdPct ? pct : DefaultThresholdPct;

    /// <summary>A saved minimum, or the default when it is missing or out of range.</summary>
    public static long NormalizeMinExecutions(long executions) =>
        executions is >= 1 and <= MaxMinExecutions ? executions : DefaultMinExecutions;

    /// <summary>
    /// Reads what the user typed. Returns false with a message to show when it can't be used.
    /// </summary>
    public static bool TryParse(string thresholdText, string minExecutionsText,
                                out RegressionCriteria? criteria, out string error)
    {
        criteria = null;
        var culture = CultureInfo.CurrentCulture;

        if (!double.TryParse(thresholdText.Trim().TrimEnd('%').Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                             culture, out var pct)
            || !double.IsFinite(pct) || pct <= 0 || pct > MaxThresholdPct)
        {
            error = string.Format(culture, "Enter the increase to flag as a percentage above 0, up to {0:N0}.", MaxThresholdPct);
            return false;
        }

        if (!long.TryParse(minExecutionsText.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, culture, out var min)
            || min < 1 || min > MaxMinExecutions)
        {
            error = string.Format(culture, "Enter the minimum executions as a whole number from 1 to {0:N0}.", MaxMinExecutions);
            return false;
        }

        criteria = new RegressionCriteria(pct, min);
        error    = string.Empty;
        return true;
    }
}

/// <summary>
/// Finds the queries that got slower: compares each query's average duration and CPU per
/// execution in the recent period with the baseline. Works the same on Query Store and on the
/// monitoring history; only Query Store knows plans, so only it can say a plan is new.
/// </summary>
public static class QueryRegressionDetector
{
    /// <summary>Most regressions returned, the costliest first.</summary>
    public const int MaxResults = 200;

    /// <summary>
    /// The regressed queries, the one costing the most extra time first: how much longer (in
    /// duration or CPU, whichever regressed more) its recent executions took than they would
    /// have at the baseline average.
    /// </summary>
    public static IReadOnlyList<QueryRegression> Detect(IEnumerable<QueryPeriodStats> stats, RegressionCriteria criteria)
    {
        var result = new List<QueryRegression>();

        foreach (var query in stats.GroupBy(s => s.QueryKey))
        {
            var baseline = query.Where(s => !s.IsRecent).ToList();
            var recent   = query.Where(s => s.IsRecent).ToList();

            long baselineExecutions = baseline.Sum(s => s.Executions);
            long recentExecutions   = recent.Sum(s => s.Executions);
            if (baselineExecutions < criteria.MinExecutions || recentExecutions < criteria.MinExecutions)
                continue;

            var baselineDuration = baseline.Sum(s => s.DurationUs) / baselineExecutions / 1000;
            var recentDuration   = recent.Sum(s => s.DurationUs)   / recentExecutions   / 1000;
            var baselineCpu      = baseline.Sum(s => s.CpuUs)      / baselineExecutions / 1000;
            var recentCpu        = recent.Sum(s => s.CpuUs)        / recentExecutions   / 1000;

            var durationChange = Change(baselineDuration, recentDuration);
            var cpuChange      = Change(baselineCpu, recentCpu);

            var durationRegressed = Regressed(durationChange, recentDuration, criteria);
            var cpuRegressed      = Regressed(cpuChange, recentCpu, criteria);
            if (!durationRegressed && !cpuRegressed)
                continue;

            var extraMs = Math.Max(
                durationRegressed ? (recentDuration - baselineDuration) * recentExecutions : 0,
                cpuRegressed      ? (recentCpu      - baselineCpu)      * recentExecutions : 0);

            var recentPlan   = MostUsedPlan(recent);
            var baselinePlan = MostUsedPlan(baseline);
            var newPlan      = recentPlan is { } p && baseline.All(s => s.PlanId != p);

            result.Add(new QueryRegression(
                query.Key,
                baselineExecutions, recentExecutions,
                baselineDuration, recentDuration, durationChange,
                baselineCpu, recentCpu, cpuChange,
                durationRegressed, cpuRegressed,
                baselinePlan, recentPlan, newPlan,
                extraMs));
        }

        return result
            .OrderByDescending(r => r.ExtraTimeMs)
            .ThenBy(r => r.QueryKey, StringComparer.Ordinal)
            .Take(MaxResults)
            .ToList();
    }

    /// <summary>The per-hash totals of the two history periods, as the detector takes them.</summary>
    public static IEnumerable<QueryPeriodStats> FromHistory(
        IEnumerable<HistoryQueryTotals> baseline, IEnumerable<HistoryQueryTotals> recent) =>
        baseline.Select(q => ToStats(q, isRecent: false))
                .Concat(recent.Select(q => ToStats(q, isRecent: true)));

    private static QueryPeriodStats ToStats(HistoryQueryTotals q, bool isRecent) =>
        new(q.QueryHash, isRecent, null, q.Executions, q.ElapsedTimeUs, q.WorkerTimeUs);

    // Percent change, or null when the baseline was zero and no percentage means anything.
    private static double? Change(double before, double after) =>
        before > 0 ? (after - before) / before * 100 : null;

    private static bool Regressed(double? changePct, double recentAverageMs, RegressionCriteria criteria) =>
        changePct > criteria.ThresholdPct && recentAverageMs >= RegressionCriteria.MinRecentAverageMs;

    // The plan that ran most in the period; the lowest id on a tie, so the answer is stable.
    private static long? MostUsedPlan(IEnumerable<QueryPeriodStats> period) =>
        period.Where(s => s.PlanId is not null)
              .GroupBy(s => s.PlanId!.Value)
              .OrderByDescending(g => g.Sum(s => s.Executions))
              .ThenBy(g => g.Key)
              .Select(g => (long?)g.Key)
              .FirstOrDefault();
}
