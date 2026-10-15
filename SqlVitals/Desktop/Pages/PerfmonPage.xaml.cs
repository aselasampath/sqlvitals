using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Perfmon, bound to <see cref="PerfmonViewModel"/> (#150, #188): the code-behind hands the range
/// picker to the top bar and its picks to the view model, and draws the counters in the theme's
/// colours.
/// </summary>
public partial class PerfmonPage : Page, IPageContext
{
    // Live, or History while a past range is picked; the range picker is in the top bar (#140).
    public PageKind Kind => ViewModel.IsLive ? PageKind.Live : PageKind.History;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
    public bool CanPause => ViewModel.IsLive;
    public bool IsPaused => ViewModel.IsPaused;

    // The bar's Resume and Pause start and stop the reads: the page's own Start and Stop were the
    // same toggle, so they went with the page template (#150). A past range doesn't read live.
    public void SetPaused(bool paused) => ViewModel.SetPaused(paused);

    public DateTime? DataTo => ViewModel.DataTo;
    public FrameworkElement? TakeBarTools() => PageBar.Detach(RangePicker);

    internal PerfmonViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public PerfmonPage(PerfmonViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        RangePicker.SetHistoryAvailable(viewModel.HistoryAvailable, ConnectionHistory.UnavailableReason);
        ViewModel.CompareToBaseline = RangePicker.CompareToBaseline;

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        Draw();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerfmonViewModel.Chart))
                Draw();
        };
        ViewModel.Sampled += Repaint;
    }

    // ── The range picker, in the top bar ──────────────────────────────────
    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private void RangePicker_RangeChanged(object? sender, HistoryRange range) => ViewModel.PickRange(range);

    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private void RangePicker_BaselineChanged(object? sender, bool on) => ViewModel.CompareToBaseline = on;

    // ── The chart, in the theme's colours ─────────────────────────────────

    // The counters ticked, and last week's under them while the baseline is on.
    private void Draw()
    {
        var chart = ViewModel.Chart;
        PerfmonChart.Series = chart.Lines.Select(line =>
        {
            var color = ChartTheme.Series(line.Colour);
            return (ISeries)new LineSeries<DateTimePoint>
            {
                Values         = line.Points,
                Name           = line.Name,
                Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.8f },
                Fill           = null,
                GeometrySize   = 3,
                GeometryFill   = new SolidColorPaint(color),
                GeometryStroke = null,
                LineSmoothness = 0.3,
            };
        }).Concat(chart.Baselines.Select(line => (ISeries)BaselineSeries.Line(line.Points, line.Name, ChartTheme.Series(line.Colour))))
          .ToList();
        if (chart.Lines.Count == 0)
            return;

        PerfmonChart.XAxes =
        [
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString(chart.AxisFormat, CultureInfo.CurrentCulture))
            {
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = chart.From?.Ticks,
                MaxLimit        = chart.To?.Ticks,
            },
        ];
        PerfmonChart.YAxes =
        [
            new Axis
            {
                Name            = chart.ValueName,
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            },
        ];
        PerfmonChart.LegendPosition = LiveChartsCore.Measure.LegendPosition.Bottom;
        Repaint();
    }

    // Drawn now rather than on LiveCharts' next pass.
    private void Repaint() =>
        PerfmonChart.CoreChart?.Update(new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
}
