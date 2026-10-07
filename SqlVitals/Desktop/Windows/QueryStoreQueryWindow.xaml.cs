using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Windows;

/// <summary>
/// One Query Store query (#111, SSMS's Tracked Queries): its full text, a metric per interval with
/// one line per plan and the forced plan marked, and its plans. Several can be open at once.
/// </summary>
public partial class QueryStoreQueryWindow : Window
{
    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#26E7A6"), SKColor.Parse("#FF6B6B"), SKColor.Parse("#7C3AED"), SKColor.Parse("#FFA62B"),
        SKColor.Parse("#00D9FF"), SKColor.Parse("#FF4560"), SKColor.Parse("#FFC107"), SKColor.Parse("#9B59B6"),
        SKColor.Parse("#1ABC9C"), SKColor.Parse("#3498DB"),
    ];

    private readonly IWaitStatsRepository _repo;
    private readonly long                 _queryId;
    private readonly QueryStorePeriod     _period;
    private readonly QueryStoreStatus     _status;

    private QueryStoreQueryDetails? _details;
    private readonly bool _ready;

    public QueryStoreQueryWindow(IWaitStatsRepository repo, long queryId, QueryStorePeriod period, QueryStoreStatus status)
    {
        _repo    = repo;
        _queryId = queryId;
        _period  = period;
        _status  = status;
        InitializeComponent();

        Title           = $"Query {queryId} · Query Store · {status.DatabaseName}";
        TxtHeading.Text = $"Query {queryId}";

        foreach (var m in QueryStoreMetrics.Available(status.Features.Sql2017Metrics))
            CmbMetric.Items.Add(new ComboBoxItem
            {
                Content = m.Metric == QueryStoreMetric.Executions ? "Executions" : $"Avg {m.NameWithUnit}",
                Tag     = m.Metric,
            });
        CmbMetric.SelectedIndex = QueryStoreMetrics.Available(status.Features.Sql2017Metrics)
            .ToList().FindIndex(m => m.Metric == QueryStoreMetric.Duration);
        _ready = true;
    }

    private QueryStoreMetric SelectedMetric => (QueryStoreMetric)((ComboBoxItem)CmbMetric.SelectedItem).Tag;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "Loading…";
        try
        {
            _details = await _repo.GetQueryStoreQueryDetailsAsync(_queryId, _period);
        }
        catch (Exception ex)
        {
            AppLog.Error($"QueryStore: reading query {_queryId} failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
            return;
        }

        if (_details.Query is not { } query)
        {
            TxtSubheading.Text = $"Query Store for {_status.DatabaseName} has no query {_queryId}. It may have been cleaned up, or the ID is from another database.";
            TxtStatus.Text     = string.Empty;
            return;
        }

        TxtSubheading.Text = $"{query.ObjectText} · {_status.DatabaseName} · {_period.Label}";
        TxtQueryText.Text  = query.QueryText.Trim();
        PlansGrid.ItemsSource = _details.Summaries;
        var plans = _details.Intervals.Select(i => i.PlanId).Distinct().Count();
        TxtStatus.Text = $"{plans:N0} {(plans == 1 ? "plan" : "plans")} used in {_period.Label.ToLower(CultureInfo.CurrentCulture)} · one point per Query Store interval";
        DrawChart();
    }

    private void CmbMetric_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            DrawChart();
    }

    // One line per plan: the metric's average per execution in each interval. The forced plan's
    // line is thicker, and "(forced)" in the legend.
    private void DrawChart()
    {
        if (_details is not { Query: not null } details || CmbMetric.SelectedItem is null)
            return;

        if (details.Intervals.Count == 0)
        {
            Chart.Series = [];
            TxtNoData.Text       = $"The query didn't run in {_period.Label.ToLower(CultureInfo.CurrentCulture)}.";
            TxtNoData.Visibility = Visibility.Visible;
            return;
        }
        TxtNoData.Visibility = Visibility.Collapsed;

        var metric = SelectedMetric;
        var info   = QueryStoreMetrics.Info(metric);
        var forced = details.Plans.Where(p => p.IsForced).Select(p => p.PlanId).ToHashSet();

        var series = details.Intervals
            .GroupBy(i => i.PlanId)
            .OrderBy(g => g.Key)
            .Select((g, index) =>
            {
                var color    = Palette[index % Palette.Length];
                var isForced = forced.Contains(g.Key);
                return (ISeries)new LineSeries<DateTimePoint>
                {
                    Values         = g.OrderBy(i => i.StartUtc).Select(i => new DateTimePoint(i.StartUtc.ToLocalTime(), i.Totals.Average(metric))).ToList(),
                    Name           = isForced ? $"Plan {g.Key} (forced)" : $"Plan {g.Key}",
                    Stroke         = new SolidColorPaint(color) { StrokeThickness = isForced ? 3.5f : 1.8f },
                    Fill           = null,
                    GeometrySize   = isForced ? 8 : 5,
                    GeometryFill   = new SolidColorPaint(color),
                    GeometryStroke = null,
                    LineSmoothness = 0,
                };
            })
            .ToList();

        var (fromUtc, toUtc) = _period.Resolve(DateTime.UtcNow);
        var axisFormat = ConnectionHistory.AxisFormat(toUtc - fromUtc);
        var first = details.Intervals.Min(i => i.StartUtc);

        Chart.Series = series;
        // The legend names the plans, so it must read on the dark card.
        Chart.LegendTextPaint = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true };
        Chart.XAxes =
        [
            new DateTimeAxis(TimeSpan.FromMinutes(1), dt => dt.ToString(axisFormat, CultureInfo.CurrentCulture))
            {
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = (fromUtc < first ? fromUtc : first).ToLocalTime().Ticks,
                MaxLimit        = toUtc.ToLocalTime().Ticks,
            },
        ];
        Chart.YAxes =
        [
            new Axis
            {
                Name            = metric == QueryStoreMetric.Executions ? info.Name : $"Avg {info.NameWithUnit}",
                NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                NameTextSize    = 11,
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            },
        ];
    }

    private void BtnCopyText_Click(object sender, RoutedEventArgs e)
    {
        if (_details?.Query is { } query)
            ClipboardHelper.SetText(query.QueryText);
    }

    private async void ViewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryStorePlanSummary plan } btn)
            await QueryStorePage.ShowPlanAsync(_repo, btn, plan.PlanId, _queryId, _details?.Query?.QueryText);
    }
}
