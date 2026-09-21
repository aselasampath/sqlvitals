using System.Text;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Scripting;

/// <summary>
/// Builds a reviewable T-SQL script that drops unused indexes. The script is only generated,
/// never executed — the DBA runs it after reviewing it.
/// </summary>
public static class UnusedIndexDropScript
{
    public static string Build(IEnumerable<UnusedIndex> indexes, DateTime generatedAt)
    {
        var list = indexes.ToList();
        var sb = new StringBuilder();

        sb.AppendLine("/*");
        sb.AppendLine("  SqlVitals — DROP script for unused indexes");
        sb.AppendLine($"  Generated: {generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Indexes:   {list.Count}");
        sb.AppendLine();
        sb.AppendLine("  REVIEW BEFORE RUNNING:");
        sb.AppendLine("  - Usage stats reset when SQL Server restarts (or the database goes offline). An index");
        sb.AppendLine("    with zero reads since a recent restart may still be needed by month-end or");
        sb.AppendLine("    quarterly jobs. Check the uptime below covers a full business cycle.");
        sb.AppendLine("  - Queries or plan guides that name an index in a hint (WITH (INDEX(...))) fail once");
        sb.AppendLine("    it is dropped.");
        sb.AppendLine("  - UNIQUE indexes enforce data rules and may back foreign keys, so they are left");
        sb.AppendLine("    commented out. Uncomment one only if you are sure it is safe to remove.");
        sb.AppendLine("  - A DROP loses the index definition. Script it out first, or use");
        sb.AppendLine("    ALTER INDEX ... DISABLE instead to keep it for an easy ALTER INDEX ... REBUILD.");
        sb.AppendLine("*/");
        sb.AppendLine();
        sb.AppendLine("SELECT sqlserver_start_time, DATEDIFF(DAY, sqlserver_start_time, GETDATE()) AS uptime_days");
        sb.AppendLine("FROM sys.dm_os_sys_info;");
        sb.AppendLine("GO");

        if (list.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- No unused indexes to drop.");
            return sb.ToString();
        }

        foreach (var db in list.GroupBy(i => i.DatabaseName))
        {
            sb.AppendLine();
            sb.AppendLine($"USE {QuoteName(db.Key)};");
            sb.AppendLine("GO");

            foreach (var ix in db)
            {
                var table = $"{QuoteName(ix.SchemaName)}.{QuoteName(ix.TableName)}";
                string[] guardAndDrop =
                [
                    $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({Literal(table)}) AND name = {Literal(ix.IndexName)})",
                    $"    DROP INDEX {QuoteName(ix.IndexName)} ON {table};",
                ];

                sb.AppendLine();
                sb.AppendLine($"-- {table}.{QuoteName(ix.IndexName)}  ({ix.IndexType}{(ix.IsUnique ? ", UNIQUE" : "")})");
                sb.AppendLine($"-- Reads: {ix.TotalReads:N0}  Writes: {ix.UserUpdates:N0}  Rows: {ix.TableRows:N0}");

                if (ix.IsUnique)
                {
                    sb.AppendLine("-- SKIPPED: unique index — may enforce a business rule or back a foreign key.");
                    foreach (var line in guardAndDrop)
                        sb.AppendLine("-- " + line);
                }
                else
                {
                    foreach (var line in guardAndDrop)
                        sb.AppendLine(line);
                }
            }

            sb.AppendLine("GO");
        }

        return sb.ToString();
    }

    /// <summary>T-SQL QUOTENAME equivalent: wraps in brackets and escapes closing brackets.</summary>
    public static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
