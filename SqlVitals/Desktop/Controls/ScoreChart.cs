using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Home;

namespace SqlVitals.Desktop.Controls;

/// <summary>A dashed line on a <see cref="ScoreChart"/> where something started, e.g. an alert.</summary>
public sealed record ChartMarker(DateTime TimeUtc, string Label, Brush Brush);

/// <summary>What a <see cref="ScoreChart"/> draws: the points over [from, to], spaced about a bucket apart.</summary>
public sealed record ScoreChartData(
    IReadOnlyList<ScorePoint> Points, DateTime FromUtc, DateTime ToUtc, TimeSpan Bucket, IReadOnlyList<ChartMarker>? Markers = null);

/// <summary>
/// A health score over time (#130): a Home tile's sparkline, or with <see cref="ShowAxes"/> the
/// Overview's chart (#143) with its 0/50/100 lines, times, alert markers, and the times the server
/// couldn't be reached shaded. Drawn in one pass rather than built from elements, so a page of
/// tiles costs a single visual each, however many points they hold.
/// </summary>
public sealed class ScoreChart : FrameworkElement
{
    private const double AxisLeft   = 30;
    private const double AxisBottom = 18;
    private const double AxisTop    = 8;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ScoreChartData), typeof(ScoreChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(ScoreChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What to draw; nothing while null.</summary>
    public ScoreChartData? Data
    {
        get => (ScoreChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>The line's colour; the theme's accent when null.</summary>
    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>Axes, labels and markers; off for a sparkline.</summary>
    public bool ShowAxes { get; set; }

    /// <summary>Said when there are no points, with the axes shown.</summary>
    public string EmptyText { get; set; } = "No scores were recorded in this range.";

    private IReadOnlyList<ScorePoint>  _points  = [];
    private IReadOnlyList<ChartMarker> _markers = [];
    private DateTime _fromUtc, _toUtc;
    private TimeSpan _bucket = TimeSpan.FromMinutes(1);

    [SuppressMessage("Naming", "CA1725", Justification = "dc is the usual WPF name; renaming touches every drawing call")]
    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 4 || size.Height < 4 || Data is not { } data)
            return;

        (_points, _fromUtc, _toUtc, _bucket, _markers) = (data.Points, data.FromUtc, data.ToUtc, data.Bucket, data.Markers ?? []);

        // Hit-testable everywhere, so the tooltip shows over the whole chart.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var plot = ShowAxes
            ? new Rect(AxisLeft, AxisTop, Math.Max(1, size.Width - AxisLeft - 4), Math.Max(1, size.Height - AxisTop - AxisBottom))
            : new Rect(1, 2, size.Width - 2, size.Height - 4);

        var muted  = Resource("TextMuted", Brushes.Gray);
        var border = Resource("Border", Brushes.LightGray);
        var danger = Resource("Danger", Brushes.Red);
        var line   = LineBrush ?? Resource("Accent", Brushes.MediumPurple);

        Point At(ChartPoint p) => new(plot.Left + p.X * plot.Width, plot.Top + p.Y * plot.Height);

        // The times the server couldn't be reached, under everything else.
        var shade = danger.Clone();
        shade.Opacity = 0.15;
        shade.Freeze();
        foreach (var (from, to) in ScoreTrend.UnreachableSpans(_points, _fromUtc, _toUtc, _bucket))
            dc.DrawRectangle(shade, null, new Rect(plot.Left + from * plot.Width, plot.Top,
                                                    Math.Max(2, (to - from) * plot.Width), plot.Height));

        if (ShowAxes)
            DrawAxes(dc, plot, muted, border);

        var pen = new Pen(line, ShowAxes ? 2.5 : 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        foreach (var segment in ScoreTrend.Segments(_points, _fromUtc, _toUtc, _bucket))
        {
            if (segment.Count == 1)
            {
                dc.DrawEllipse(line, null, At(segment[0]), 2, 2);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(At(segment[0]), isFilled: false, isClosed: false);
                ctx.PolyLineTo(segment.Skip(1).Select(At).ToList(), isStroked: true, isSmoothJoin: true);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        if (ShowAxes)
            DrawMarkers(dc, plot);

        if (ShowAxes && _points.Count == 0)
        {
            var text = Text(EmptyText, 12, muted);
            dc.DrawText(text, new Point(plot.Left + (plot.Width - text.Width) / 2, plot.Top + (plot.Height - text.Height) / 2));
        }
    }

    private void DrawAxes(DrawingContext dc, Rect plot, Brush muted, Brush border)
    {
        var grid = new Pen(border, 1);
        grid.Freeze();
        foreach (var score in new[] { 100, 50, 0 })
        {
            var y = plot.Top + ScoreTrend.Y(score) * plot.Height;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(score.ToString(CultureInfo.CurrentCulture), 10, muted);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        var format = ConnectionHistory.AxisFormat(_toUtc - _fromUtc);
        var lastRight = double.NegativeInfinity;
        foreach (var tick in ScoreTrend.Ticks(_fromUtc, _toUtc, offset, most: Math.Max(2, (int)(plot.Width / 110))))
        {
            var label = Text(tick.ToLocalTime().ToString(format, CultureInfo.CurrentCulture), 10, muted);
            var x     = plot.Left + ScoreTrend.X(tick, _fromUtc, _toUtc) * plot.Width;
            var left  = Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width);
            if (left < lastRight + 8)
                continue;
            dc.DrawText(label, new Point(left, plot.Bottom + 3));
            lastRight = left + label.Width;
        }
    }

    // A dashed line where each marker started, its label beside it near the bottom; labels that
    // would sit on each other step up a row.
    private void DrawMarkers(DrawingContext dc, Rect plot)
    {
        var card      = Resource("BgCard", Brushes.White);
        var rowsRight = new List<double>();
        foreach (var marker in _markers.Where(m => m.TimeUtc >= _fromUtc && m.TimeUtc <= _toUtc).OrderBy(m => m.TimeUtc))
        {
            var x   = plot.Left + ScoreTrend.X(marker.TimeUtc, _fromUtc, _toUtc) * plot.Width;
            var pen = new Pen(marker.Brush, 1.2) { DashStyle = new DashStyle([4, 3], 0) };
            pen.Freeze();
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            var label = Text(marker.Label, 10, marker.Brush);
            var left  = x - label.Width - 4 >= plot.Left ? x - label.Width - 4 : x + 4;
            var row   = rowsRight.FindIndex(right => right < left - 4);
            if (row < 0) { row = rowsRight.Count; rowsRight.Add(0); }
            rowsRight[row] = left + label.Width;
            var top = plot.Bottom - (row + 1) * (label.Height + 2);
            // On the card's colour, so a line passing behind doesn't run through the words.
            dc.DrawRectangle(card, null, new Rect(left - 2, top, label.Width + 4, label.Height));
            dc.DrawText(label, new Point(left, top));
        }
    }

    private FormattedText Text(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
