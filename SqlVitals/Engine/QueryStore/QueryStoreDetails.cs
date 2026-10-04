using System.Globalization;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.QueryStore;

/// <summary>
/// What the details drawer says about a Query Store row (#147): its figures from the tab it was
/// selected on, and a line per plan. Pure.
/// </summary>
public static class QueryStoreDetails
{
    /// <summary>The selected row's query: its id, the plan it used most (or the forced one), and its text.</summary>
    public static (long QueryId, long? PlanId, string QueryText)? QueryOf(object? row) => row switch
    {
        QueryStoreQueryRow r     => (r.QueryId, r.TopPlanId, r.QueryText),
        QueryStoreVariationRow r => (r.QueryId, r.TopPlanId, r.QueryText),
        QueryStoreWaitQuery r    => (r.QueryId, r.TopPlanId, r.QueryText),
        QueryStoreForcedPlan r   => (r.QueryId, r.PlanId, r.QueryText),
        QueryStoreHint r         => (r.QueryId, null, r.QueryText),
        _                        => null,
    };

    /// <summary>The row's figures, as its tab shows them; empty for a row that isn't a query's.</summary>
    /// <param name="period">"Last 24 hours": the period the figures cover.</param>
    public static IReadOnlyList<DetailRow> Rows(object? row, string period)
    {
        var c = CultureInfo.CurrentCulture;
        return row switch
        {
            QueryStoreQueryRow r =>
            [
                new("Object", r.ObjectText),
                new("Period", period),
                new("Executions", r.Executions.ToString("N0", c)),
                new("Plans", r.Plans.ToString("N0", c), Warning: r.Plans > 1),
                new("Failed", r.FailedExecutions.ToString("N0", c), Warning: r.FailedExecutions > 0),
                new("Avg duration", Ms(r.AvgDurationMs)),
                new("Total CPU", Ms(r.TotalCpuMs)),
                new("Total reads", r.TotalLogicalReads.ToString("N0", c)),
                .. r.ForcedPlanId is { } forced ? [new DetailRow("Forced plan", $"Plan {forced}")] : Array.Empty<DetailRow>(),
                .. r.LastExecutionLocal is { } last ? [new DetailRow("Last executed", last.ToString("d MMM HH:mm:ss", c))] : Array.Empty<DetailRow>(),
            ],
            QueryStoreVariationRow r =>
            [
                new("Object", r.ObjectText),
                new("Period", period),
                new("Executions", r.Executions.ToString("N0", c)),
                new("Plans", r.Plans.ToString("N0", c), Warning: r.Plans > 1),
                new("Average", r.Avg.ToString("N1", c)),
                new("Min · max", $"{r.Min.ToString("N1", c)} · {r.Max.ToString("N1", c)}"),
                new("Std deviation", r.StdDev.ToString("N1", c), Warning: r.Avg > 0 && r.StdDev > r.Avg),
            ],
            QueryStoreWaitQuery r =>
            [
                new("Object", r.ObjectText),
                new("Period", period),
                new("Executions", r.Executions.ToString("N0", c)),
                new("Waited", Ms(r.WaitMs)),
                .. r.AvgWaitMs is { } avg ? [new DetailRow("Per execution", Ms(avg))] : Array.Empty<DetailRow>(),
                new("Of the category", r.PctOfCategory.ToString("N1", c) + " %"),
            ],
            QueryStoreForcedPlan r =>
            [
                new("Object", r.ObjectText),
                new("Forced plan", $"Plan {r.PlanId} · forced by {r.ForcedBy}"),
                new("Force failures", r.ForceFailures.ToString("N0", c), Warning: r.ForceFailures > 0),
                .. string.IsNullOrWhiteSpace(r.LastFailureReason) || r.LastFailureReason == "NONE"
                    ? Array.Empty<DetailRow>() : [new DetailRow("Last failure", r.LastFailureReason, Warning: true)],
                new("Executions", $"{r.Executions.ToString("N0", c)} in {period.ToLower(c)}"),
                .. r.AvgDurationMs is { } avg ? [new DetailRow("Avg duration", Ms(avg))] : Array.Empty<DetailRow>(),
            ],
            QueryStoreHint r =>
            [
                new("Hint", r.HintText),
                new("Source", r.Source),
                new("Failures", r.Failures.ToString("N0", c), Warning: r.Failures > 0),
                .. r.LastFailureText.Length > 0 ? [new DetailRow("Last failure", r.LastFailureText, Warning: true)] : Array.Empty<DetailRow>(),
            ],
            _ => [],
        };
    }

    /// <summary>"Plan 902 · forced": a plan's name in the drawer's list.</summary>
    public static string PlanTitle(QueryStorePlanSummary plan) =>
        plan.IsForced ? $"Plan {plan.PlanId} · forced" : $"Plan {plan.PlanId}";

    /// <summary>"200 runs · 2,050.0 ms avg · 100.0 ms CPU · last 4 Oct 15:12": its figures in the period.</summary>
    public static string PlanLine(QueryStorePlanSummary plan)
    {
        var c = CultureInfo.CurrentCulture;
        var parts = new List<string> { plan.Executions == 1 ? "1 run" : $"{plan.Executions.ToString("N0", c)} runs" };
        if (plan.AvgDurationMs is { } duration) parts.Add($"{Ms(duration)} avg");
        if (plan.AvgCpuMs is { } cpu)           parts.Add($"{Ms(cpu)} CPU");
        if (plan.Failed > 0)                    parts.Add($"{plan.Failed.ToString("N0", c)} failed");
        if (plan.LastUsedLocal is { } last)     parts.Add($"last {last.ToString("d MMM HH:mm", c)}");
        return string.Join(" · ", parts);
    }

    private static string Ms(double ms) => ms.ToString("N1", CultureInfo.CurrentCulture) + " ms";
}
