using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class TopWaitsPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public TopWaitsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var data = (await _repo.GetTopWaitTypesAsync()).ToList();
        WaitsGrid.ItemsSource = data;

        var top15 = data.Take(15).ToList();
        var labels = top15.Select(w => w.WaitType).ToArray();
        var values = top15.Select(w => w.WaitTimeSec).ToArray();

        // Single series with all values — one bar per X-label position
        TopChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values      = values,
                Name        = "Wait Time (sec)",
                Fill        = new SolidColorPaint(SKColor.Parse("#7C3AED")),
                Stroke      = null,
                MaxBarWidth = 30,
                Padding     = 1,
            }
        };

        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        TopChart.XAxes = new[] { new Axis
        {
            Labels      = labels,
            LabelsPaint = new SolidColorPaint(axisColor),
            SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
            TextSize    = 9,
            LabelsRotation = 30,
        }};
        TopChart.YAxes = new[] { new Axis
        {
            LabelsPaint = new SolidColorPaint(axisColor),
            SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
            TextSize    = 11,
        }};
    }
}
