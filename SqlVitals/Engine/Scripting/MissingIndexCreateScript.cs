using System.Text;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Scripting;

/// <summary>
/// Builds a reviewable T-SQL script that creates the missing indexes SQL Server suggested.
/// The script is only generated, never executed — the DBA runs it after reviewing it.
/// </summary>
public static class MissingIndexCreateScript
{
    // sysname is nvarchar(128).
    private const int MaxNameLength = 128;

    public static string Build(IEnumerable<MissingIndex> suggestions, DateTime generatedAt)
    {
        var list = suggestions.ToList();
        var sb = new StringBuilder();

        sb.AppendLine("/*");
        sb.AppendLine("  SqlVitals — CREATE script for missing-index suggestions");
        sb.AppendLine($"  Generated: {generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Indexes:   {list.Count}");
        sb.AppendLine();
        sb.AppendLine("  REVIEW BEFORE RUNNING:");
        sb.AppendLine("  - These come from the optimizer's missing-index DMVs. SQL Server does not compare them");
        sb.AppendLine("    with existing indexes, so a suggestion may duplicate or overlap one you already have.");
        sb.AppendLine("    Widening an existing index is often better than adding a new one.");
        sb.AppendLine("  - Key columns are listed equality first, then inequality, in table column order —");
        sb.AppendLine("    not by selectivity. Put the most selective equality column first.");
        sb.AppendLine("  - Every index slows INSERT, UPDATE and DELETE and takes space. Wide INCLUDE lists");
        sb.AppendLine("    in particular can make an index nearly as large as the table.");
        sb.AppendLine("  - Building an index on a large table takes locks and log space. Run it in a");
        sb.AppendLine("    maintenance window, or add WITH (ONLINE = ON) where your edition supports it.");
        sb.AppendLine("  - Test on a non-production copy first. Index names are generated — rename to match");
        sb.AppendLine("    your conventions.");
        sb.AppendLine("*/");

        if (list.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- No missing-index suggestions to create.");
            return sb.ToString();
        }

        foreach (var db in list.GroupBy(i => i.DatabaseName))
        {
            // Several suggestions for one table can share key columns (different INCLUDEs),
            // so names are made unique per table within the script.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            sb.AppendLine();
            sb.AppendLine($"USE {UnusedIndexDropScript.QuoteName(db.Key)};");
            sb.AppendLine("GO");

            foreach (var ix in db)
            {
                sb.AppendLine();

                var keyColumns = JoinColumnLists(ix.EqualityColumns, ix.InequalityColumns);
                if (string.IsNullOrWhiteSpace(ix.SchemaName) || string.IsNullOrWhiteSpace(ix.TableName)
                    || keyColumns is null)
                {
                    sb.AppendLine($"-- SKIPPED: suggestion on {ix.SchemaName ?? "?"}.{ix.TableName ?? "?"} has no table name or key columns.");
                    continue;
                }

                var table = $"{UnusedIndexDropScript.QuoteName(ix.SchemaName)}.{UnusedIndexDropScript.QuoteName(ix.TableName)}";
                var name  = UniqueName(IndexName(ix), $"{ix.SchemaName}.{ix.TableName}", usedNames);

                sb.AppendLine($"-- {table}  ({ix.Severity})");
                sb.AppendLine($"-- Impact score: {ix.ImpactScore:N1}  Avg impact: {ix.AvgUserImpact:N1}%  Seeks: {ix.UserSeeks:N0}  Scans: {ix.UserScans:N0}");
                sb.AppendLine($"IF OBJECT_ID({Literal(table)}) IS NOT NULL");
                sb.AppendLine($"   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({Literal(table)}) AND name = {Literal(name)})");
                var create = $"    CREATE NONCLUSTERED INDEX {UnusedIndexDropScript.QuoteName(name)} ON {table} ({keyColumns})";
                if (string.IsNullOrWhiteSpace(ix.IncludedColumns))
                {
                    sb.AppendLine(create + ";");
                }
                else
                {
                    sb.AppendLine(create);
                    sb.AppendLine($"    INCLUDE ({ix.IncludedColumns.Trim()});");
                }
            }

            sb.AppendLine("GO");
        }

        return sb.ToString();
    }

    /// <summary>
    /// IX_&lt;Table&gt;_&lt;key columns&gt;, e.g. IX_Orders_CustomerId_OrderDate. Characters that
    /// aren't letters, digits or underscores become underscores so the name needs no quoting
    /// to read, and it is cut to fit sysname.
    /// </summary>
    public static string IndexName(MissingIndex ix)
    {
        var parts = new List<string> { "IX", ix.TableName };
        parts.AddRange(ParseColumns(ix.EqualityColumns));
        parts.AddRange(ParseColumns(ix.InequalityColumns));

        var name = string.Join("_", parts.Select(Sanitize).Where(p => p.Length > 0));
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    /// <summary>
    /// Splits a DMV column list such as "[CustomerId], [Order]]Date]" into unquoted names.
    /// </summary>
    public static IReadOnlyList<string> ParseColumns(string? columnList)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(columnList)) return result;

        var current = new StringBuilder();
        bool inBrackets = false;
        for (int i = 0; i < columnList.Length; i++)
        {
            char c = columnList[i];
            if (inBrackets)
            {
                if (c == ']')
                {
                    if (i + 1 < columnList.Length && columnList[i + 1] == ']')
                    {
                        current.Append(']');
                        i++;
                    }
                    else
                    {
                        inBrackets = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '[')
            {
                inBrackets = true;
            }
            else if (c == ',')
            {
                AddIfAny(result, current);
            }
            else if (!char.IsWhiteSpace(c))
            {
                current.Append(c);
            }
        }
        AddIfAny(result, current);
        return result;
    }

    private static void AddIfAny(List<string> result, StringBuilder current)
    {
        if (current.Length > 0) result.Add(current.ToString());
        current.Clear();
    }

    private static string? JoinColumnLists(string? equality, string? inequality)
    {
        var lists = new[] { equality, inequality }
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim())
            .ToList();
        return lists.Count == 0 ? null : string.Join(", ", lists);
    }

    private static string UniqueName(string baseName, string tableKey, HashSet<string> used)
    {
        var name = baseName;
        for (int n = 2; !used.Add(tableKey + "|" + name); n++)
        {
            var suffix = "_" + n;
            name = (baseName.Length + suffix.Length <= MaxNameLength
                ? baseName
                : baseName[..(MaxNameLength - suffix.Length)]) + suffix;
        }
        return name;
    }

    private static string Sanitize(string part)
    {
        var sb = new StringBuilder(part.Length);
        foreach (var c in part)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString().Trim('_');
    }

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
