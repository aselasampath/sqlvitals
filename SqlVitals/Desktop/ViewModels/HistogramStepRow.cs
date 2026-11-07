using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A row of the histogram grid on Table Statistics (#243). A class, as a grid's row is: a refresh gives
/// new rows. <see cref="KeyText"/> is a real value from the table, for the screen and the grid's own
/// Copy and export only.
/// </summary>
public sealed class HistogramStepRow(HistogramStep step, double? unknownEstimate, IReadOnlyList<HistogramStep> histogram)
{
    public int Number => step.Number;

    /// <summary>NULL for the NULL step, <c>''</c> for an empty string, or the key as text.</summary>
    public string KeyText => step.Key switch
    {
        null => "NULL",
        ""   => "''",
        var key => key,
    };

    public double RangeRows => step.RangeRows;
    public double EqRows => step.EqRows;
    public double DistinctRangeRows => step.DistinctRangeRows;
    public double AvgRangeRows => step.AvgRangeRows;

    /// <summary>The choice of steps "10x or more from the unknown-value estimate".</summary>
    public bool IsFarFromUnknown { get; } = StatisticsFindings.IsFarFromUnknown(step, unknownEstimate);

    /// <summary>The choice of steps "above-average AVG_RANGE_ROWS".</summary>
    public bool IsAboveAverageRange { get; } = StatisticsFindings.IsAboveAverageRange(step, histogram);
}
