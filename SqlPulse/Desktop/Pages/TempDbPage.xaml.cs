using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

public partial class TempDbPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly ITempDbRepository _repo;

    public TempDbPage(ITempDbRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var (files, sessions, counters) = await _repo.GetTempDbPressureAsync();

        var fileList    = files.ToList();
        var sessionList = sessions.ToList();
        var counterList = counters.ToList();

        // ── Counter KPI cards ──────────────────────────────────────────
        CounterGrid.Children.Clear();
        foreach (var c in counterList)
        {
            double val = c.CounterValue;
            string hexColor = c.CounterName.Contains("Version") && val > 0 ? "#EF4444"
                            : c.CounterName.Contains("Free")    && val < 1_000_000 ? "#FBBF24" // < ~1 GB free (counter is in KB)
                            : "#7C3AED";
            string tip = c.CounterName switch
            {
                var n when n.Contains("Version Store Size")       => "Size (KB) of the version store in TempDB used by snapshot/RCSI isolation. Rapid growth indicates long-running read-committed snapshot transactions that keep row versions alive.",
                var n when n.Contains("Version Generation rate")  => "KB of version store data generated per second. A spike indicates a heavy RCSI/snapshot workload or a long-running UPDATE/DELETE that generates many row versions.",
                var n when n.Contains("Version Cleanup rate")     => "KB of version store data cleaned up per second. If cleanup rate < generation rate, the version store will grow until TempDB runs out of space.",
                var n when n.Contains("Free Space in tempdb")     => "Free space remaining in TempDB (KB). Low free space can cause query failures and blocking. Monitor alongside auto-grow events.",
                var n when n.Contains("Active Temp Tables")       => "Number of temporary tables and table variables currently open across all sessions. Very high counts may indicate connection-per-request designs or missing cleanup logic.",
                var n when n.Contains("Temp Tables Creation Rate")=> "Temporary tables created per second. A sustained high rate combined with query spills can exhaust TempDB I/O bandwidth.",
                _                                                  => $"TempDB performance counter from sys.dm_os_performance_counters: {c.CounterName}."
            };
            AddKpiCard(c.CounterName, $"{val:N0}", hexColor, tip);
        }

        // ── File space bar chart ───────────────────────────────────────
        if (fileList.Count > 0)
        {
            var labels   = fileList.Select(f => f.FileName).ToArray();
            var usedVals = fileList.Select(f => (double)f.SpaceUsedMB).ToArray();
            var freeVals = fileList.Select(f => (double)f.FreeSpaceMB).ToArray();

            var axisColor = SKColor.Parse("#94A3B8");
            var gridColor = SKColor.Parse("#2D2D44");

            FileChart.Series = new ISeries[]
            {
                new StackedColumnSeries<double>
                {
                    Values      = usedVals,
                    Name        = "Used MB",
                    Fill        = new SolidColorPaint(SKColor.Parse("#EF4444")),
                    Stroke      = null,
                    MaxBarWidth = 60,
                    Padding     = 4,
                },
                new StackedColumnSeries<double>
                {
                    Values      = freeVals,
                    Name        = "Free MB",
                    Fill        = new SolidColorPaint(SKColor.Parse("#22C55E")),
                    Stroke      = null,
                    MaxBarWidth = 60,
                    Padding     = 4,
                },
            };
            FileChart.XAxes = new[] { new Axis
            {
                Labels          = labels,
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
            }};
            FileChart.YAxes = new[] { new Axis
            {
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
            }};
        }

        // ── Sessions DataGrid ──────────────────────────────────────────
        SessionsGrid.ItemsSource = sessionList.OrderByDescending(s => s.InternalObjAllocKB + s.UserObjAllocKB);
    }

    private void AddKpiCard(string label, string value, string hexColor, string? tooltip = null)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        var card  = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 10, 10),
            Width  = 200,
        };
        if (tooltip is not null)
            ToolTipService.SetToolTip(card, MakeTooltip(label, tooltip));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue"), Foreground = brush });
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = sp;
        CounterGrid.Children.Add(card);
    }

    private static ToolTip MakeTooltip(string title, string description)
    {
        var sp = new StackPanel { MaxWidth = 340 };
        sp.Children.Add(new TextBlock { Text = title,       FontWeight = FontWeights.Bold,   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,4) });
        sp.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return new ToolTip { Content = sp };
    }
}
