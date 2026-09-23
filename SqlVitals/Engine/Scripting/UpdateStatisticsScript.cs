using System.Text;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Scripting;

/// <summary>How much of the table UPDATE STATISTICS reads to rebuild the histogram.</summary>
public enum StatisticsSampling
{
    /// <summary>No WITH clause: SQL Server picks the sample size (or uses a persisted sample percent).</summary>
    Default,

    /// <summary>WITH FULLSCAN: every row is read. The most accurate histogram, and the most I/O.</summary>
    FullScan,
}

/// <summary>What the script will touch — for the warning above the preview.</summary>
public readonly record struct UpdateStatisticsSummary(int Statistics, int Tables, long RowsInTables);

/// <summary>
/// Builds a reviewable T-SQL UPDATE STATISTICS script from the Stale Statistics grid. The
/// script is only generated, never executed: the DBA runs it after reviewing it.
/// </summary>
public static class UpdateStatisticsScript
{
    public static UpdateStatisticsSummary Summarize(IEnumerable<StaleStatistic> statistics)
    {
        var list = Distinct(statistics);
        return new UpdateStatisticsSummary(
            list.Count,
            list.Select(TableKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            list.Sum(s => s.RowsInTable));
    }

    public static string Build(IEnumerable<StaleStatistic> statistics, StatisticsSampling sampling, DateTime generatedAt)
    {
        var list    = Distinct(statistics);
        var summary = Summarize(list);

        var sb = new StringBuilder();

        sb.AppendLine("/*");
        sb.AppendLine("  SqlVitals — UPDATE STATISTICS script for stale statistics");
        sb.AppendLine($"  Generated:  {generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Statistics: {summary.Statistics} on {summary.Tables} table(s)");
        sb.AppendLine($"  Sampling:   {(sampling == StatisticsSampling.FullScan ? "FULLSCAN — every row is read" : "default — SQL Server chooses the sample size")}");
        sb.AppendLine();
        sb.AppendLine("  REVIEW BEFORE RUNNING:");
        if (sampling == StatisticsSampling.FullScan)
        {
            sb.AppendLine("  - FULLSCAN reads the whole table once per statistic. On large tables that is a lot");
            sb.AppendLine($"    of I/O (about {summary.RowsInTables:N0} rows in total here) — run it off-peak.");
        }
        else
        {
            sb.AppendLine("  - Default sampling is fast but reads only part of each table, so skewed data can");
            sb.AppendLine("    still get a poor histogram. If a statistic was last built with a persisted sample");
            sb.AppendLine("    percent, that percent is used again.");
            sb.AppendLine("  - Statistics last built with a full scan are flagged below: a sampled update");
            sb.AppendLine("    replaces that histogram with a less accurate one.");
        }
        sb.AppendLine("  - Updating a statistic invalidates the cached plans that use it, so the next run of");
        sb.AppendLine("    each affected query recompiles. Expect a short CPU bump afterwards.");
        sb.AppendLine("  - Each update holds a schema stability lock while it runs, so DDL on the table (an");
        sb.AppendLine("    index rebuild, ALTER TABLE) waits for it to finish.");
        sb.AppendLine("  - Stale statistics that keep coming back are better handled by AUTO_UPDATE_STATISTICS");
        sb.AppendLine("    (with AUTO_UPDATE_STATISTICS_ASYNC) or a scheduled maintenance job.");
        sb.AppendLine("*/");

        if (list.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- No statistics to update.");
            return sb.ToString();
        }

        string withClause = sampling == StatisticsSampling.FullScan ? " WITH FULLSCAN" : "";

        foreach (var db in list.GroupBy(s => s.DatabaseName))
        {
            sb.AppendLine();
            sb.AppendLine($"USE {UnusedIndexDropScript.QuoteName(db.Key)};");
            sb.AppendLine("GO");

            foreach (var st in db)
            {
                var table = $"{UnusedIndexDropScript.QuoteName(st.SchemaName)}.{UnusedIndexDropScript.QuoteName(st.TableName)}";
                var name  = UnusedIndexDropScript.QuoteName(st.StatisticsName);

                sb.AppendLine();
                sb.AppendLine($"-- {table}.{name}");
                sb.AppendLine($"-- Modifications: {st.ModificationsSinceLastUpdate:N0}  Rows: {st.RowsInTable:N0}  " +
                              $"Last updated: {(st.LastUpdated is { } d ? $"{d:yyyy-MM-dd} ({st.DaysOld:N0} days)" : "never")}  " +
                              $"Last sample: {st.SamplePct:0.#}%");

                if (sampling == StatisticsSampling.Default && st.SamplePct >= 100)
                    sb.AppendLine("-- NOTE: last built with a full scan — a sampled update lowers its accuracy.");
                if (st.IsIncremental)
                    sb.AppendLine("-- NOTE: incremental statistic — this updates every partition. WITH RESAMPLE ON PARTITIONS (...) refreshes only the ones that changed.");

                sb.AppendLine(Guard(table, st.StatisticsName));
                sb.AppendLine($"    UPDATE STATISTICS {table} ({name}){withClause};");
            }

            sb.AppendLine("GO");
        }

        return sb.ToString();
    }

    // The grid should not repeat a statistic, but a stat named twice would only be scanned twice.
    private static List<StaleStatistic> Distinct(IEnumerable<StaleStatistic> statistics) =>
        statistics
            .DistinctBy(s => TableKey(s) + '\u0001' + s.StatisticsName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string TableKey(StaleStatistic s) => string.Join('\u0001', s.DatabaseName, s.SchemaName, s.TableName);

    // The statistic may have been dropped (with its index, or by hand) since the grid was
    // loaded, and the script is meant to survive being saved and run later.
    private static string Guard(string table, string statisticsName) =>
        $"IF EXISTS (SELECT 1 FROM sys.stats WHERE object_id = OBJECT_ID({Literal(table)}) AND name = {Literal(statisticsName)})";

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
