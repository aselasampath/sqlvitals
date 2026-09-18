using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlPulse.Desktop.Helpers;
using SqlPulse.Engine.Models;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

// ── View-model for each wait type checkbox row ────────────────────────────────
public sealed class WaitTypeItem : INotifyPropertyChanged
{
    public string WaitType     { get; }
    public string WaitCategory { get; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public WaitTypeItem(string waitType, string waitCategory)
    {
        WaitType     = waitType;
        WaitCategory = waitCategory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

// ── Page ──────────────────────────────────────────────────────────────────────
public partial class WaitStatsTrendPage : Page, IRefreshable
{
    // ── constants ────────────────────────────────────────────────────────────
    private const int MaxPoints = 60;
    private const int MaxSeries = 12;

    // ── colour palette (12 distinct shades) ──────────────────────────────────
    private static readonly SKColor[] Palette =
    {
        SKColor.Parse("#7C3AED"), SKColor.Parse("#E11D48"), SKColor.Parse("#2563EB"),
        SKColor.Parse("#D97706"), SKColor.Parse("#059669"), SKColor.Parse("#DB2777"),
        SKColor.Parse("#0891B2"), SKColor.Parse("#65A30D"), SKColor.Parse("#9333EA"),
        SKColor.Parse("#EA580C"), SKColor.Parse("#0284C7"), SKColor.Parse("#16A34A"),
    };

    // ── fields ────────────────────────────────────────────────────────────────
    private readonly IWaitStatsRepository _repo;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _intervalSec = 10;
    private int _countdown;

    // Wait type list (all items, used as the single source of truth for IsChecked)
    private readonly List<WaitTypeItem> _allItems = new();

    // Rolling window buffers: waitType → ordered time-series points
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _windows = new();

    // Previous snapshot for delta calculation
    private Dictionary<string, (long WaitTimeMs, long SignalWaitTimeMs, long WaitingTasksCount)> _prevValues = new();
    private DateTime _prevTime = DateTime.MinValue;
    private bool _initialized;

    private enum MetricType { WaitTimeMs, SignalWaitMs, ResourceWaitMs, WaitingTasks }
    private MetricType _metric = MetricType.WaitTimeMs;

    // ── constructor ───────────────────────────────────────────────────────────
    public WaitStatsTrendPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        _timer.Tick += Timer_Tick;
    }

    // ── IRefreshable ──────────────────────────────────────────────────────────
    public async Task RefreshAsync() => await LoadSnapshotAsync();

    // ── core data load ───────────────────────────────────────────────────────
    private async Task LoadSnapshotAsync()
    {
        try
        {
            TxtStatus.Text = "Querying sys.dm_os_wait_stats…";
            var data = (await _repo.GetCumulativeWaitsAsync()).ToList();
            var now  = DateTime.Now;

            if (!_initialized)
            {
                PopulateWaitTypeList(data);
                StoreValues(data, now);
                _initialized = true;
                TxtStatus.Text = $"Loaded {_allItems.Count} wait types. Select types, then press ▶ Start.";
                return; // first tick — no delta yet
            }

            double elapsedSec = Math.Max(0.1, (now - _prevTime).TotalSeconds);

            // Append delta values to each window
            foreach (var w in data)
            {
                if (!_prevValues.TryGetValue(w.WaitType, out var prev)) continue;

                if (!_windows.TryGetValue(w.WaitType, out var window))
                {
                    window = new ObservableCollection<DateTimePoint>();
                    _windows[w.WaitType] = window;
                }

                double value = _metric switch
                {
                    MetricType.WaitTimeMs    => (w.WaitTimeMs    - prev.WaitTimeMs)    / elapsedSec,
                    MetricType.SignalWaitMs  => (w.SignalWaitTimeMs - prev.SignalWaitTimeMs) / elapsedSec,
                    MetricType.ResourceWaitMs => ((w.WaitTimeMs - w.SignalWaitTimeMs)
                                                 - (prev.WaitTimeMs - prev.SignalWaitTimeMs)) / elapsedSec,
                    MetricType.WaitingTasks  => (w.WaitingTasksCount - prev.WaitingTasksCount) / elapsedSec,
                    _ => 0d
                };

                // Guard against negative values (can occur on server restart)
                value = Math.Max(0d, value);

                window.Add(new DateTimePoint(now, value));
                if (window.Count > MaxPoints) window.RemoveAt(0);
            }

            StoreValues(data, now);
            RebuildSeries();

            int checkedCount = _allItems.Count(i => i.IsChecked);
            TxtStatus.Text = $"Updated {now:HH:mm:ss}  |  {checkedCount} series shown";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private void PopulateWaitTypeList(List<WaitStatCumulative> data)
    {
        _allItems.Clear();
        foreach (var w in data)
            _allItems.Add(new WaitTypeItem(w.WaitType, w.WaitCategory));

        // Default: check top 20 by current cumulative wait time
        var top20 = data
            .OrderByDescending(w => w.WaitTimeMs)
            .Take(20)
            .Select(w => w.WaitType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _allItems)
            item.IsChecked = top20.Contains(item.WaitType);

        UpdateListDisplay();
        UpdateSelCount();
    }

    private void StoreValues(List<WaitStatCumulative> data, DateTime captureTime)
    {
        _prevValues = data.ToDictionary(
            w => w.WaitType,
            w => (w.WaitTimeMs, w.SignalWaitTimeMs, w.WaitingTasksCount));
        _prevTime = captureTime;
    }

    private void UpdateListDisplay(string filter = "")
    {
        IEnumerable<WaitTypeItem> items = string.IsNullOrWhiteSpace(filter)
            ? _allItems
            : _allItems.Where(i => i.WaitType.Contains(filter, StringComparison.OrdinalIgnoreCase));

        WaitTypeList.ItemsSource = items.ToList();
    }

    private void UpdateSelCount()
    {
        int sel   = _allItems.Count(i => i.IsChecked);
        int total = _allItems.Count;
        TxtSelCount.Text = $"{sel} / {total} selected";
    }

    private void RebuildSeries()
    {
        // Guard: controls may not be ready if called during InitializeComponent
        if (TxtMaxNote is null || TrendChart is null || TxtNoData is null) return;

        var toShow = _allItems.Where(i => i.IsChecked).Take(MaxSeries).ToList();

        TxtMaxNote.Text = _allItems.Count(i => i.IsChecked) > MaxSeries
            ? $"⚠  Max {MaxSeries} series — first {MaxSeries} shown"
            : "";

        if (toShow.Count == 0 || _windows.Count == 0)
        {
            TrendChart.Series = Array.Empty<ISeries>();
            TxtNoData.Visibility = Visibility.Visible;
            return;
        }

        TxtNoData.Visibility = Visibility.Collapsed;

        string yLabel = CmbMetric.SelectedIndex switch
        {
            0 => "ms / sec",
            1 => "ms / sec",
            2 => "ms / sec",
            3 => "tasks / sec",
            _ => ""
        };

        var series = toShow.Select((item, idx) =>
        {
            if (!_windows.TryGetValue(item.WaitType, out var buf))
            {
                buf = new ObservableCollection<DateTimePoint>();
                _windows[item.WaitType] = buf;
            }

            var colour = Palette[idx % Palette.Length];
            return (ISeries)new LineSeries<DateTimePoint>
            {
                Values         = buf,
                Name           = item.WaitType,
                Stroke         = new SolidColorPaint(colour) { StrokeThickness = 2 },
                Fill           = null,
                GeometrySize   = 0,
                GeometryStroke = null,
                GeometryFill   = null,
                LineSmoothness = 0,
            };
        }).ToArray();

        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        TrendChart.Series = series;
        TrendChart.XAxes  = new[]
        {
            new DateTimeAxis(TimeSpan.FromSeconds(1), dt => dt.ToString("HH:mm:ss"))
            {
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 10,
            }
        };
        TrendChart.YAxes = new[]
        {
            new Axis
            {
                Name            = yLabel,
                NamePaint       = new SolidColorPaint(axisColor),
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            }
        };

        // Force LiveCharts to repaint immediately
        TrendChart.CoreChart?.Update(
            new LiveChartsCore.Kernel.ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
    }

    // ── timer ─────────────────────────────────────────────────────────────────
    private void Timer_Tick(object? sender, EventArgs e)
    {
        _countdown--;
        TxtCountdown.Text = $"{_countdown}s";
        if (_countdown <= 0)
        {
            _countdown = _intervalSec;
            _ = LoadSnapshotAsync();
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    // ── event handlers ────────────────────────────────────────────────────────
    private void BtnTopWaits_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;

        var top20 = _prevValues
            .OrderByDescending(kv => kv.Value.WaitTimeMs)
            .Take(20)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in _allItems)
            item.IsChecked = top20.Contains(item.WaitType);

        UpdateSelCount();
        RebuildSeries();
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _allItems) item.IsChecked = false;
        UpdateSelCount();
        RebuildSeries();
    }

    private void WaitType_CheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateSelCount();
        RebuildSeries();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateListDisplay(TxtSearch.Text);

    private void CmbMetric_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _metric = CmbMetric.SelectedIndex switch
        {
            0 => MetricType.WaitTimeMs,
            1 => MetricType.SignalWaitMs,
            2 => MetricType.ResourceWaitMs,
            3 => MetricType.WaitingTasks,
            _ => MetricType.WaitTimeMs,
        };

        // Clear windows so chart restarts with fresh deltas for the chosen metric
        _windows.Clear();
        RebuildSeries();
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "⏹  Stop";
        _intervalSec = CmbInterval.SelectedIndex switch
        {
            0 => 5, 1 => 10, 2 => 30, 3 => 60, _ => 10
        };
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
}
