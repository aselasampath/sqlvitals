using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlPulse.Desktop.Helpers;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

public partial class ApplicationConnectionsPage : Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public ApplicationConnectionsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var data = (await _repo.GetApplicationConnectionsAsync()).ToList();

        // ── Summary line ─────────────────────────────────────────────
        TxtSummary.Text = data.Count > 0
            ? $"{data.Count} application(s)  •  {data.Sum(d => d.CurrentConnections)} total connections"
            : "No active user connections";

        // ── KPI cards ────────────────────────────────────────────────
        KpiPanel.Children.Clear();
        if (data.Count > 0)
        {
            AddKpiCard("Applications",      $"{data.Count}",                                    "#7C3AED", "Distinct applications connected (grouped by program_name).");
            AddKpiCard("Total Connections",  $"{data.Sum(d => d.CurrentConnections):N0}",        "#06B6D4", "Total user sessions currently connected.");
            AddKpiCard("Active Requests",    $"{data.Sum(d => d.ActiveRequests):N0}",             "#10B981", "Sessions with an actively executing request right now.");
            AddKpiCard("Total CPU (ms)",     $"{data.Sum(d => d.TotalCpuTimeMs):N0}",             "#F59E0B", "Cumulative CPU time across all connected sessions.");
        }

        // ── DataGrid ─────────────────────────────────────────────────
        ConnectionsGrid.ItemsSource = data;

        // ── Bar chart ────────────────────────────────────────────────
        var top = data.OrderByDescending(d => d.CurrentConnections).Take(15).ToList();
        if (top.Count > 0)
        {
            var axisColor = ChartTheme.MutedAxisColor;
            var gridColor = ChartTheme.GridColor;

            ConnectionsChart.Series = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Values      = top.Select(d => d.CurrentConnections).ToArray(),
                    Name        = "Connections",
                    Fill        = new SolidColorPaint(SKColor.Parse("#7C3AED")),
                    Stroke      = null,
                    MaxBarWidth = 48,
                    Padding     = 2,
                }
            };
            ConnectionsChart.XAxes = new[] { new Axis
            {
                Labels          = top.Select(d => TruncateLabel(d.ApplicationName, 25)).ToArray(),
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 10,
                LabelsRotation  = 25,
            }};
            ConnectionsChart.YAxes = new[] { new Axis
            {
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 10,
                MinLimit        = 0,
            }};
        }
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
        {
            var sp = new StackPanel { MaxWidth = 340 };
            sp.Children.Add(new TextBlock { Text = label,   FontWeight = FontWeights.Bold,   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,4) });
            sp.Children.Add(new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap });
            ToolTipService.SetToolTip(card, new ToolTip { Content = sp });
        }
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue"), Foreground = brush });
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = panel;
        KpiPanel.Children.Add(card);
    }

    private static string TruncateLabel(string text, int maxLength) =>
        string.IsNullOrWhiteSpace(text) ? "(empty)"
        : text.Length <= maxLength ? text
        : text[..(maxLength - 1)] + "…";
}
