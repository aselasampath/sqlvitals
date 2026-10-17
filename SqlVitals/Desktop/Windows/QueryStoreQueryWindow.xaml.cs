using System.Globalization;
using System.Windows;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.ViewModels.Windows;

namespace SqlVitals.Desktop.Windows;

/// <summary>
/// One Query Store query (#111): its <see cref="QueryStoreQueryViewModel"/>, read once the window
/// is on screen. The chart is drawn here, in the theme's colours: one line per plan, the forced
/// plan's thicker.
/// </summary>
public partial class QueryStoreQueryWindow : Window
{
    public QueryStoreQueryWindow(QueryStoreQueryViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueryStoreQueryViewModel.Chart))
                DrawChart();
        };
    }

    internal QueryStoreQueryViewModel ViewModel { get; }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private void DrawChart()
    {
        var chart = ViewModel.Chart;
        Chart.Series = chart.Lines
            .Select(line =>
            {
                var color = ChartTheme.Series(line.Colour);
                return (ISeries)new LineSeries<DateTimePoint>
                {
                    Values         = line.Points,
                    Name           = line.Name,
                    Stroke         = new SolidColorPaint(color) { StrokeThickness = line.Emphasised ? 3.5f : 1.8f },
                    Fill           = null,
                    GeometrySize   = line.Emphasised ? 8 : 5,
                    GeometryFill   = new SolidColorPaint(color),
                    GeometryStroke = null,
                    LineSmoothness = 0,
                };
            })
            .ToList();
        if (chart.Lines.Count == 0)
            return;

        // The legend names the plans, so it must read on the dark card.
        Chart.LegendTextPaint = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true };
        Chart.XAxes =
        [
            new DateTimeAxis(TimeSpan.FromMinutes(1), dt => dt.ToString(chart.AxisFormat, CultureInfo.CurrentCulture))
            {
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = chart.From?.Ticks,
                MaxLimit        = chart.To?.Ticks,
            },
        ];
        Chart.YAxes =
        [
            new Axis
            {
                Name            = chart.ValueName,
                NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                NameTextSize    = 11,
                LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                TextSize        = 11,
                MinLimit        = 0,
            },
        ];
    }
}
