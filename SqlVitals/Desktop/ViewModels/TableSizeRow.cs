using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A row of Table Statistics' table list (#242). A class, not a record: after a refresh the row with
/// the same figures is still a new row, for the grid to select again.
/// </summary>
/// <param name="largestPages">The largest table's pages: the bars are in proportion to it.</param>
public sealed class TableSizeRow(TableSize table, long largestPages)
{
    /// <summary>The widest bar, in pixels.</summary>
    private const double MaxBarWidth = 120;

    public TableSize Table { get; } = table;

    public string SchemaName => Table.SchemaName;
    public string TableName  => Table.TableName;
    public long? RowsInTable => Table.RowsInTable;
    public long? DataPages   => Table.DataPages;
    public long? IndexPages  => Table.IndexPages;
    public long? TotalPages  => Table.TotalPages;
    public bool IsLocked     => Table.IsLocked;

    public string DataText  => TableSpace.SizeText(DataPages);
    public string IndexText => TableSpace.SizeText(IndexPages);
    public string TotalText => IsLocked ? "Locked" : TableSpace.SizeText(TotalPages);

    public double BarWidth { get; } = largestPages > 0 ? MaxBarWidth * (table.TotalPages ?? 0) / largestPages : 0;
}
