using System.Collections.ObjectModel;
using System.Globalization;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Memory, bound to <see cref="MemoryViewModel"/> (#150, #187): the code-behind draws the trend and
/// the clerks in the theme's colours, scrolls the grant the verdict shows into view, and forwards
/// the top bar's Pause and an alert's focus to the view model.
/// </summary>
public partial class MemoryPage : System.Windows.Controls.Page, IPageContext, IFocusPage
{
    // ── A memory grants alert's evidence (#145): the view model filters the grants ──
    public void ShowFocus(PageFocus focus) => ViewModel.ShowFocus(focus);
    public void ClearFocus() => ViewModel.ClearFocus();
    public string? FocusNote => ViewModel.FocusNote;
    public string? ShowAllText => ViewModel.ShowAllText;

    // Live while trending. The bar's Resume and Pause start and stop it: the page's own Start and
    // Stop were the same toggle, so they went with the page template (#150). The trend reads on
    // the view model's timer (#169).
    public PageKind Kind => PageKind.Live;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
    public bool CanPause => true;
    public bool IsPaused => ViewModel.IsPaused;
    public void SetPaused(bool paused) => ViewModel.SetPaused(paused);

    // ── chart series colours ─────────────────────────────────────────
    private static SKColor ColTotal  => ChartTheme.Series(4);   // cyan
    private static SKColor ColTarget => ChartTheme.Series(9);   // blue
    private static SKColor ColBuffer => ChartTheme.Series(0);   // green
    private static SKColor ColGrants => ChartTheme.Series(6);   // amber

    // ── rolling windows (values in GB), the view model's samples ─────
    private readonly ObservableCollection<DateTimePoint> _winTotal  = new();
    private readonly ObservableCollection<DateTimePoint> _winTarget = new();
    private readonly ObservableCollection<DateTimePoint> _winBuffer = new();
    private readonly ObservableCollection<DateTimePoint> _winGrants = new();

    internal MemoryViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's, the figures' and the trend's folds are remembered; none in some tests.</param>
    public MemoryPage(MemoryViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        BuildInitialChart(); // create series (empty windows) on startup

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        foreach (var sample in ViewModel.Samples)
            Append(sample);
        ViewModel.Sampled += AppendAndDraw;
        ViewModel.Clerks.CollectionChanged += (_, _) => BuildClerksChart(ViewModel.Clerks);
        ViewModel.ShowRow += GrantsGrid.ScrollIntoView;
    }

    // ── append a reading to the rolling windows ──────────────────────
    private void Append(MemorySnapshot s)
    {
        var t = s.CaptureTime;
        static double toGB(long kb) => Math.Round(kb / 1_048_576.0, 3);

        Append(_winTotal,  t, toGB(s.TotalServerMemoryKB));
        Append(_winTarget, t, toGB(s.TargetServerMemoryKB));
        Append(_winBuffer, t, toGB(s.DatabaseCacheMemoryKB));
        Append(_winGrants, t, toGB(s.GrantedWorkspaceMemoryKB));
    }

    private void AppendAndDraw(MemorySnapshot s)
    {
        Append(s);
        // Force chart repaint
        MemChart.CoreChart?.Update(
            new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
    }

    private static void Append(ObservableCollection<DateTimePoint> win, DateTime t, double v)
    {
        win.Add(new DateTimePoint(t, v));
        if (win.Count > MemoryViewModel.MaxSamples) win.RemoveAt(0);
    }

    // ── build series once (uses the same shared ObservableCollections) ─
    private void BuildInitialChart()
    {
        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        MemChart.Series = new ISeries[]
        {
            MakeLine("Total Server Memory", _winTotal,  ColTotal),
            MakeLine("Target Memory",       _winTarget, ColTarget),
            MakeLine("Buffer Pool",         _winBuffer, ColBuffer),
            MakeLine("Memory Grants",       _winGrants, ColGrants),
        };

        MemChart.XAxes = new[]
        {
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString("HH:mm:ss", CultureInfo.CurrentCulture))
            {
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 10,
            }
        };
        MemChart.YAxes = new[]
        {
            new Axis
            {
                Name            = "Memory (GB)",
                NamePaint       = new SolidColorPaint(axisColor),
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            }
        };
    }

    private static LineSeries<DateTimePoint> MakeLine(
        string name, ObservableCollection<DateTimePoint> buf, SKColor colour) =>
        new()
        {
            Values         = buf,
            Name           = name,
            Stroke         = new SolidColorPaint(colour) { StrokeThickness = 2 },
            Fill           = null,
            GeometrySize   = 4,
            GeometryStroke = new SolidColorPaint(colour) { StrokeThickness = 2 },
            GeometryFill   = new SolidColorPaint(colour),
            LineSmoothness = 0,
        };

    // ── clerks bar chart ──────────────────────────────────────────────
    private void BuildClerksChart(IReadOnlyList<MemoryClerk> clerks)
    {
        if (clerks.Count == 0) return;
        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        ClerksChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values      = clerks.Select(c => (double)c.TotalPagesMB).ToArray(),
                Fill        = new SolidColorPaint(ChartTheme.Of("AccentColor")),
                Stroke      = null,
                MaxBarWidth = 48,
                Padding     = 2,
            }
        };
        ClerksChart.XAxes = new[] { new Axis
        {
            Labels          = clerks.Select(c => c.ClerkName).ToArray(),
            LabelsPaint     = new SolidColorPaint(axisColor),
            SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
            TextSize        = 10,
        }};
        ClerksChart.YAxes = new[] { new Axis
        {
            LabelsPaint     = new SolidColorPaint(axisColor),
            SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
            TextSize        = 10,
        }};
    }
}
