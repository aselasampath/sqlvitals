using System.Windows.Controls.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;
using Chart = SqlVitals.Desktop.ViewModels.Pages.LiveMetricsDashboardViewModel.Chart;
using Metric = SqlVitals.Desktop.ViewModels.Pages.LiveMetricsDashboardViewModel.Metric;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Live charts, bound to <see cref="LiveMetricsDashboardViewModel"/> (#189): the code-behind hands the
/// range picker and the sample interval to the top bar, builds a card per chart shown with its
/// series in the theme's colours, and opens the health score's breakdown.
/// </summary>
public partial class LiveMetricsDashboardPage : Page, IPageContext, IFocusPage
{
    internal LiveMetricsDashboardViewModel ViewModel { get; }

    // Each chart's card and LiveCharts chart, and its series: its own, and last week's beside them.
    private readonly Dictionary<Chart, Border> _panels = [];
    private readonly Dictionary<Chart, LiveChartsCore.SkiaSharpView.WPF.CartesianChart> _charts = [];
    private readonly Dictionary<Chart, (Func<ISeries[]> Current, ISeries[] Baseline)> _chartSeries = [];
    private ProcessMapControl? _processMap;

    public LiveMetricsDashboardPage(LiveMetricsDashboardViewModel viewModel)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        RangePicker.SetHistoryAvailable(viewModel.HistoryAvailable, ConnectionHistory.UnavailableReason);
        viewModel.CompareToBaseline = RangePicker.CompareToBaseline;
        // Its own, as it moves to the top bar (TakeBarTools), out of the page's.
        CmbInterval.DataContext = viewModel;

        foreach (var chart in Enum.GetValues<Chart>())
            ShowPanel(chart);

        viewModel.Sampled            += ForceChartUpdate;
        viewModel.WaitsChanged       += () => { ApplySeries(Chart.Waits); ForceChartUpdate(); };
        viewModel.BreakdownRequested += OpenBreakdown;
        viewModel.PropertyChanged    += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LiveMetricsDashboardViewModel.ShowsWaits):      ShowPanel(Chart.Waits); break;
                case nameof(LiveMetricsDashboardViewModel.ShowsCpu):        ShowPanel(Chart.Cpu); break;
                case nameof(LiveMetricsDashboardViewModel.ShowsThroughput): ShowPanel(Chart.Throughput); break;
                case nameof(LiveMetricsDashboardViewModel.ShowsPhysicalIo): ShowPanel(Chart.PhysicalIo); break;
                case nameof(LiveMetricsDashboardViewModel.ShowsMemory):     ShowPanel(Chart.Memory); break;
                case nameof(LiveMetricsDashboardViewModel.ShowsProcessMap): ShowPanel(Chart.ProcessMap); break;
                case nameof(LiveMetricsDashboardViewModel.Axis):            ApplyXAxisLimits(); break;
                case nameof(LiveMetricsDashboardViewModel.CompareToBaseline):
                    foreach (var chart in _chartSeries.Keys)
                        ApplySeries(chart);
                    break;
                case nameof(LiveMetricsDashboardViewModel.Processes) when ViewModel.Processes is { } nodes:
                    _processMap?.UpdateNodes(nodes);
                    break;
                case nameof(LiveMetricsDashboardViewModel.Breakdown) when HealthScorePopup.IsOpen:
                    ShowBreakdown();
                    break;
            }
        };
    }

    // ── An alert's window (#145) ──────────────────────────────────────────────

    public void ShowFocus(PageFocus focus)
    {
        // Before the page loads: its first load reads the window instead of plotting live samples.
        if (ViewModel.FocusRange(focus) is not { } range)
            return;
        RangePicker.Pick(range, notify: false);
        if (RangePicker.Range == range)
            ViewModel.ShowFocus(range);
    }

    public void ClearFocus()
    {
        if (ViewModel.ClearFocus())
            RangePicker.Pick(HistoryRange.Live);
    }

    public bool ShowsWindow => ViewModel.ShowsWindow;
    public string? FocusNote => ViewModel.FocusNote;
    public string? ShowAllText => ViewModel.ShowAllText;

    // ── Top bar (#140) ───────────────────────────────────────────────────────
    // Live, or History while a past range is picked. The range picker and the sample interval are
    // in the top bar; the interval box says how often, so the badge doesn't repeat it. Not
    // monitored, nothing moves: it reads as paused, and Start monitoring is what resumes it.

    public PageKind Kind => ViewModel.IsLive ? PageKind.Live : PageKind.History;
    public bool CanPause => ViewModel.CanPause;
    public bool IsPaused => ViewModel.IsPaused || !ViewModel.IsMonitoring;
    public DateTime? DataTo => ViewModel.DataTo;

    // Paused, the charts stop moving so they can be read; the session keeps collecting and
    // recording, and Resume plots what arrived meanwhile.
    public void SetPaused(bool paused) => ViewModel.SetPaused(paused);

    public FrameworkElement? TakeBarTools()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(PageBar.Detach(RangePicker));
        tools.Children.Add(PageBar.Detach(CmbInterval));
        RangePicker.Margin = new Thickness(0, 0, 8, 0);
        return tools;
    }

    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range) =>
        await ViewModel.ReportErrorsOf(ViewModel.PickRangeAsync(range));

    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private void RangePicker_BaselineChanged(object? sender, bool on) => ViewModel.CompareToBaseline = on;

    // ── The health score's breakdown (#36) ────────────────────────────────────

    // Clicking anywhere else closes it (StaysOpen is false).
    private void OpenBreakdown()
    {
        ShowBreakdown();
        HealthScorePopup.IsOpen = true;
    }

    private void ShowBreakdown()
    {
        var breakdown = ViewModel.Breakdown;
        HealthScoreDetails.Show(breakdown.Name, breakdown.Score, breakdown.Text, breakdown.Paused);
    }

    // ── The cards, in the theme's colours ─────────────────────────────────────

    private static SKColor ColCpu   => ChartTheme.Of("WaitCpuColor");
    private static SKColor ColIo    => ChartTheme.Of("WaitIoColor");
    private static SKColor ColLock  => ChartTheme.Of("WaitLockColor");
    private static SKColor ColMem   => ChartTheme.Of("WaitMemoryColor");
    private static SKColor ColNet   => ChartTheme.Of("WaitNetworkColor");
    private static SKColor ColOther => ChartTheme.Of("WaitOtherColor");
    private static SKColor ColBatch => ChartTheme.Of("MetricBatchesColor");
    private static SKColor ColComp  => ChartTheme.Of("MetricCompilationsColor");
    private static SKColor ColRec   => ChartTheme.Of("MetricRecompilationsColor");
    private static SKColor ColTxn   => ChartTheme.Of("MetricTransactionsColor");
    private static SKColor ColPhysR => ChartTheme.Of("MetricReadsColor");
    private static SKColor ColPhysW => ChartTheme.Of("MetricWritesColor");
    private static SKColor ColPle   => ChartTheme.Of("MetricPleColor");
    private static SKColor ColMgP   => ChartTheme.Of("DangerColor");
    private static SKColor ColBuf   => ChartTheme.Of("SuccessColor");

    // A chart's card, added in its place while its chip is on, removed when it is off.
    private void ShowPanel(Chart chart)
    {
        if (_panels.Remove(chart, out var existing))
        {
            ChartGrid.Children.Remove(existing);
            _charts.Remove(chart);
            _chartSeries.Remove(chart);
            if (chart == Chart.ProcessMap)
                _processMap = null;
        }

        if (ViewModel.Shows(chart))
        {
            var panel = chart switch
            {
                Chart.Waits      => BuildWaitsPanel(),
                Chart.Cpu        => BuildCpuPanel(),
                Chart.Throughput => BuildThroughputPanel(),
                Chart.PhysicalIo => BuildPhysicalIoPanel(),
                Chart.Memory     => BuildMemoryPanel(),
                _                => BuildProcessMapPanel(),
            };
            _panels[chart] = panel;
            ChartGrid.Children.Insert(Math.Min(InsertIndex(chart), ChartGrid.Children.Count), panel);
        }
        ChartGrid.Columns = ChartGrid.Children.Count <= 1 ? 1 : 2;
    }

    // Where a card goes so the cards keep their order.
    private int InsertIndex(Chart chart) =>
        Enum.GetValues<Chart>().TakeWhile(c => c != chart).Count(c => _panels.TryGetValue(c, out var panel) && ChartGrid.Children.Contains(panel));

    private Border BuildCpuPanel()
    {
        var chart  = MakeCartesian(Chart.Cpu);
        var series = new ISeries[] { Line(Metric.Cpu, "SQL Server CPU", ColCpu) };
        SetSeries(Chart.Cpu, () => series, BaselineLine(Metric.Cpu, "SQL Server CPU", ColCpu));
        SetDateTimeAxes(chart, "% CPU", 0, 100);
        return WrapPanel("CPU Usage", chart, Chart.Cpu);
    }

    private Border BuildThroughputPanel()
    {
        var chart  = MakeCartesian(Chart.Throughput);
        var series = new ISeries[]
        {
            Line(Metric.Batches,        "Batch Requests", ColBatch),
            Line(Metric.Compilations,   "Compilations",   ColComp),
            Line(Metric.Recompilations, "Recompilations", ColRec),
            Line(Metric.Transactions,   "Transactions",   ColTxn),
        };
        SetSeries(Chart.Throughput, () => series,
            BaselineLine(Metric.Batches,        "Batch Requests", ColBatch),
            BaselineLine(Metric.Compilations,   "Compilations",   ColComp),
            BaselineLine(Metric.Recompilations, "Recompilations", ColRec),
            BaselineLine(Metric.Transactions,   "Transactions",   ColTxn));
        SetDateTimeAxes(chart, "# per sec.");
        return WrapPanel("SQL Throughput", chart, Chart.Throughput);
    }

    private Border BuildPhysicalIoPanel()
    {
        var chart  = MakeCartesian(Chart.PhysicalIo);
        var series = new ISeries[]
        {
            Line(Metric.PhysicalReads,  "Physical Reads",  ColPhysR),
            Line(Metric.PhysicalWrites, "Physical Writes", ColPhysW),
        };
        SetSeries(Chart.PhysicalIo, () => series,
            BaselineLine(Metric.PhysicalReads,  "Physical Reads",  ColPhysR),
            BaselineLine(Metric.PhysicalWrites, "Physical Writes", ColPhysW));
        SetDateTimeAxes(chart, "R/W per sec.");
        return WrapPanel("Physical R/W", chart, Chart.PhysicalIo);
    }

    private Border BuildMemoryPanel()
    {
        var chart = MakeCartesian(Chart.Memory);

        // Grants pending and the cache hit ratio on the first axis; page life expectancy on the second.
        var series = new ISeries[]
        {
            Line(Metric.GrantsPending,       "Mem Grants Pending",     ColMgP, 0),
            Line(Metric.BufferCacheHitRatio, "Buffer Cache Hit Ratio", ColBuf, 0),
            Line(Metric.PageLifeExpectancy,  "Page Life Expectancy",   ColPle, 1),
        };
        SetSeries(Chart.Memory, () => series,
            BaselineLine(Metric.GrantsPending,       "Mem Grants Pending",     ColMgP, 0),
            BaselineLine(Metric.BufferCacheHitRatio, "Buffer Cache Hit Ratio", ColBuf, 0),
            BaselineLine(Metric.PageLifeExpectancy,  "Page Life Expectancy",   ColPle, 1));

        chart.XAxes = [TimeAxis()];
        chart.YAxes =
        [
            new Axis
            {
                Name            = "Count / %",
                NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 14,
                Position        = LiveChartsCore.Measure.AxisPosition.Start,
            },
            // PLE as seconds, hours or days, by its size.
            new Axis
            {
                Name            = "PLE (sec)",
                NamePaint       = new SolidColorPaint(ColPle) { IsAntialias = true },
                LabelsPaint     = new SolidColorPaint(ColPle) { IsAntialias = true },
                SeparatorsPaint = null,   // no grid lines of its own, to avoid clutter
                TextSize        = 14,
                Position        = LiveChartsCore.Measure.AxisPosition.End,
                Labeler         = v => v < 0 ? "0s" : v < 3600 ? $"{v:N0}s" : v < 86400 ? $"{v / 3600:N1}h" : $"{v / 86400:N1}d",
            },
        ];
        return WrapPanel("Memory", chart, Chart.Memory);
    }

    private Border BuildProcessMapPanel()
    {
        _processMap = new ProcessMapControl();
        if (ViewModel.Processes is { } nodes)
            _processMap.UpdateNodes(nodes);
        return WrapPanel("Process Map", _processMap, Chart.ProcessMap);
    }

    // The waits stacked, the categories ticked on its card; the baseline is one line against the
    // top of the stack: last week's total of the categories shown.
    private Border BuildWaitsPanel()
    {
        var chart = MakeCartesian(Chart.Waits);
        var stacked = new[]
        {
            StackedArea(Metric.WaitCpu,     "CPU",     ColCpu),
            StackedArea(Metric.WaitIo,      "I/O",     ColIo),
            StackedArea(Metric.WaitLock,    "Locks",   ColLock),
            StackedArea(Metric.WaitMemory,  "Memory",  ColMem),
            StackedArea(Metric.WaitNetwork, "Network", ColNet),
            StackedArea(Metric.WaitOther,   "Other",   ColOther),
        };
        SetSeries(Chart.Waits,
            () => stacked.Where((_, i) => ViewModel.WaitsShown[i].IsShown).Cast<ISeries>().ToArray(),
            BaselineSeries.Line(ViewModel.BaselineWaits, "Total waits", ChartTheme.AxisColor));
        SetDateTimeAxes(chart, "Wait (ms/s)");

        var filters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        foreach (var category in ViewModel.WaitsShown)
        {
            var box = new CheckBox
            {
                Content    = category.Name,
                Margin     = new Thickness(6, 0, 0, 0),
                FontSize   = 11,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor     = Cursors.Hand,
            };
            box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(LiveMetricsDashboardViewModel.WaitCategory.IsShown)) { Source = category });
            box.SetResourceReference(ForegroundProperty, "TextMuted");
            filters.Children.Add(box);
        }
        return WrapPanel("Waits - Stack Area", chart, Chart.Waits, filters);
    }

    // Registers a chart's series and shows them, with its baseline while that is on.
    private void SetSeries(Chart chart, Func<ISeries[]> current, params ISeries[] baseline)
    {
        _chartSeries[chart] = (current, baseline);
        ApplySeries(chart);
    }

    private void ApplySeries(Chart chart)
    {
        if (!_charts.TryGetValue(chart, out var view) || !_chartSeries.TryGetValue(chart, out var series))
            return;
        view.Series = ViewModel.CompareToBaseline ? [.. series.Current(), .. series.Baseline] : series.Current();
    }

    // Shows the whole picked range, so missing history reads as missing rather than being cropped.
    private void ApplyXAxisLimits()
    {
        foreach (var chart in _charts.Values)
            foreach (var axis in chart.XAxes)
            {
                axis.MinLimit = ViewModel.Axis.MinTicks;
                axis.MaxLimit = ViewModel.Axis.MaxTicks;
            }
    }

    // Drawn now rather than on LiveCharts' next pass.
    private void ForceChartUpdate()
    {
        foreach (var chart in _charts.Values)
        {
            // CoreChart throws ("Core not set yet") rather than returning null until the chart has
            // loaded: a card just built, or a sample arriving before the page loads.
            if (!chart.IsLoaded)
                continue;
            chart.CoreChart.Update(new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        }
    }

    // ── LiveCharts helpers ───────────────────────────────────────────────────

    private LiveChartsCore.SkiaSharpView.WPF.CartesianChart MakeCartesian(Chart chart) =>
        _charts[chart] = new()
        {
            TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Top,
            LegendPosition  = LiveChartsCore.Measure.LegendPosition.Bottom,
            Background      = System.Windows.Media.Brushes.Transparent,
            AnimationsSpeed = TimeSpan.Zero,
        };

    private StackedAreaSeries<DateTimePoint> StackedArea(Metric metric, string name, SKColor color) =>
        new()
        {
            Values         = ViewModel.Points(metric),
            Name           = name,
            Fill           = new SolidColorPaint(color.WithAlpha(160)),
            Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.5f },
            GeometrySize   = 0,
            LineSmoothness = 0.4,
        };

    private LineSeries<DateTimePoint> Line(Metric metric, string name, SKColor color, int scalesAt = 0) =>
        new()
        {
            Values         = ViewModel.Points(metric),
            Name           = name,
            Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.8f },
            Fill           = null,
            GeometrySize   = 4,
            GeometryFill   = new SolidColorPaint(color),
            GeometryStroke = null,
            LineSmoothness = 0.3,
            ScalesYAt      = scalesAt,
        };

    // The baseline under one of the plotted series.
    private ISeries BaselineLine(Metric metric, string name, SKColor color, int scalesAt = 0) =>
        BaselineSeries.Line(ViewModel.Baseline(metric), name, color, scalesAt);

    // Labels follow the range on screen: seconds while live, dates on ranges over a day.
    private DateTimeAxis TimeAxis() =>
        new(TimeSpan.FromSeconds(1), dt => dt.ToString(ViewModel.Axis.Format, CultureInfo.CurrentCulture))
        {
            LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
            SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
            TextSize        = 14,
            MinLimit        = ViewModel.Axis.MinTicks,
            MaxLimit        = ViewModel.Axis.MaxTicks,
        };

    private void SetDateTimeAxes(LiveChartsCore.SkiaSharpView.WPF.CartesianChart chart, string yLabel, double? yMin = null, double? yMax = null)
    {
        chart.XAxes = [TimeAxis()];
        var yAxis = new Axis
        {
            Name            = yLabel,
            NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
            LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
            SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
            TextSize        = 14,
        };
        if (yMin.HasValue) yAxis.MinLimit = yMin;
        if (yMax.HasValue) yAxis.MaxLimit = yMax;
        chart.YAxes = [yAxis];
    }

    // A chart's card: its title, its ✕ (which turns its chip off), and what else its header holds.
    private Border WrapPanel(string title, FrameworkElement content, Chart chart, FrameworkElement? extraHeader = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var titleBlock = new TextBlock
        {
            Text       = title,
            Foreground = ChartTheme.PanelTitleColor,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(titleBlock, Dock.Left);
        header.Children.Add(titleBlock);

        var close = new Button
        {
            Content             = "x",
            Style               = (Style)FindResource("CloseButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Command             = ViewModel.CloseCommand,
            CommandParameter    = chart,
        };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        if (extraHeader != null)
        {
            DockPanel.SetDock(extraHeader, Dock.Right);
            header.Children.Add(extraHeader);
        }

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        inner.Children.Add(header);
        Grid.SetRow(content, 1);
        inner.Children.Add(content);

        return new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(4),
            Child  = inner,
        };
    }
}
