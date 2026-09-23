using System.Text;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Scripting;

/// <summary>
/// Thresholds and server capabilities the maintenance script is built against. The defaults
/// are the conventional 5 / 30% split; both are configurable because the right numbers depend
/// on the workload and on the storage underneath it.
/// </summary>
public sealed record IndexMaintenanceOptions
{
    /// <summary>At or above this fragmentation an index is REORGANIZE'd. Below it, nothing is done.</summary>
    public double ReorganizeThresholdPercent { get; init; } = 5;

    /// <summary>At or above this fragmentation an index is REBUILD'd instead of REORGANIZE'd.</summary>
    public double RebuildThresholdPercent { get; init; } = 30;

    /// <summary>Indexes with fewer pages than this are skipped — fragmentation there is noise.</summary>
    public long MinPageCount { get; init; } = 1000;

    /// <summary>
    /// Add WITH (ONLINE = ON) to REBUILD. Only set this when the server's edition supports it
    /// (see <see cref="IndexMaintenanceScript.EditionSupportsOnlineRebuild"/>) — the statement
    /// fails outright on editions that don't.
    /// </summary>
    public bool OnlineRebuild { get; init; }
}

/// <summary>How many indexes a set of options would rebuild, reorganize and leave alone.</summary>
public readonly record struct IndexMaintenanceSummary(int Rebuild, int Reorganize, int Skipped);

/// <summary>
/// Builds a reviewable T-SQL maintenance script — ALTER INDEX ... REORGANIZE / REBUILD — from
/// fragmentation results. The script is only generated, never executed: the DBA runs it after
/// reviewing it, in a window they choose.
/// </summary>
public static class IndexMaintenanceScript
{
    private enum Action { Rebuild, Reorganize, SkipHeap, SkipSmall, SkipBelowThreshold }

    /// <summary>
    /// SERVERPROPERTY('EngineEdition') values that accept WITH (ONLINE = ON) on a rebuild:
    /// 3 = Enterprise (also Developer and Evaluation), 5 = Azure SQL Database,
    /// 8 = Azure SQL Managed Instance. Standard (2), Express (4) and the rest reject it.
    /// </summary>
    public static bool EditionSupportsOnlineRebuild(int engineEdition) => engineEdition is 3 or 5 or 8;

    /// <summary>What the script will do, without building it — for the warning above the preview.</summary>
    public static IndexMaintenanceSummary Summarize(IEnumerable<IndexFragmentation> indexes, IndexMaintenanceOptions options)
    {
        var actions = Plan(indexes, options).Select(p => p.Action).ToList();
        return new IndexMaintenanceSummary(
            actions.Count(a => a == Action.Rebuild),
            actions.Count(a => a == Action.Reorganize),
            actions.Count(a => a is not (Action.Rebuild or Action.Reorganize)));
    }

    // dm_db_index_physical_stats reports one row per partition, so a partitioned index arrives
    // several times. ALTER INDEX without a PARTITION clause covers every partition, so keep the
    // worst row and act on it once.
    private static List<(IndexFragmentation Index, Action Action)> Plan(
        IEnumerable<IndexFragmentation> indexes, IndexMaintenanceOptions options) =>
        indexes
            .GroupBy(i => string.Join('\u0001', i.DatabaseName, i.SchemaName, i.TableName, i.IndexName),
                     StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(i => i.FragmentationPercent)!)
            .Select(i => (Index: i, Action: Classify(i, options)))
            .ToList();

    public static string Build(IEnumerable<IndexFragmentation> indexes, IndexMaintenanceOptions options, DateTime generatedAt)
    {
        var plan    = Plan(indexes, options);
        var summary = new IndexMaintenanceSummary(
            plan.Count(p => p.Action == Action.Rebuild),
            plan.Count(p => p.Action == Action.Reorganize),
            plan.Count(p => p.Action is not (Action.Rebuild or Action.Reorganize)));

        var sb = new StringBuilder();

        sb.AppendLine("/*");
        sb.AppendLine("  SqlVitals — index maintenance script (REORGANIZE / REBUILD)");
        sb.AppendLine($"  Generated: {generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Indexes:   {plan.Count} — {summary.Rebuild} to rebuild, " +
                      $"{summary.Reorganize} to reorganize, {summary.Skipped} skipped");
        sb.AppendLine();
        sb.AppendLine("  Thresholds used:");
        sb.AppendLine($"  - REORGANIZE from {options.ReorganizeThresholdPercent:0.##}% fragmentation");
        sb.AppendLine($"  - REBUILD from {options.RebuildThresholdPercent:0.##}%");
        sb.AppendLine($"  - Indexes under {options.MinPageCount:N0} pages are skipped");
        sb.AppendLine($"  - ONLINE = ON: {(options.OnlineRebuild ? "yes, on rowstore rebuilds" : "no")}");
        sb.AppendLine();
        sb.AppendLine("  REVIEW BEFORE RUNNING:");
        sb.AppendLine("  - Run this in a maintenance window. A REBUILD without ONLINE = ON holds a schema");
        sb.AppendLine("    modification lock for the whole operation — the table is unavailable until it ends.");
        sb.AppendLine("    REORGANIZE is always online, but it is single-threaded and can take longer.");
        sb.AppendLine("  - Both generate transaction log. Under FULL recovery a large rebuild can fill the log;");
        sb.AppendLine("    check free space and how often the log is backed up first.");
        sb.AppendLine("  - REBUILD updates statistics with a full scan as a side effect. REORGANIZE does not, so");
        sb.AppendLine("    follow it with UPDATE STATISTICS where plans depend on fresh statistics.");
        sb.AppendLine("  - Fragmentation matters far less on SSD, and on indexes that are only ever seeked.");
        sb.AppendLine("    Rebuilding everything on a schedule usually costs more than it returns.");
        sb.AppendLine("  - The statements run one after another with no time budget. Add batching if the");
        sb.AppendLine("    window is tight.");
        sb.AppendLine("*/");

        if (plan.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- No fragmented indexes to maintain.");
            return sb.ToString();
        }

        foreach (var db in plan.GroupBy(p => p.Index.DatabaseName))
        {
            sb.AppendLine();
            sb.AppendLine($"USE {UnusedIndexDropScript.QuoteName(db.Key)};");
            sb.AppendLine("GO");

            foreach (var (ix, action) in db)
            {
                var table = $"{UnusedIndexDropScript.QuoteName(ix.SchemaName)}.{UnusedIndexDropScript.QuoteName(ix.TableName)}";

                sb.AppendLine();
                sb.AppendLine($"-- {table}.{(string.IsNullOrEmpty(ix.IndexName) ? "(heap)" : UnusedIndexDropScript.QuoteName(ix.IndexName))}  ({ix.IndexType})");
                sb.AppendLine($"-- Fragmentation: {ix.FragmentationPercent:N1}%  Pages: {ix.PageCount:N0}  " +
                              $"Page density: {ix.AvgPageSpaceUsed:N1}%  Rows: {ix.RecordCount:N0}");

                switch (action)
                {
                    case Action.SkipHeap:
                        sb.AppendLine("-- SKIPPED: a heap has no index to alter. ALTER TABLE ... REBUILD defragments one,");
                        sb.AppendLine("--          but it also rebuilds every nonclustered index on the table.");
                        sb.AppendLine($"-- ALTER TABLE {table} REBUILD;");
                        break;

                    case Action.SkipSmall:
                        sb.AppendLine($"-- SKIPPED: {ix.PageCount:N0} pages is below the {options.MinPageCount:N0} page minimum — small");
                        sb.AppendLine("--          indexes sit in mixed extents and cannot be meaningfully defragmented.");
                        break;

                    case Action.SkipBelowThreshold:
                        sb.AppendLine($"-- SKIPPED: {ix.FragmentationPercent:N1}% is below the " +
                                      $"{options.ReorganizeThresholdPercent:0.##}% reorganize threshold.");
                        break;

                    case Action.Reorganize:
                        sb.AppendLine(Guard(table, ix.IndexName!));
                        sb.AppendLine($"    ALTER INDEX {UnusedIndexDropScript.QuoteName(ix.IndexName!)} ON {table} REORGANIZE;");
                        break;

                    case Action.Rebuild:
                        bool online = options.OnlineRebuild && TypeSupportsOnline(ix.IndexType);
                        if (options.OnlineRebuild && !online)
                            sb.AppendLine($"-- NOTE: ONLINE = ON is not available for a {ix.IndexType} — this one rebuilds offline.");
                        sb.AppendLine(Guard(table, ix.IndexName!));
                        sb.AppendLine($"    ALTER INDEX {UnusedIndexDropScript.QuoteName(ix.IndexName!)} ON {table} REBUILD" +
                                      (online ? " WITH (ONLINE = ON)" : "") + ";");
                        break;
                }
            }

            sb.AppendLine("GO");
        }

        return sb.ToString();
    }

    private static Action Classify(IndexFragmentation ix, IndexMaintenanceOptions options) =>
        string.IsNullOrEmpty(ix.IndexName)                              ? Action.SkipHeap
        : ix.PageCount < options.MinPageCount                           ? Action.SkipSmall
        : ix.FragmentationPercent >= options.RebuildThresholdPercent    ? Action.Rebuild
        : ix.FragmentationPercent >= options.ReorganizeThresholdPercent ? Action.Reorganize
        : Action.SkipBelowThreshold;

    /// <summary>
    /// ONLINE = ON covers rowstore indexes. XML, spatial and columnstore indexes reject it (a
    /// clustered columnstore only takes it from SQL Server 2019 on), so those rebuild offline.
    /// </summary>
    private static bool TypeSupportsOnline(string indexType) =>
        indexType.Equals("CLUSTERED INDEX", StringComparison.OrdinalIgnoreCase)
        || indexType.Equals("NONCLUSTERED INDEX", StringComparison.OrdinalIgnoreCase);

    // The index may have been dropped or renamed since the grid was loaded, and the script is
    // meant to survive being saved and run later.
    private static string Guard(string table, string indexName) =>
        $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({Literal(table)}) AND name = {Literal(indexName)})";

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
