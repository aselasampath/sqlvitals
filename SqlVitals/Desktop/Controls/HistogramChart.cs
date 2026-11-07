using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A statistics object's histogram (#243): one stacked column per step, in step order with its key as
/// the label, whatever the column's type. A control of its own, not a LiveCharts one: a stack on a log
/// axis must be split by linear share, which a series can't do, and 201 steps draw at once this way.
/// </summary>
/// <remarks>
/// Keys are real values from the table: they stay on screen, in the labels and the tooltip. The
/// automation name says what the chart is, and no key. See <see cref="HistogramLayout"/>.
/// </remarks>
public sealed class HistogramChart : FrameworkElement
{
    private const double FontSize = 10.5;
    private static readonly Typeface Face = new("Segoe UI");

    public static readonly DependencyProperty StepsProperty = Register(nameof(Steps), typeof(IReadOnlyList<HistogramStep>), null);
    public static readonly DependencyProperty UnknownEstimateProperty = Register(nameof(UnknownEstimate), typeof(double?), null);
    public static readonly DependencyProperty LogScaleProperty = Register(nameof(LogScale), typeof(bool), false);

    private static readonly DependencyProperty EqColorProperty      = Register("EqColor", typeof(Color), Colors.Transparent);
    private static readonly DependencyProperty RangeColorProperty   = Register("RangeColor", typeof(Color), Colors.Transparent);
    private static readonly DependencyProperty LineColorProperty    = Register("LineColor", typeof(Color), Colors.Transparent);
    private static readonly DependencyProperty TextColorProperty    = Register("TextColor", typeof(Color), Colors.Transparent);
    private static readonly DependencyProperty GridColorProperty    = Register("GridColor", typeof(Color), Colors.Transparent);
    private static readonly DependencyProperty SurfaceColorProperty = Register("SurfaceColor", typeof(Color), Colors.Transparent);

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(HistogramChart),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) =>
            {
                var chart = (HistogramChart)d;
                chart._layout = null;
                chart.CloseTip();
            }));

    private readonly ToolTip _tip;
    private readonly TextBlock _tipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private int? _hovered;

    // The last layout, kept as the pointer moves: its labels' text is measured, and only the highlight changes.
    private HistogramLayout? _layout;
    private Size _layoutSize;

    public HistogramChart()
    {
        SetResourceReference(EqColorProperty, "HistogramEqColor");
        SetResourceReference(RangeColorProperty, "HistogramRangeColor");
        SetResourceReference(LineColorProperty, "TextPrimaryColor");
        SetResourceReference(TextColorProperty, "TextMutedColor");
        SetResourceReference(GridColorProperty, "BorderColor");
        SetResourceReference(SurfaceColorProperty, "BgCardColor");

        _tip = new ToolTip { Content = _tipText, PlacementTarget = this, Placement = PlacementMode.Mouse };
        Unloaded += (_, _) => CloseTip();
    }

    /// <summary>The steps in order: always the whole statistics object.</summary>
    public IReadOnlyList<HistogramStep>? Steps
    {
        get => (IReadOnlyList<HistogramStep>?)GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    /// <summary>The estimate for a value the optimizer can't see: the reference line. Null draws none.</summary>
    public double? UnknownEstimate
    {
        get => (double?)GetValue(UnknownEstimateProperty);
        set => SetValue(UnknownEstimateProperty, value);
    }

    public bool LogScale
    {
        get => (bool)GetValue(LogScaleProperty);
        set => SetValue(LogScaleProperty, value);
    }

    /// <summary>What it draws at the size it has; for tests too.</summary>
    internal HistogramLayout Layout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (_layout is null || size != _layoutSize)
        {
            _layout = HistogramLayout.Compute(Steps ?? [], LogScale, UnknownEstimate, ActualWidth, ActualHeight, t => Text(t, Colors.Black).Width);
            _layoutSize = size;
        }
        return _layout;
    }

    /// <summary>
    /// Every value of a step and how the optimizer uses it, in plain words, the key whole.
    /// </summary>
    internal static string TipFor(HistogramStep step, int count)
    {
        var culture = CultureInfo.CurrentCulture;
        string Rows(double value) => value.ToString("#,0.##", culture);
        var key = step.Key switch
        {
            null => "NULL",
            ""   => "'' (an empty string)",
            var k => k,
        };
        var lines = new List<string>
        {
            string.Format(culture, "Step {0:N0} of {1:N0}", step.Number, count),
            $"RANGE_HI_KEY: {key}",
        };
        if (step.IsNullStep)
        {
            lines.Add($"EQ_ROWS: {Rows(step.EqRows)}. The rows where the column is NULL: the estimate for IS NULL.");
            return string.Join(Environment.NewLine, lines);
        }
        lines.Add($"EQ_ROWS: {Rows(step.EqRows)}. Rows equal to this key: the estimate for = this value.");
        lines.Add($"RANGE_ROWS: {Rows(step.RangeRows)}. Rows between the last step's key and this one, not counting either key.");
        lines.Add($"DISTINCT_RANGE_ROWS: {Rows(step.DistinctRangeRows)}. Distinct values among those rows.");
        lines.Add($"AVG_RANGE_ROWS: {Rows(step.AvgRangeRows)}. RANGE_ROWS / DISTINCT_RANGE_ROWS: the estimate for = a value between the keys, whatever its real count.");
        return string.Join(Environment.NewLine, lines);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Hits anywhere in the plot, between columns too.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var steps = Steps;
        if (steps is not { Count: > 0 } || ActualWidth < HistogramLayout.PlotLeft + 20 || ActualHeight < HistogramLayout.PlotTop + 30)
            return;

        var layout = Layout();
        var eq     = new SolidColorBrush(Get(EqColorProperty));
        var range  = new SolidColorBrush(Get(RangeColorProperty));
        var grid   = new Pen(new SolidColorBrush(Get(GridColorProperty)), 1);
        var muted  = Get(TextColorProperty);
        var strong = Get(LineColorProperty);

        DrawLegend(drawingContext, eq, range, muted);

        foreach (var tick in layout.Ticks)
        {
            var y = Math.Round(tick.Y) + 0.5;
            drawingContext.DrawLine(grid, new Point(HistogramLayout.PlotLeft, y), new Point(layout.PlotRight, y));
            var text = Text(tick.Text, muted);
            drawingContext.DrawText(text, new Point(HistogramLayout.PlotLeft - 6 - text.Width, tick.Y - text.Height / 2));
        }

        if (_hovered is { } hovered && hovered < layout.Bars.Count)
        {
            var bar = layout.Bars[hovered];
            var wash = new SolidColorBrush(Color.FromArgb(0x30, strong.R, strong.G, strong.B));
            drawingContext.DrawRectangle(wash, null, new Rect(bar.X, HistogramLayout.PlotTop, Math.Max(bar.Width, 2), layout.PlotBottom - HistogramLayout.PlotTop));
        }

        foreach (var bar in layout.Bars)
        {
            var width = Math.Max(bar.Width, 0.75);
            if (bar.EqHeight > 0)
                drawingContext.DrawRectangle(eq, null, new Rect(bar.X, bar.EqTop, width, bar.EqHeight));
            if (bar.RangeHeight > 0)
                drawingContext.DrawRectangle(range, null, new Rect(bar.X, bar.RangeTop, width, bar.RangeHeight));
        }

        foreach (var label in layout.Labels)
        {
            var text = Text(label.Text, muted);
            drawingContext.DrawLine(grid, new Point(label.X, layout.PlotBottom), new Point(label.X, layout.PlotBottom + 3));
            drawingContext.DrawText(text, new Point(label.X - text.Width / 2, layout.PlotBottom + 4));
        }

        if (layout.ReferenceY is { } line && UnknownEstimate is { } estimate)
            DrawReference(drawingContext, layout, line, estimate, strong);
    }

    private void DrawLegend(DrawingContext context, Brush eq, Brush range, Color muted)
    {
        var x = HistogramLayout.PlotLeft;
        foreach (var (brush, name) in new[] { (eq, "EQ_ROWS: rows equal to the key"), (range, "RANGE_ROWS: rows between the keys") })
        {
            context.DrawRectangle(brush, null, new Rect(x, 6, 10, 10));
            var text = Text(name, muted);
            context.DrawText(text, new Point(x + 15, 11 - text.Height / 2));
            x += 15 + text.Width + 16;
        }
    }

    // The line's own label, on the card's colour so it reads over the columns.
    private void DrawReference(DrawingContext context, HistogramLayout layout, double y, double estimate, Color strong)
    {
        var brush = new SolidColorBrush(strong);
        var pen = new Pen(brush, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
        context.DrawLine(pen, new Point(HistogramLayout.PlotLeft, y), new Point(layout.PlotRight, y));

        var text = Text(string.Format(CultureInfo.CurrentCulture, "Unknown-value estimate: {0} rows", estimate.ToString("#,0.#", CultureInfo.CurrentCulture)), strong);
        var left = layout.PlotRight - text.Width - 6;
        var top  = y - text.Height - 3 < HistogramLayout.PlotTop ? y + 3 : y - text.Height - 3;
        context.DrawRectangle(new SolidColorBrush(Get(SurfaceColorProperty)), null, new Rect(left - 3, top, text.Width + 6, text.Height));
        context.DrawText(text, new Point(left, top));
    }

    private Color Get(DependencyProperty property) => (Color)GetValue(property);

    private FormattedText Text(string text, Color color) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, FontSize, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Hover(e.GetPosition(this).X);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        CloseTip();
    }

    /// <summary>The pointer is over this x: its step's tooltip opens, at the pointer, and its column lights.</summary>
    internal void Hover(double x)
    {
        var steps = Steps ?? [];
        var at = HistogramLayout.StepAt(x, steps.Count, Math.Max(HistogramLayout.PlotLeft + 1, ActualWidth - HistogramLayout.PlotRightMargin));
        if (at == _hovered)
            return;
        _hovered = at;
        _tip.IsOpen = false;
        if (at is { } step)
        {
            _tipText.Text = TipFor(steps[step], steps.Count);
            _tip.IsOpen = true;
        }
        InvalidateVisual();
    }

    /// <summary>What the tooltip says now; null when it's closed.</summary>
    internal string? TipText => _hovered is null ? null : _tipText.Text;

    private void CloseTip()
    {
        if (_hovered is null)
            return;
        _hovered = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }
}
