using System.Globalization;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Monitoring;

/// <summary>
/// The details drawer's rows (#149) for a row of Resource Queries, Query Regressions and SP Trace:
/// what the grid has, labelled, with the units said.
/// </summary>
public static class RowDetails
{
    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // Microseconds, as the DMVs count them.
    private static string Us(long microseconds) => microseconds >= 1_000_000
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.##} s", microseconds / 1_000_000.0)
        : string.Format(CultureInfo.CurrentCulture, "{0:N1} ms", microseconds / 1_000.0);

    private static string Ms(double ms) => string.Format(CultureInfo.CurrentCulture, "{0:N1} ms", ms);

    private static string When(DateTime time) => time == default ? "—" : time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>A query from the plan cache, as Resource Queries lists it.</summary>
    public static IReadOnlyList<DetailRow> Rows(ResourceIntensiveQuery q)
    {
        var avgOf = q.ExecutionCount > 0 ? q.ExecutionCount : 1;
        return
        [
            new("Database", q.DatabaseName ?? "—"),
            new("Object", string.IsNullOrWhiteSpace(q.ObjectName) ? "Ad hoc" : q.ObjectName + (string.IsNullOrWhiteSpace(q.ObjectType) ? "" : $" ({q.ObjectType})")),
            new("Executions", N(q.ExecutionCount)),
            new("CPU", $"{Us(q.TotalCPUTime)} in all, {Us(q.AvgCPUTime)} a run"),
            new("Elapsed", $"{Us(q.TotalElapsedTime)} in all, {Us(q.AvgElapsedTime)} a run"),
            new("Logical reads", $"{N(q.TotalLogicalReads)} in all, {N(q.AvgLogicalReads)} a run"
                                 + (q.LastLogicalReads is { } last ? $", {N(last)} the last run" : "")),
            new("Physical reads", $"{N(q.TotalPhysicalReads)} in all, {N(q.TotalPhysicalReads / avgOf)} a run"),
            new("Writes", $"{N(q.TotalLogicalWrites)} in all"),
            new("Last run", When(q.LastExecutionTime)),
            new("Plan cached", When(q.PlanCreationTime)),
        ];
    }

    /// <summary>A regressed query: before and now side by side.</summary>
    public static IReadOnlyList<DetailRow> Rows(QueryRegression r)
    {
        static string Change(double? pct) => pct is { } p ? string.Format(CultureInfo.CurrentCulture, " ({0:+#,0;-#,0;0} %)", p) : "";
        return
        [
            new("Query", r.QueryStoreId is { } id ? $"Query Store query {id}" : $"Query hash {r.QueryKey}"),
            new("Database", r.DatabaseName ?? "—"),
            new("Slower in", r.RegressedOn, Warning: true),
            new("Duration", $"{Ms(r.BaselineAvgDurationMs)} before, {Ms(r.RecentAvgDurationMs)} now{Change(r.DurationChangePct)}", r.DurationRegressed),
            new("CPU", $"{Ms(r.BaselineAvgCpuMs)} before, {Ms(r.RecentAvgCpuMs)} now{Change(r.CpuChangePct)}", r.CpuRegressed),
            new("Runs", $"{N(r.BaselineExecutions)} before, {N(r.RecentExecutions)} now"),
            new("Extra time", string.Format(CultureInfo.CurrentCulture, "{0:N0} ms over the recent runs", r.ExtraTimeMs)),
            new("Plan", r.PlanChange.Length > 0
                ? $"{r.PlanChange}: {Plan(r.BaselinePlanId)} before, {Plan(r.RecentPlanId)} now"
                : r.RecentPlanId is null ? "The monitoring history keeps no plan ids" : $"The same plan, {Plan(r.RecentPlanId)}",
                r.PlanChange.Length > 0),
        ];
    }

    private static string Plan(long? id) => id is { } p ? $"plan {p}" : "no plan";

    /// <summary>One captured stored-procedure call.</summary>
    public static IReadOnlyList<DetailRow> Rows(SpTraceEvent call) =>
    [
        new("Time", call.EventTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture)),
        new("Procedure", string.IsNullOrWhiteSpace(call.ObjectName) ? "—" : call.ObjectName),
        new("Event", call.EventName),
        new("Database", call.DatabaseName ?? "—"),
        new("Duration", $"{N(call.DurationMs)} ms"),
        new("CPU", $"{N(call.CpuTimeMs)} ms"),
        new("Reads", $"{N(call.LogicalReads)} logical, {N(call.PhysicalReads)} physical"),
        new("Writes", N(call.Writes)),
        new("Rows", N(call.RowsReturned)),
        new("Session", call.SessionId.ToString(CultureInfo.CurrentCulture)),
        new("Login", call.LoginName ?? "—"),
        new("Application", call.ClientAppName ?? "—"),
        new("Host", call.ClientHostName ?? "—"),
    ];
}
