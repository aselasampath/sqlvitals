using System.Globalization;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Where <see cref="HistogramChart"/> draws everything (#243), worked out apart from drawing so it
/// can be tested without a window. On the log scale a column's height is log10(total + 1), and that
/// height is split by the linear share of EQ_ROWS and RANGE_ROWS: summing two logs would make a
/// stack higher than its total says.
/// </summary>
internal sealed class HistogramLayout
{
    public const double PlotLeft = 52;
    public const double PlotRightMargin = 10;
    public const double PlotTop = 26;
    public const double PlotBottomMargin = 22;

    /// <summary>The longest key an axis label holds; the tooltip has it whole.</summary>
    public const int MaxLabelLength = 14;

    private const double LabelGap = 10;

    /// <summary>A column in the plot. EQ_ROWS is at the bottom, so on the linear scale it reads against the reference line.</summary>
    public readonly record struct Bar(int Index, double X, double Width, double EqTop, double EqHeight, double RangeTop, double RangeHeight);

    /// <summary>A key on the x axis, centred at <paramref name="X"/>.</summary>
    public readonly record struct Label(int Index, string Text, double X);

    /// <summary>A rows value on the y axis, with its gridline at <paramref name="Y"/>.</summary>
    public readonly record struct Tick(double Value, string Text, double Y);

    public required IReadOnlyList<Bar> Bars { get; init; }
    public required IReadOnlyList<Label> Labels { get; init; }
    public required IReadOnlyList<Tick> Ticks { get; init; }

    /// <summary>The unknown-value line; null with no estimate above 0.</summary>
    public required double? ReferenceY { get; init; }

    public required double AxisMax { get; init; }
    public required double PlotRight { get; init; }
    public required double PlotBottom { get; init; }

    /// <param name="measure">How wide a text draws.</param>
    public static HistogramLayout Compute(IReadOnlyList<HistogramStep> steps, bool log, double? estimate,
                                          double width, double height, Func<string, double> measure)
    {
        var right  = Math.Max(PlotLeft + 1, width - PlotRightMargin);
        var bottom = Math.Max(PlotTop + 1, height - PlotBottomMargin);
        var plot   = bottom - PlotTop;

        var largest = steps.Count == 0 ? 0 : steps.Max(s => s.TotalRows);
        if (estimate > largest)
            largest = estimate.Value;
        var (axisMax, ticks) = log ? LogAxis(largest) : LinearAxis(largest);

        return new HistogramLayout
        {
            Bars       = Bars_(steps, log, axisMax, right, bottom, plot),
            Labels     = Labels_(steps, right, measure),
            Ticks      = ticks.Select(v => new Tick(v, Compact(v), bottom - Fraction(v, axisMax, log) * plot)).ToList(),
            ReferenceY = estimate > 0 ? bottom - Fraction(estimate.Value, axisMax, log) * plot : null,
            AxisMax    = axisMax,
            PlotRight  = right,
            PlotBottom = bottom,
        };
    }

    /// <summary>How far up the plot a number of rows reaches, 0 to 1.</summary>
    public static double Fraction(double rows, double axisMax, bool log)
    {
        if (axisMax <= 0 || rows <= 0)
            return 0;
        return log ? Math.Log10(rows + 1) / Math.Log10(axisMax + 1) : rows / axisMax;
    }

    /// <summary>The step under this x; null outside the plot.</summary>
    public static int? StepAt(double x, int count, double plotRight)
    {
        if (count == 0 || x < PlotLeft || x >= plotRight)
            return null;
        return Math.Min(count - 1, (int)((x - PlotLeft) / ((plotRight - PlotLeft) / count)));
    }

    /// <summary>A key on the axis: "NULL", <c>''</c> for an empty string, on one line and shortened.</summary>
    public static string AxisLabel(string? key)
    {
        if (key is null)
            return "NULL";
        if (key.Length == 0)
            return "''";
        var line = string.Concat(key.Select(c => char.IsControl(c) ? ' ' : c));
        if (string.IsNullOrWhiteSpace(line))
            return $"'{line}'";
        return line.Length <= MaxLabelLength ? line : line[..(MaxLabelLength - 1)] + "…";
    }

    /// <summary>A value on the y axis: 950, 12K, 3.4M.</summary>
    public static string Compact(double value)
    {
        var culture = CultureInfo.CurrentCulture;
        return value switch
        {
            >= 1e12 => (value / 1e12).ToString("0.#", culture) + "T",
            >= 1e9  => (value / 1e9).ToString("0.#", culture) + "B",
            >= 1e6  => (value / 1e6).ToString("0.#", culture) + "M",
            >= 1e4  => (value / 1e3).ToString("0.#", culture) + "K",
            _       => value.ToString("#,0.##", culture),
        };
    }

    private static List<Bar> Bars_(IReadOnlyList<HistogramStep> steps, bool log, double axisMax, double right, double bottom, double plot)
    {
        var bars = new List<Bar>(steps.Count);
        if (steps.Count == 0)
            return bars;
        var slot = (right - PlotLeft) / steps.Count;
        var gap  = slot >= 5 ? 1 : 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step  = steps[i];
            var total = step.TotalRows;
            var x     = PlotLeft + i * slot;
            if (total <= 0)
            {
                bars.Add(new Bar(i, x, slot - gap, bottom, 0, bottom, 0));
                continue;
            }
            // A step with rows never vanishes, however small next to the largest.
            var tall = Math.Max(1, Fraction(total, axisMax, log) * plot);
            var eq   = tall * step.EqRows / total;
            var rest = tall - eq;
            bars.Add(new Bar(i, x, slot - gap, bottom - eq, eq, bottom - tall, rest));
        }
        return bars;
    }

    // Every Nth key, N from the widest label shown, so they're evenly spaced and never overlap.
    private static List<Label> Labels_(IReadOnlyList<HistogramStep> steps, double right, Func<string, double> measure)
    {
        var labels = new List<Label>();
        if (steps.Count == 0)
            return labels;
        var slot  = (right - PlotLeft) / steps.Count;
        var texts = steps.Select(s => AxisLabel(s.Key)).ToList();
        var widest = texts.Max(measure);
        var every = Math.Max(1, (int)Math.Ceiling((widest + LabelGap) / slot));
        for (var i = 0; i < steps.Count; i += every)
        {
            var x = PlotLeft + (i + 0.5) * slot;
            // The last may not fit past the plot's end: it's in the tooltip.
            if (x + measure(texts[i]) / 2 <= right + PlotRightMargin)
                labels.Add(new Label(i, texts[i], x));
        }
        return labels;
    }

    private static (double Max, List<double> Ticks) LinearAxis(double largest)
    {
        if (largest <= 0)
            return (1, [0, 1]);
        var rough = largest / 4;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var scale = rough / power;
        var step  = (scale <= 1 ? 1 : scale <= 2 ? 2 : scale <= 5 ? 5 : 10) * power;
        var top   = Math.Ceiling(largest / step) * step;
        var ticks = new List<double>();
        for (var i = 0; i * step <= top * (1 + 1e-9); i++)
            ticks.Add(i * step);
        return (top, ticks);
    }

    private static (double Max, List<double> Ticks) LogAxis(double largest)
    {
        if (largest <= 1)
            return (1, [0, 1]);
        var top   = (int)Math.Ceiling(Math.Log10(largest));
        var every = Math.Max(1, (int)Math.Ceiling(top / 6.0));
        var ticks = new List<double> { 0 };
        for (var power = every; power <= top; power += every)
            ticks.Add(Math.Pow(10, power));
        if (top % every != 0)
            ticks.Add(Math.Pow(10, top));
        return (Math.Pow(10, top), ticks);
    }
}
