using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Turns two consecutive <c>sys.dm_exec_procedure_stats</c> snapshots into per-window
/// activity. The DMV exposes cumulative totals since the plan was cached, so "what ran
/// in the last N seconds" only exists as the difference between two reads.
/// <para>Pure and static so it is unit-testable without a database.</para>
/// </summary>
public static class ProcedureStatsDelta
{
    /// <summary>
    /// Computes per-procedure activity between two snapshots, keeping only procedures
    /// that actually executed in the window.
    /// </summary>
    /// <param name="previous">The earlier snapshot. Pass an empty set for the first poll.</param>
    /// <param name="current">The snapshot just taken.</param>
    public static IReadOnlyList<SpAggregateRow> Compute(
        IReadOnlyDictionary<ProcStatsKey, ProcStatsSnapshot> previous,
        IReadOnlyDictionary<ProcStatsKey, ProcStatsSnapshot> current)
    {
        // The first poll has no baseline, so every procedure would otherwise report its
        // entire lifetime history as if it had just happened. Report nothing instead.
        if (previous.Count == 0) return [];

        var rows = new List<SpAggregateRow>();

        foreach (var (key, now) in current)
        {
            // A key absent from the baseline is a plan cached since the last poll, so its
            // totals are all new activity and the delta is the full current value.
            var baseline = previous.TryGetValue(key, out var before) ? before : null;

            var execDelta = Delta(now.ExecutionCount, baseline?.ExecutionCount);
            if (execDelta <= 0) continue;

            var elapsedUsDelta = Delta(now.TotalElapsedTimeUs, baseline?.TotalElapsedTimeUs);
            var workerUsDelta  = Delta(now.TotalWorkerTimeUs,  baseline?.TotalWorkerTimeUs);
            var readsDelta     = Delta(now.TotalLogicalReads,  baseline?.TotalLogicalReads);

            var totalDurationMs = elapsedUsDelta / 1000.0;
            var totalCpuMs      = workerUsDelta  / 1000.0;

            rows.Add(new SpAggregateRow(
                DatabaseName:      now.DatabaseName,
                SchemaName:        now.SchemaName,
                ProcedureName:     now.ProcedureName,
                ExecCount:         execDelta,
                AvgDurationMs:     totalDurationMs / execDelta,
                // max_elapsed_time is a running high-water mark, not a windowed value —
                // it is reported as-is and labelled "max ever" in the UI.
                MaxDurationMs:     now.MaxElapsedTimeUs / 1000.0,
                TotalDurationMs:   totalDurationMs,
                AvgCpuMs:          totalCpuMs / execDelta,
                AvgLogicalReads:   readsDelta / execDelta,
                TotalLogicalReads: readsDelta,
                LastExecutionTime: now.LastExecutionTime));
        }

        return rows
            .OrderByDescending(r => r.TotalDurationMs)
            .ToList();
    }

    /// <summary>
    /// Difference against a baseline, clamped at zero. Counters can still move backwards
    /// despite <c>CachedTime</c> being part of the key — for example when the DMV is
    /// cleared by DBCC FREEPROCCACHE between polls.
    /// </summary>
    private static long Delta(long current, long? baseline) =>
        Math.Max(0, current - (baseline ?? 0));
}
