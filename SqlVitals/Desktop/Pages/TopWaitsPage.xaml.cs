using System.Globalization;
using System.Windows;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

public partial class TopWaitsPage : System.Windows.Controls.Page, IRefreshable, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#149): the top wait category and what it usually means.
    internal TopWaitsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the chart's folds are remembered; none in some tests.</param>
    /// <param name="navigator">Where the verdict's actions go; the app's (#182) when null.</param>
    public TopWaitsPage(IWaitStatsRepository repo, ISectionFolds? folds = null, INavigator? navigator = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new TopWaitsViewModel(navigator ?? CompositionRoot.Get<INavigator>());
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        List<Engine.Models.TopWaitType> data;
        try
        {
            data = (await _repo.GetTopWaitTypesAsync()).ToList();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the wait statistics", ex);
            throw;
        }
        ViewModel.Show(data);
        WaitsGrid.ItemsSource = data;

        var top15 = data.Take(15).ToList();
        var labels = top15.Select(w => w.WaitType).ToArray();
        var values = top15.Select(w => w.WaitTimeSec).ToArray();

        // Folded, the chart says it in one line (#149): "PAGEIOLATCH_SH 41.0 % · WRITELOG 12.3 %".
        ChartSection.FoldedSummary = data.Count == 0 ? "No waits worth noting"
            : string.Join(" · ", data.Take(3).Select(w => string.Format(CultureInfo.CurrentCulture, "{0} {1:N1} %", w.WaitType, w.PctOfTotal)));

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
