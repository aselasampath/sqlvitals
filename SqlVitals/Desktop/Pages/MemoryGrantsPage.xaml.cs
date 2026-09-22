using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class MemoryGrantsPage : System.Windows.Controls.Page, IRefreshable
{
    // ── constants ────────────────────────────────────────────────────
    private const int MaxPoints = 60;

    // ── chart series colours ─────────────────────────────────────────
    private static readonly SKColor ColTotal   = SKColor.Parse("#38BDF8"); // light blue
    private static readonly SKColor ColTarget  = SKColor.Parse("#60A5FA"); // blue
    private static readonly SKColor ColBuffer  = SKColor.Parse("#34D399"); // green
    private static readonly SKColor ColGrants  = SKColor.Parse("#FCD34D"); // amber

    // ── rolling windows (values in GB) ───────────────────────────────
    private readonly ObservableCollection<DateTimePoint> _winTotal  = new();
    private readonly ObservableCollection<DateTimePoint> _winTarget = new();
    private readonly ObservableCollection<DateTimePoint> _winBuffer = new();
    private readonly ObservableCollection<DateTimePoint> _winGrants = new();

    // ── timer ─────────────────────────────────────────────────────────
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _intervalSec = 10;
    private int _countdown;

    private readonly IWaitStatsRepository _repo;

    public MemoryGrantsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        _timer.Tick += Timer_Tick;
        BuildInitialChart(); // create series (empty windows) on startup
    }

    // ── IRefreshable ──────────────────────────────────────────────────
    public async System.Threading.Tasks.Task RefreshAsync()
    {
        // Load grids and chart in parallel
        var snapTask   = _repo.GetMemorySnapshotAsync();
        var grantsTask = _repo.GetMemoryGrantsAsync();

        await System.Threading.Tasks.Task.WhenAll(snapTask, grantsTask);

        var snap               = snapTask.Result;
        var (grants, clerks, _) = grantsTask.Result;

        // ── KPI strip ────────────────────────────────────────────────
        ApplyKpi(snap);

        // ── Seed first chart point ────────────────────────────────────
        AppendSnapshot(snap);

        // ── Clerks DataGrid + bar chart ───────────────────────────────
        var clerkList = clerks.OrderByDescending(c => c.TotalPagesMB).Take(15).ToList();
        ClerksGrid.ItemsSource = clerkList;
        BuildClerksChart(clerkList);

        // ── Grants DataGrid ───────────────────────────────────────────
        GrantsGrid.ItemsSource = grants.OrderByDescending(g => g.GrantedMemoryKB).ToList();
    }

    // ── KPI strip update ─────────────────────────────────────────────
    private void ApplyKpi(MemorySnapshot s)
    {
        KpiPhysical.Text   = FormatKB(s.PhysicalMemoryKB);
        KpiTotal.Text      = FormatKB(s.TotalServerMemoryKB);
        KpiTarget.Text     = FormatKB(s.TargetServerMemoryKB);
        KpiBuffer.Text     = FormatKB(s.DatabaseCacheMemoryKB);
        KpiPlanCache.Text  = FormatKB(s.SqlCacheMemoryKB);
        KpiAvailPhys.Text  = FormatKB(s.AvailablePhysicalKB);
        KpiPageFile.Text   = FormatKB(s.TotalPageFileKB);
        KpiAvailPage.Text  = FormatKB(s.AvailablePageFileKB);
        KpiMemState.Text   = s.SystemMemoryState;
        KpiMemModel.Text   = s.MemoryModel;

        // Colour system state text
        KpiMemState.Foreground = s.SystemMemoryState switch
        {
            var v when v.StartsWith("Available", StringComparison.OrdinalIgnoreCase) =>
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(52, 211, 153)),   // green
            var v when v.Contains("Low", StringComparison.OrdinalIgnoreCase) =>
                new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(239, 68,  68)),   // red
            _ => new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(253, 186, 116))   // orange
        };
    }

    // ── append point to rolling windows ──────────────────────────────
    private void AppendSnapshot(MemorySnapshot s)
    {
        var t = s.CaptureTime;
        double toGB(long kb) => Math.Round(kb / 1_048_576.0, 3);

        Append(_winTotal,  t, toGB(s.TotalServerMemoryKB));
        Append(_winTarget, t, toGB(s.TargetServerMemoryKB));
        Append(_winBuffer, t, toGB(s.DatabaseCacheMemoryKB));
        Append(_winGrants, t, toGB(s.GrantedWorkspaceMemoryKB));

        TxtNoData.Visibility = Visibility.Collapsed;

        // Force chart repaint
        MemChart.CoreChart?.Update(
            new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
    }

    private static void Append(ObservableCollection<DateTimePoint> win, DateTime t, double v)
    {
        win.Add(new DateTimePoint(t, v));
        if (win.Count > MaxPoints) win.RemoveAt(0);
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
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString("HH:mm:ss"))
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
    private void BuildClerksChart(List<MemoryClerk> clerks)
    {
        if (clerks.Count == 0) return;
        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        ClerksChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values      = clerks.Select(c => (double)c.TotalPagesMB).ToArray(),
                Fill        = new SolidColorPaint(SKColor.Parse("#7C3AED")),
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

    // ── tab switching ─────────────────────────────────────────────────
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        bool isOverview = (string?)rb.Tag == "Overview";

        if (PanelOverview is null) return; // guard during InitializeComponent
        PanelOverview.Visibility  = isOverview                         ? Visibility.Visible : Visibility.Collapsed;
        PanelClerks.Visibility    = (string?)rb.Tag == "Clerks"        ? Visibility.Visible : Visibility.Collapsed;
        PanelGrants.Visibility    = (string?)rb.Tag == "Grants"        ? Visibility.Visible : Visibility.Collapsed;
        OverviewToolbar.Visibility = isOverview                         ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── page lifetime ────────────────────────────────────────────────
    private void Page_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    // ── timer ─────────────────────────────────────────────────────────
    private void Timer_Tick(object? sender, EventArgs e)
    {
        _countdown--;
        TxtCountdown.Text = $"{_countdown}s";
        if (_countdown <= 0)
        {
            _countdown = _intervalSec;
            _ = PollSnapshotAsync();
        }
    }

    private async System.Threading.Tasks.Task PollSnapshotAsync()
    {
        try
        {
            var snap = await _repo.GetMemorySnapshotAsync();
            ApplyKpi(snap);
            AppendSnapshot(snap);
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            MainWindow.ReportBackgroundError(this, "Memory Grants", ex);
        }
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "⏹  Stop";
        _intervalSec = CmbInterval.SelectedIndex switch { 0 => 5, 1 => 10, 2 => 30, 3 => 60, _ => 10 };
        _countdown = _intervalSec;
        TxtCountdown.Text = $"{_countdown}s";
        _timer.Start();
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "▶  Start";
        TxtCountdown.Text = "--";
        _timer.Stop();
    }

    // ── helper ───────────────────────────────────────────────────────
    private static string FormatKB(long kb) =>
        kb >= 1_048_576 ? $"{kb / 1_048_576.0:N1} GB" :
        kb >= 1_024     ? $"{kb / 1_024:N0} MB"       :
                          $"{kb:N0} KB";
}


