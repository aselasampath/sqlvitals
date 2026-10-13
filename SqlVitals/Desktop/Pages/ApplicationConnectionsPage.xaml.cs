using System.ComponentModel;
using System.Windows.Controls;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// App Connections, bound to <see cref="ApplicationConnectionsViewModel"/> (#185): the code-behind
/// draws the chart of the applications holding the most connections, in the theme's colours.
/// </summary>
public partial class ApplicationConnectionsPage : Page, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal ApplicationConnectionsViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's, the counts' and the chart's folds are remembered; none in some tests.</param>
    public ApplicationConnectionsPage(ApplicationConnectionsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationConnectionsViewModel.ChartApplications))
            DrawChart();
    }

    // Chart glue: the connections of each application charted, in the theme's colours.
    private void DrawChart()
    {
        var top = ViewModel.ChartApplications;
        var axisColor = ChartTheme.MutedAxisColor;
        var gridColor = ChartTheme.GridColor;

        ConnectionsChart.Series = new ISeries[]
        {
            new ColumnSeries<int>
            {
                Values      = top.Select(d => d.CurrentConnections).ToArray(),
                Name        = "Connections",
                Fill        = new SolidColorPaint(ChartTheme.Of("AccentColor")),
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

    private static string TruncateLabel(string text, int maxLength) =>
        string.IsNullOrWhiteSpace(text) ? "(empty)"
        : text.Length <= maxLength ? text
        : text[..(maxLength - 1)] + "…";
}
