using System.Collections.ObjectModel;
using System.Globalization;
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
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

public partial class MemoryGrantsPage : System.Windows.Controls.Page, IRefreshable, IPageContext, IFocusPage
{
    // ── A memory grants alert's evidence (#145): the grants still waiting, until Show all ──
    private bool _pendingOnly;
    private IReadOnlyList<MemoryGrant>? _grants;

    public void ShowFocus(PageFocus focus)
    {
        _pendingOnly = focus.Filter.Kind == FocusKind.PendingGrants;
        ShowGrants();
    }

    public void ClearFocus()
    {
        _pendingOnly = false;
        ShowGrants();
    }

    public string? FocusNote => !_pendingOnly || _grants is null ? null
        : PendingCount switch
        {
            0     => "no query is waiting for memory now",
            1     => "showing the 1 grant waiting for memory only",
            var n => $"showing the {n} grants waiting for memory only",
        };

    public string? ShowAllText => !_pendingOnly ? null : _grants is null ? "Show all" : $"Show all {_grants.Count} grants";

    private int PendingCount => _grants?.Count(g => g.IsPending) ?? 0;

    private void ShowGrants()
    {
        if (_grants is null)
            return;
        GrantsGrid.ItemsSource = (_pendingOnly ? _grants.Where(g => g.IsPending) : _grants)
            .OrderByDescending(g => g.GrantedMemoryKB).ToList();
        MainWindow.ReportContextChanged(this);
    }

    // Live while trending. The bar's Resume and Pause start and stop it: the page's own Start and
    // Stop were the same toggle, so they went with the page template (#150).
    public PageKind Kind => PageKind.Live;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(_intervalSec);
    public bool CanPause => true;
    public bool IsPaused => !_timer.IsEnabled;

    public void SetPaused(bool paused)
    {
        if (paused) StopReading();
        else StartReading();
    }

    // ── constants ────────────────────────────────────────────────────
    private const int MaxPoints = 60;

    // ── chart series colours ─────────────────────────────────────────
    private static SKColor ColTotal  => ChartTheme.Series(4);   // cyan
    private static SKColor ColTarget => ChartTheme.Series(10);  // blue
    private static SKColor ColBuffer => ChartTheme.Series(0);   // green
    private static SKColor ColGrants => ChartTheme.Series(6);   // amber

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

    // The page template's verdict (#150): queries waiting for a memory grant, or how memory is used.
    internal MemoryGrantsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's, the figures' and the trend's folds are remembered; none in some tests.</param>
    public MemoryGrantsPage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();
        _timer.Tick += Timer_Tick;
        BuildInitialChart(); // create series (empty windows) on startup

        ViewModel = new MemoryGrantsViewModel(showWaiting: ShowWaiting, showClerks: () => Tabs.SelectedItem = TabClerks);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ShowTrendSummary();
    }

    // The verdict's Show the waiting grants: the Memory Grants tab, the longest waiter read selected.
    private void ShowWaiting()
    {
        Tabs.SelectedItem = TabGrants;
        if ((_grants ?? []).Where(g => g.IsPending).MaxBy(g => g.WaitForGrantMs) is { } longest && GrantsGrid.Items.Contains(longest))
        {
            GrantsGrid.SelectedItem = longest;
            GrantsGrid.ScrollIntoView(longest);
        }
    }

    // ── IRefreshable ──────────────────────────────────────────────────
    public async System.Threading.Tasks.Task RefreshAsync()
    {
        MemorySnapshot snap;
        IEnumerable<MemoryGrant> grants;
        IEnumerable<MemoryClerk> clerks;
        try
        {
            // Load grids and chart in parallel
            var snapTask   = _repo.GetMemorySnapshotAsync();
            var grantsTask = _repo.GetMemoryGrantsAsync();

            await System.Threading.Tasks.Task.WhenAll(snapTask, grantsTask);

            snap = snapTask.Result;
            (grants, clerks, _) = grantsTask.Result;
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the memory figures", ex);
            throw;
        }

        // ── KPI strip ────────────────────────────────────────────────
        ApplyKpi(snap);

        // ── Seed first chart point ────────────────────────────────────
        AppendSnapshot(snap);

        // ── Clerks DataGrid + bar chart ───────────────────────────────
        var clerkList = clerks.OrderByDescending(c => c.TotalPagesMB).Take(15).ToList();
        ClerksGrid.ItemsSource = clerkList;
        BuildClerksChart(clerkList);

        // ── Grants DataGrid ───────────────────────────────────────────
        _grants = grants.ToList();
        ShowGrants();
        ViewModel.Show(snap, _grants);
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

        // Folded, the figures say it in one line (#150).
        FiguresSection.FoldedSummary = $"SQL Server {FormatKB(s.TotalServerMemoryKB)} of {FormatKB(s.TargetServerMemoryKB)} target · " +
                                       $"buffer pool {FormatKB(s.DatabaseCacheMemoryKB)} · plan cache {FormatKB(s.SqlCacheMemoryKB)} · {s.SystemMemoryState}";

        // Colour system state text
        KpiMemState.SetResourceReference(TextBlock.ForegroundProperty, s.SystemMemoryState switch
        {
            var v when v.StartsWith("Available", StringComparison.OrdinalIgnoreCase) => "Success",
            var v when v.Contains("Low", StringComparison.OrdinalIgnoreCase)         => "Danger",
            _                                                                        => "Warning",
        });
    }

    // ── append point to rolling windows ──────────────────────────────
    private void AppendSnapshot(MemorySnapshot s)
    {
        var t = s.CaptureTime;
        static double toGB(long kb) => Math.Round(kb / 1_048_576.0, 3);

        Append(_winTotal,  t, toGB(s.TotalServerMemoryKB));
        Append(_winTarget, t, toGB(s.TargetServerMemoryKB));
        Append(_winBuffer, t, toGB(s.DatabaseCacheMemoryKB));
        Append(_winGrants, t, toGB(s.GrantedWorkspaceMemoryKB));

        TxtNoData.Visibility = Visibility.Collapsed;
        _last = s;
        ShowTrendSummary();

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
            ViewModel.Show(snap, null);   // the counter says whether queries wait; the grants are read on Refresh
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            MainWindow.ReportBackgroundError(this, "Memory Grants", ex);
        }
    }

    private int SelectedIntervalSec => CmbInterval.SelectedIndex switch { 0 => 5, 1 => 10, 2 => 30, 3 => 60, _ => 10 };

    // The latest reading, for the trend's folded line.
    private MemorySnapshot? _last;

    // Folded, the trend says it in one line (#150): the latest figures, and whether it is moving.
    private void ShowTrendSummary()
    {
        static double Gb(long kb) => Math.Round(kb / 1_048_576.0, 2);
        var reading = _last is { } s ? $"SQL Server {Gb(s.TotalServerMemoryKB):0.##} GB, grants {Gb(s.GrantedWorkspaceMemoryKB):0.##} GB · " : "";
        TrendSection.FoldedSummary = reading + (_timer.IsEnabled ? $"every {_intervalSec} s" : "paused: Resume (▶) in the top bar trends it");
    }

    // What Start did: read every interval, counting down to the next read.
    private void StartReading()
    {
        if (_timer.IsEnabled)
            return;
        _intervalSec = SelectedIntervalSec;
        _countdown = _intervalSec;
        TxtCountdown.Text = $"{_countdown}s";
        _timer.Start();
        ShowTrendSummary();
        MainWindow.ReportContextChanged(this);
    }

    // What Stop did.
    private void StopReading()
    {
        if (!_timer.IsEnabled)
            return;
        TxtCountdown.Text = "--";
        _timer.Stop();
        ShowTrendSummary();
        MainWindow.ReportContextChanged(this);
    }

    // Another interval: used from the next read on, while reading.
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtCountdown is null)
            return;   // during InitializeComponent
        _intervalSec = SelectedIntervalSec;
        if (_timer.IsEnabled)
        {
            _countdown = Math.Min(_countdown, _intervalSec);
            TxtCountdown.Text = $"{_countdown}s";
            ShowTrendSummary();
        }
        MainWindow.ReportContextChanged(this);
    }

    // ── helper ───────────────────────────────────────────────────────
    private static string FormatKB(long kb) =>
        kb >= 1_048_576 ? $"{kb / 1_048_576.0:N1} GB" :
        kb >= 1_024     ? $"{kb / 1_024:N0} MB"       :
                          $"{kb:N0} KB";
}


