using System.Globalization;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>A row of the selected table's index grid (#242).</summary>
/// <param name="tablePages">The whole table's pages: each index's share is of it.</param>
public sealed class TableIndexRow(TableIndexSize index, long tablePages)
{
    /// <summary>A flagged index's note in the grid.</summary>
    internal const string CostlyNote = "Few reads, many writes";

    public TableIndexSize Index { get; } = index;

    public string Name       => Index.Name ?? "(heap)";
    public string Type       => TableSpace.TypeText(Index.Type);
    public long? RowsInIndex => Index.RowsInIndex;
    public long? ReservedPages => Index.ReservedPages;
    public long? UsedPages   => Index.UsedPages;
    public long? Reads       => Index.Reads;
    public long? Writes      => Index.Writes;

    public string ReservedText => TableSpace.SizeText(ReservedPages);
    public string UsedText     => TableSpace.SizeText(UsedPages);

    /// <summary>Its share of the table's total, in percent; null when its size isn't known.</summary>
    public double? SharePct { get; } = index.ReservedPages is { } pages && tablePages > 0 ? 100.0 * pages / tablePages : null;

    public string ShareText => SharePct is { } share ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} %", share) : TableSpace.Unknown;

    public bool IsCostly { get; } = TableSpace.IsCostly(index);

    public string Note => IsCostly ? CostlyNote : "";
}
