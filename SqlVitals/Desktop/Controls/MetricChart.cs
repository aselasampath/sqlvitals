using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;

namespace SqlVitals.Desktop.Controls;

/// <summary>A point of a <see cref="MetricSeries"/>: UTC on this PC's clock; a null value breaks the line.</summary>
public readonly record struct MetricPoint(DateTime TimeUtc, double? Value);

/// <summary>One line on a <see cref="MetricChart"/>.</summary>
public sealed record MetricSeries(string Name, Brush Brush, IReadOnlyList<MetricPoint> Points);

/// <summary>What a <see cref="MetricChart"/> draws over [from, to], points about a bucket apart.</summary>
/// <param name="Top">The top of the scale, e.g. 100 for a percentage; a round number above the highest value when null.</param>
public sealed record MetricChartData(
    IReadOnlyList<MetricSeries> Series, DateTime FromUtc, DateTime ToUtc, TimeSpan Bucket,
    IReadOnlyList<ChartMarker>? Markers = null, double? Top = null);

/// <summary>
/// A metric over time on the Overview (#143): one line per series from zero up, times along the
/// bottom on this PC's clock, and a dashed line where each alert started. Drawn in one pass, like
/// <see cref="ScoreChart"/>, so six of them redrawn on every sample cost six visuals.
/// </summary>
public sealed class MetricChart : FrameworkElement
{
    private const double AxisLeft   = 40;
    private const double AxisBottom = 18;
    private const double AxisTop    = 8;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(MetricChartData), typeof(MetricChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What to draw; nothing while null.</summary>
    public MetricChartData? Data
    {
        get => (MetricChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Said in the middle when no series has a value in the range.</summary>
    public string EmptyText { get; set; } = "Nothing recorded in this range.";

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 4 || size.Height < 4 || Data is not { } data)
            return;

        // Hit-testable everywhere, so the chart's tooltip shows over all of it.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var plot   = new Rect(AxisLeft, AxisTop, Math.Max(1, size.Width - AxisLeft - 4), Math.Max(1, size.Height - AxisTop - AxisBottom));
        var muted  = Resource("TextMuted", Brushes.Gray);
        var border = Resource("Border", Brushes.LightGray);

        var values = data.Series.SelectMany(s => s.Points)
                         .Where(p => p.Value is not null && p.TimeUtc >= data.FromUtc && p.TimeUtc <= data.ToUtc)
                         .Select(p => p.Value!.Value)
                         .ToList();
        var top = data.Top ?? MetricScale.Top(values.Count == 0 ? 0 : values.Max());

        DrawAxes(dc, plot, data, top, muted, border);

        foreach (var series in data.Series)
            DrawSeries(dc, plot, data, series, top);

        DrawMarkers(dc, plot, data);

        if (values.Count == 0)
        {
            var text = Text(EmptyText, 12, muted);
            dc.DrawText(text, new Point(plot.Left + (plot.Width - text.Width) / 2, plot.Top + (plot.Height - text.Height) / 2));
        }
    }

    // Lines at zero, half and the top of the scale, and round times along the bottom.
    private void DrawAxes(DrawingContext dc, Rect plot, MetricChartData data, double top, Brush muted, Brush border)
    {
        var grid = new Pen(border, 1);
        grid.Freeze();
        foreach (var value in new[] { top, top / 2, 0 })
        {
            var y = plot.Top + MetricScale.Y(value, top) * plot.Height;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(Compact(value), 10, muted);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        var offset    = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        var format    = data.ToUtc - data.FromUtc <= TimeSpan.FromHours(1) ? "HH:mm" : ConnectionHistory.AxisFormat(data.ToUtc - data.FromUtc);
        var lastRight = double.NegativeInfinity;
        foreach (var tick in ScoreTrend.Ticks(data.FromUtc, data.ToUtc, offset, most: Math.Max(2, (int)(plot.Width / 90))))
        {
            var label = Text(tick.ToLocalTime().ToString(format, CultureInfo.CurrentCulture), 10, muted);
            var x     = plot.Left + ScoreTrend.X(tick, data.FromUtc, data.ToUtc) * plot.Width;
            var left  = Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width);
            if (left < lastRight + 8)
                continue;
            dc.DrawText(label, new Point(left, plot.Bottom + 3));
            lastRight = left + label.Width;
        }
    }

    // The series' line, broken at a null and where monitoring stopped (see HistoryGaps).
    private static void DrawSeries(DrawingContext dc, Rect plot, MetricChartData data, MetricSeries series, double top)
    {
        var inRange   = series.Points.Where(p => p.TimeUtc >= data.FromUtc && p.TimeUtc <= data.ToUtc).OrderBy(p => p.TimeUtc).ToList();
        var threshold = HistoryGaps.Threshold(inRange.Select(p => p.TimeUtc).ToList(), data.Bucket);

        var pen = new Pen(series.Brush, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        var segment = new List<Point>();
        DateTime? previous = null;
        void Flush()
        {
            if (segment.Count == 1)
                dc.DrawEllipse(series.Brush, null, segment[0], 1.8, 1.8);
            else if (segment.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(segment[0], isFilled: false, isClosed: false);
                    ctx.PolyLineTo(segment.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            segment.Clear();
        }

        foreach (var point in inRange)
        {
            if (previous is { } last && point.TimeUtc - last > threshold)
                Flush();
            previous = point.TimeUtc;
            if (point.Value is not { } value)
            {
                Flush();
                continue;
            }
            segment.Add(new Point(plot.Left + ScoreTrend.X(point.TimeUtc, data.FromUtc, data.ToUtc) * plot.Width,
                                  plot.Top + MetricScale.Y(value, top) * plot.Height));
        }
        Flush();
    }

    // A dashed line where each alert started; its label is on the health score chart beside it.
    private static void DrawMarkers(DrawingContext dc, Rect plot, MetricChartData data)
    {
        foreach (var marker in data.Markers ?? [])
        {
            if (marker.TimeUtc < data.FromUtc || marker.TimeUtc > data.ToUtc)
                continue;
            var x   = plot.Left + ScoreTrend.X(marker.TimeUtc, data.FromUtc, data.ToUtc) * plot.Width;
            var pen = new Pen(marker.Brush, 1.2) { DashStyle = new DashStyle([4, 3], 0) };
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    /// <summary>"0", "2.5", "750", "12k", "1.5M": a scale label short enough for the left margin.</summary>
    public static string Compact(double value)
    {
        var culture = CultureInfo.CurrentCulture;
        return Math.Abs(value) switch
        {
            >= 1_000_000 => (value / 1_000_000).ToString("0.#", culture) + "M",
            >= 10_000    => (value / 1_000).ToString("0.#", culture) + "k",
            >= 100       => value.ToString("0", culture),
            _            => value.ToString("0.#", culture),
        };
    }

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}

/// <summary>
/// A headline number's recent samples as a line (#143), from its lowest to its highest, so the
/// shape shows even when the figure hardly moves.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>The line's colour; the theme's accent when null.</summary>
    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Values is not { Count: > 0 } values || ActualWidth < 4 || ActualHeight < 4)
            return;

        var brush = LineBrush ?? TryFindResource("Accent") as Brush ?? Brushes.MediumPurple;
        var (low, high) = (values.Min(), values.Max());
        var span = high - low;
        Point At(int i) => new(
            values.Count == 1 ? ActualWidth - 2 : 1 + i * (ActualWidth - 2) / (values.Count - 1),
            span <= 0 ? ActualHeight / 2 : 2 + (1 - (values[i] - low) / span) * (ActualHeight - 4));

        if (values.Count == 1)
        {
            dc.DrawEllipse(brush, null, At(0), 2, 2);
            return;
        }

        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), isFilled: false, isClosed: false);
            ctx.PolyLineTo(Enumerable.Range(1, values.Count - 1).Select(At).ToList(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        dc.DrawEllipse(brush, null, At(values.Count - 1), 2.2, 2.2);
    }
}
