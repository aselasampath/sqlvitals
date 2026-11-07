using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>A row of the density vector grid on Table Statistics (#243): one column prefix.</summary>
/// <param name="rows">The rows the statistics were built on; null when the header wasn't read.</param>
public sealed class StatisticsDensityRow(DensityEntry entry, long? rows)
{
    public string Columns => entry.Columns;
    public double AllDensity => entry.AllDensity;
    public double? DistinctValues => entry.DistinctValues;
    public double? RowsPerValue { get; } = rows is { } known ? entry.RowsPerValue(known) : null;
}
