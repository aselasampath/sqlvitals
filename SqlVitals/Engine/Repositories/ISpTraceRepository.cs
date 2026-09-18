using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Live stored-procedure tracing. Prefers an app-managed Extended Events session for
/// per-call detail and falls back to sys.dm_exec_procedure_stats deltas when the login
/// cannot create event sessions.
/// </summary>
public interface ISpTraceRepository
{
    /// <summary>
    /// Starts a trace, choosing the capture mode from the server edition and the login's
    /// permissions. Never throws for a missing permission — returns a
    /// <see cref="TraceMode.DmvFallback"/> status carrying the reason instead.
    /// </summary>
    Task<TraceSessionStatus> StartTraceAsync(SpTraceOptions options);

    /// <summary>Stops the trace and drops the event session if this app created one.</summary>
    Task<TraceSessionStatus> StopTraceAsync();

    /// <summary>Current trace state, including ring-buffer dropped-event counters.</summary>
    Task<TraceSessionStatus> GetTraceStatusAsync();

    /// <summary>
    /// Returns calls captured since the previous poll. Empty in
    /// <see cref="TraceMode.DmvFallback"/> mode, which has no per-call data.
    /// </summary>
    Task<IReadOnlyList<SpTraceEvent>> PollTraceEventsAsync();

    /// <summary>
    /// Returns per-procedure activity since the previous poll, derived from
    /// sys.dm_exec_procedure_stats. The first poll after a start returns nothing — it
    /// establishes the baseline the next poll measures against.
    /// </summary>
    Task<IReadOnlyList<SpAggregateRow>> PollProcedureStatsAsync();

    /// <summary>
    /// Cumulative per-procedure totals since each plan was cached, heaviest first. Unlike
    /// <see cref="PollProcedureStatsAsync"/> this needs no baseline and no running trace,
    /// which is what makes it usable for a one-shot report.
    /// </summary>
    Task<IReadOnlyList<SpAggregateRow>> GetProcedureStatsTotalsAsync(int topN = 25);
}
