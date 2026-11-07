using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlVitals.Desktop.Controls;
using SqlVitals.Engine.TableStatistics;

namespace SqlVitals.Desktop.Tests;

/// <summary>The histogram chart (#243): its layout on its own, and that it draws what it's given.</summary>
public sealed class HistogramChartTests
{
    private const double Width = 1000;
    private const double Height = 250;

    private static double Measure(string text) => text.Length * 6.0;

    private static HistogramStep Step(int number, string? key, double range, double eq) => new(number, key, range, eq, 1, range);

    private static HistogramLayout Layout(IReadOnlyList<HistogramStep> steps, bool log = false, double? estimate = null, double width = Width) =>
        HistogramLayout.Compute(steps, log, estimate, width, Height, Measure);

    private static double Plot(HistogramLayout layout) => layout.PlotBottom - HistogramLayout.PlotTop;

    private static List<HistogramStep> Full() =>
        [Step(1, null, 0, 5_000), .. Enumerable.Range(2, 200).Select(i => Step(i, $"key-{i:000}", i * 10, i))];

    [Fact]
    public void TwoHundredAndOneSteps_FitAndTheLabelsDontOverlap()
    {
        var layout = Layout(Full());

        Assert.Equal(201, layout.Bars.Count);
        Assert.All(layout.Bars, b => Assert.InRange(b.X, HistogramLayout.PlotLeft, layout.PlotRight));
        Assert.InRange(layout.Bars[^1].X + layout.Bars[^1].Width, 0, layout.PlotRight + 0.01);
        Assert.Equal("NULL", layout.Labels[0].Text);
        Assert.InRange(layout.Labels.Count, 2, 40);

        for (var i = 1; i < layout.Labels.Count; i++)
        {
            var (left, right) = (layout.Labels[i - 1], layout.Labels[i]);
            Assert.True(left.X + Measure(left.Text) / 2 < right.X - Measure(right.Text) / 2, $"{left.Text} overlaps {right.Text}");
        }
    }

    [Fact]
    public void ANarrowChart_ShowsFewerLabels_StillNotOverlapping()
    {
        var layout = Layout(Full(), width: 320);

        Assert.True(layout.Labels.Count < Layout(Full()).Labels.Count);
        for (var i = 1; i < layout.Labels.Count; i++)
            Assert.True(layout.Labels[i - 1].X + Measure(layout.Labels[i - 1].Text) / 2 < layout.Labels[i].X - Measure(layout.Labels[i].Text) / 2);
    }

    [Fact]
    public void Linear_ColumnsAreInProportion_AndTheStackAddsUp()
    {
        var layout = Layout([Step(1, "a", 300, 100), Step(2, "b", 0, 800)]);

        Assert.Equal(800, layout.AxisMax, 3);
        var (a, b) = (layout.Bars[0], layout.Bars[1]);
        Assert.Equal(Plot(layout) * 0.5, a.EqHeight + a.RangeHeight, 3);
        Assert.Equal(Plot(layout), b.EqHeight, 3);
        Assert.Equal(3, a.RangeHeight / a.EqHeight, 3);

        // EQ_ROWS at the bottom, RANGE_ROWS on it.
        Assert.Equal(layout.PlotBottom, a.EqTop + a.EqHeight, 3);
        Assert.Equal(a.EqTop, a.RangeTop + a.RangeHeight, 3);
    }

    [Fact]
    public void Log_TheHeightIsLogOfTheTotal_SplitByLinearShare()
    {
        var layout = Layout([Step(1, "a", 900, 99), Step(2, "b", 0, 1_000_000)], log: true);

        var a = layout.Bars[0];
        Assert.Equal(1_000_000, layout.AxisMax, 3);
        Assert.Equal(Plot(layout) * Math.Log10(1000) / Math.Log10(1_000_001), a.EqHeight + a.RangeHeight, 3);
        // Not summed in log space: the shares are 99 and 900 of 999.
        Assert.Equal(900.0 / 99, a.RangeHeight / a.EqHeight, 3);
        Assert.Equal(Plot(layout), layout.Bars[1].EqHeight, 3);
    }

    [Fact]
    public void TheLogSwitch_KeepsASmallStepVisible()
    {
        var steps = new[] { Step(1, "a", 0, 10), Step(2, "b", 0, 10_000_000) };

        var linear = Layout(steps).Bars[0];
        var log = Layout(steps, log: true).Bars[0];

        Assert.Equal(1, linear.EqHeight, 3);   // the least a step with rows gets
        Assert.True(log.EqHeight > Plot(Layout(steps, log: true)) * 0.1, $"{log.EqHeight}");
    }

    [Fact]
    public void Ticks_RunUpTheAxis_InBothScales()
    {
        var steps = new[] { Step(1, "a", 0, 12_345) };

        var linear = Layout(steps);
        Assert.Equal(0, linear.Ticks[0].Value);
        Assert.Equal(linear.PlotBottom, linear.Ticks[0].Y, 3);
        Assert.Equal(linear.AxisMax, linear.Ticks[^1].Value, 3);
        Assert.Equal(HistogramLayout.PlotTop, linear.Ticks[^1].Y, 3);

        var log = Layout(steps, log: true);
        Assert.Equal([0, 10, 100, 1_000, 10_000, 100_000], log.Ticks.Select(t => t.Value));
        Assert.Equal(HistogramLayout.PlotTop, log.Ticks[^1].Y, 3);
    }

    [Fact]
    public void Empty_DrawsNothing()
    {
        var layout = Layout([]);

        Assert.Empty(layout.Bars);
        Assert.Empty(layout.Labels);
        Assert.Null(layout.ReferenceY);
    }

    [Fact]
    public void AllZero_DoesNotDivideByZero()
    {
        var steps = new[] { Step(1, "a", 0, 0), Step(2, "b", 0, 0) };

        foreach (var log in new[] { false, true })
        {
            var layout = Layout(steps, log);
            Assert.All(layout.Bars, b =>
            {
                Assert.Equal(0, b.EqHeight);
                Assert.Equal(0, b.RangeHeight);
                Assert.False(double.IsNaN(b.EqTop));
            });
            Assert.All(layout.Ticks, t => Assert.False(double.IsNaN(t.Y)));
            Assert.Equal(1, layout.AxisMax);
        }
    }

    [Fact]
    public void TheReferenceLine_IsAtTheEstimate_OnBothScales_AndAboveTheColumnsItStaysInThePlot()
    {
        var steps = new[] { Step(1, "a", 0, 1_000) };

        var linear = Layout(steps, estimate: 250);
        Assert.Equal(linear.PlotBottom - Plot(linear) * 250 / linear.AxisMax, linear.ReferenceY!.Value, 3);

        var log = Layout(steps, log: true, estimate: 99);
        Assert.Equal(log.PlotBottom - Plot(log) * Math.Log10(100) / Math.Log10(log.AxisMax + 1), log.ReferenceY!.Value, 3);

        var above = Layout(steps, estimate: 5_000);
        Assert.InRange(above.ReferenceY!.Value, HistogramLayout.PlotTop, above.PlotBottom);

        Assert.Null(Layout(steps, estimate: 0).ReferenceY);
        Assert.Null(Layout(steps).ReferenceY);
    }

    [Theory]
    [InlineData(null, "NULL")]
    [InlineData("", "''")]
    [InlineData("  ", "'  '")]
    [InlineData("42", "42")]
    [InlineData("a\r\nb", "a  b")]
    [InlineData("a key that is much longer than the axis has room for", "a key that is…")]
    public void AnAxisLabel_ReadsOnOneLine_AndIsShort(string? key, string label)
    {
        Assert.Equal(label, HistogramLayout.AxisLabel(key));
        Assert.True(label.Length <= HistogramLayout.MaxLabelLength);
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(12_000, "12K")]
    [InlineData(3_400_000, "3.4M")]
    [InlineData(2_000_000_000, "2B")]
    public void AValueOnTheAxis_IsCompact(double value, string text) =>
        Assert.Equal(text, HistogramLayout.Compact(value).Replace(',', '.'));

    [Fact]
    public void TheTooltip_HasEveryValue_TheKeyWhole_AndHowEachIsUsed()
    {
        var key = new string('x', 300);
        var tip = HistogramChart.TipFor(new HistogramStep(3, key, 1_000, 50, 10, 100), 201);

        Assert.Contains("Step 3 of 201", tip);
        Assert.Contains(key, tip);
        Assert.Contains("EQ_ROWS: 50.", tip);
        Assert.Contains("the estimate for = this value", tip);
        Assert.Contains("DISTINCT_RANGE_ROWS: 10.", tip);
        Assert.Contains("AVG_RANGE_ROWS: 100.", tip);
        Assert.Contains("the estimate for = a value between the keys", tip);

        var nulls = HistogramChart.TipFor(new HistogramStep(1, null, 0, 7, 0, 0), 201);
        Assert.Contains("RANGE_HI_KEY: NULL", nulls);
        Assert.Contains("IS NULL", nulls);
        Assert.DoesNotContain("AVG_RANGE_ROWS", nulls);

        Assert.Contains("an empty string", HistogramChart.TipFor(new HistogramStep(2, "", 0, 7, 0, 0), 201));
    }

    [Fact]
    public void AStepUnderThePointer_IsFound_AndNoneOutsideThePlot()
    {
        const double right = 1000;
        Assert.Null(HistogramLayout.StepAt(10, 100, right));
        Assert.Null(HistogramLayout.StepAt(right, 100, right));
        Assert.Null(HistogramLayout.StepAt(500, 0, right));
        Assert.Equal(0, HistogramLayout.StepAt(HistogramLayout.PlotLeft, 100, right));
        Assert.Equal(99, HistogramLayout.StepAt(right - 0.1, 100, right));
    }

    [Fact]
    public void ItsLayout_IsKept_UntilWhatItDrawsChanges() => UiThread.Run(() =>
    {
        var chart = Laid(Full());
        var first = chart.Layout();

        Assert.Same(first, chart.Layout());

        chart.LogScale = true;
        var log = chart.Layout();
        Assert.NotSame(first, log);

        chart.Measure(new Size(Width / 2, Height));
        chart.Arrange(new Rect(0, 0, Width / 2, Height));
        Assert.NotSame(log, chart.Layout());
    });

    private static HistogramChart Laid(IReadOnlyList<HistogramStep>? steps, double? estimate = null, bool log = false)
    {
        var chart = new HistogramChart { Steps = steps, UnknownEstimate = estimate, LogScale = log };
        chart.Measure(new Size(Width, Height));
        chart.Arrange(new Rect(0, 0, Width, Height));
        return chart;
    }

    private static HashSet<uint> Colours(HistogramChart chart)
    {
        var bitmap = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chart);
        var pixels = new uint[(int)Width * (int)Height];
        bitmap.CopyPixels(pixels, (int)Width * 4, 0);
        return [.. pixels];
    }

    private static uint Argb(FrameworkElement chart, string key)
    {
        var colour = (Color)chart.FindResource(key);
        return (uint)colour.A << 24 | (uint)colour.R << 16 | (uint)colour.G << 8 | colour.B;
    }

    [Theory]
    [InlineData("DarkTheme")]
    [InlineData("LightTheme")]
    public void ItDrawsBothSeries_InTheThemesColours(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);

        var chart = Laid(Full(), estimate: 500, log: true);
        var colours = Colours(chart);

        Assert.Contains(Argb(chart, "HistogramEqColor"), colours);
        Assert.Contains(Argb(chart, "HistogramRangeColor"), colours);
    });

    [Fact]
    public void ItDraws_WithNothingAndWithZeros_AndHoverDoesNotThrow() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");

        Colours(Laid(null));
        Colours(Laid([]));
        Colours(Laid([Step(1, "a", 0, 0)], estimate: 0));

        var chart = Laid([Step(1, null, 0, 5), Step(2, "", 0, 5), Step(3, "b", 5, 5)]);
        chart.Hover(HistogramLayout.PlotLeft + 1);
        Assert.Contains("RANGE_HI_KEY: NULL", chart.TipText);
        chart.Hover(5);
        Assert.Null(chart.TipText);
    });

    [Fact]
    public void ANamedChart_SaysNoKeyToAScreenReader()
    {
        var panel = File.ReadAllText(Path.Combine(DesktopSource.Dir, "Controls/TableStatisticsDetailsPanel.xaml"));
        Assert.Contains("AutomationProperties.Name=\"Histogram chart", panel);
        Assert.DoesNotContain("AutomationProperties.Name=\"{Binding", panel);
    }
}
