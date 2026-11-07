using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Engine.ExecutionPlans;

/// <summary>
/// The table an operator of a plan reads (#244), for its link to Table Statistics. Pure.
/// </summary>
public static class PlanTableTarget
{
    /// <summary>
    /// Where the operator's link opens; null when it isn't on a table Table Statistics lists: an
    /// operator with no object, a table variable, a temporary table or a system table. It opens on the
    /// operator's own index's statistics if the optimizer used them, else on the first the plan lists
    /// for the table, and on the table alone when the plan lists none.
    /// </summary>
    public static TableStatisticsTarget? For(ExecutionPlan plan, PlanOperator op)
    {
        if (op.ObjectTable is not { Length: > 0 } table || op.ObjectSchema is not { Length: > 0 } schema || !IsUserTable(op.ObjectDatabase, schema, table))
            return null;

        var used = plan.Statements.FirstOrDefault(s => s.Root.Descendants().Contains(op))?.StatisticsUsed
                                  .Where(s => Same(s.Table, table) && Same(s.Schema, schema) && Same(s.Database, op.ObjectDatabase))
                                  .ToList() ?? [];
        var statistics = used.FirstOrDefault(s => Same(s.Statistics, op.ObjectIndex)) ?? used.FirstOrDefault();
        return new TableStatisticsTarget(op.ObjectDatabase, schema, table, statistics?.Statistics);
    }

    private static bool IsUserTable(string? database, string schema, string table) =>
        !table.StartsWith('#') && !table.StartsWith('@')
        && !Same(schema, "sys") && !Same(schema, "INFORMATION_SCHEMA")
        && !Same(database, "tempdb") && !Same(database, "mssqlsystemresource");

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
