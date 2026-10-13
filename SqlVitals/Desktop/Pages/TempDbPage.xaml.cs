using System.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// TempDB Pressure, bound to <see cref="TempDbViewModel"/> (#184): the code-behind draws the file
/// chart in the theme's colours and scrolls the session the verdict shows into view.
/// </summary>
public partial class TempDbPage : System.Windows.Controls.Page, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal TempDbViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's, the counters' and the chart's folds are remembered; none in some tests.</param>
    public TempDbPage(TempDbViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.ShowRow += SessionsGrid.ScrollIntoView;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TempDbViewModel.Files))
            DrawFiles();
    }

    // Chart glue: the used and free space of each file, stacked, in the theme's colours.
    private void DrawFiles()
    {
        var files = ViewModel.Files;
        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        FileChart.Series = new ISeries[]
        {
            new StackedColumnSeries<double>
            {
                Values      = files.Select(f => (double)f.SpaceUsedMB).ToArray(),
                Name        = "Used MB",
                Fill        = new SolidColorPaint(ChartTheme.Of("DangerColor")),
                Stroke      = null,
                MaxBarWidth = 60,
                Padding     = 4,
            },
            new StackedColumnSeries<double>
            {
                Values      = files.Select(f => (double)f.FreeSpaceMB).ToArray(),
                Name        = "Free MB",
                Fill        = new SolidColorPaint(ChartTheme.Of("SuccessColor")),
                Stroke      = null,
                MaxBarWidth = 60,
                Padding     = 4,
            },
        };
        FileChart.XAxes = new[] { new Axis
        {
            Labels          = files.Select(f => f.FileName).ToArray(),
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
}
