using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Query Store (#111), bound to <see cref="QueryStoreViewModel"/> (#189): the code-behind keeps the
/// details drawer, the ranked columns' headers and formats, and draws the Over Time charts in the
/// theme's colours.
/// </summary>
public partial class QueryStorePage : Page, IPageContext, IFocusPage, IDetailsSource
{
    internal QueryStoreViewModel ViewModel { get; }

    /// <param name="folds">Where the health section's fold is remembered; none in some tests.</param>
    public QueryStorePage(QueryStoreViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        foreach (var grid in new[] { TopGrid, VariationGrid, WaitQueriesGrid, ForcedGrid, HintsGrid })
            OpenOnClick(grid);

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ShowTopColumns();
        ShowVariationColumns();
        ViewModel.CustomPeriodOpened += () => FromTime.Focus();
        ViewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(QueryStoreViewModel.Selected):
                    if (ViewModel.Selected is { } row)
                        ShowQuery(row, open: false);
                    break;
                case nameof(QueryStoreViewModel.TopMetricHeader) or nameof(QueryStoreViewModel.TopMetricFormat) or nameof(QueryStoreViewModel.ShowsTotalCpu):
                    ShowTopColumns();
                    break;
                case nameof(QueryStoreViewModel.VariationMetric):
                    ShowVariationColumns();
                    break;
                case nameof(QueryStoreViewModel.OverTime):
                    DrawCharts();
                    break;
            }
        };
    }

    // ── The top bar (#140) and a link's focus (#145) ──────────────────────────
    // A period of the connection's database's Query Store; the period box stays on the page.

    public PageKind Kind => PageKind.History;
    public bool UsesDatabase => true;
    public string? HistoryWindow => ViewModel.PeriodLabel;
    public DateTime? DataTo => ViewModel.DataTo;

    public void ShowFocus(PageFocus focus) => ViewModel.ShowFocus(focus);
    public void ClearFocus() => ViewModel.ClearFocus();
    public string? FocusNote => ViewModel.FocusNote;
    public string? ShowAllText => ViewModel.ShowAllText;

    // ── The details drawer (#147): the selected query ─────────────────────────
    // A row selected in any tab: its figures there, its text and its plans in the period. It stays
    // until another row is selected, so a reload or another tab doesn't lose it.
    private const string SelectQueryHint = "Select a query in any tab to see its text, its figures and its plans.";

    private QueryDetailsView? _queryView;

    public DrawerContent Details { get; private set; } = new("Query details", null, SelectQueryHint);

    public event Action<bool>? DetailsChanged;

    /// <summary>The drawer's latest read of a query's plans, for tests.</summary>
    internal Task DetailsLoaded { get; private set; } = Task.CompletedTask;

    private void ShowQuery(object row, bool open)
    {
        if (QueryStoreDetails.QueryOf(row) is not { } query)
            return;
        _queryView ??= new QueryDetailsView(
            ViewModel.ReadPlansAsync,
            (button, planId, queryId, text) => BusyButton.WhileAsync(button, () => ViewModel.ShowPlanAsync(planId, queryId, text)),
            ViewModel.OpenQuery);
        DetailsLoaded = _queryView.ShowAsync(row, ViewModel.PeriodName);
        Details = new($"Query {query.QueryId}", _queryView, SelectQueryHint);
        DetailsChanged?.Invoke(open);
    }

    // A click on a row also opens the drawer.
    private void OpenOnClick(DataGrid grid) =>
        grid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(grid, source) is DataGridRow { Item: { } row })
                ShowQuery(row, open: true);
        }), handledEventsToo: true);

    // A row double-clicked: its query's window. Only a row: not the header, the scroll bar or a button in a row.
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(grid, source) is DataGridRow row)
            ViewModel.OpenRowCommand.Execute(row.Item);
    }

    // ── The columns that follow what was read ─────────────────────────────────

    private void ShowTopColumns()
    {
        TxtMetricHeader.Text   = ViewModel.TopMetricHeader;
        ColMetric.Binding      = new Binding(nameof(QueryStoreQueryRow.MetricValue)) { StringFormat = ViewModel.TopMetricFormat };
        ColTotalCpu.Visibility = ViewModel.ShowsTotalCpu ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowVariationColumns()
    {
        var info = ViewModel.VariationMetric;
        var unit = info.Unit.Length == 0 ? string.Empty : $" {info.Unit}";
        foreach (var (column, name, path) in new[]
                 {
                     (ColVarAvg, "Avg", nameof(QueryStoreVariationRow.Avg)),
                     (ColVarMin, "Min", nameof(QueryStoreVariationRow.Min)),
                     (ColVarMax, "Max", nameof(QueryStoreVariationRow.Max)),
                 })
        {
            column.Header  = name + unit;
            column.Binding = new Binding(path) { StringFormat = info.Format };
        }
        ((TextBlock)ColVarStdDev.Header).Text = "Std dev" + unit;
        ColVarStdDev.Binding = new Binding(nameof(QueryStoreVariationRow.StdDev)) { StringFormat = info.Format };
    }

    // ── Over Time, in the theme's colours ─────────────────────────────────────

    // One chart per metric ticked, stacked, all on the same time axis.
    private void DrawCharts()
    {
        ChartsPanel.Children.Clear();
        var view = ViewModel.OverTime;
        if (view.Empty is { } empty)
        {
            ChartsPanel.Children.Add(new TextBlock
            {
                Text = empty, FontSize = 14, Margin = new Thickness(0, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            }.WithForeground("TextMuted"));
            return;
        }

        foreach (var line in view.Charts)
        {
            var color = ChartTheme.Series(line.Colour);
            var chart = new LiveChartsCore.SkiaSharpView.WPF.CartesianChart
            {
                Height          = 170,
                Margin          = new Thickness(0, 0, 0, 6),
                Background      = Brushes.Transparent,
                TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Top,
                LegendPosition  = LiveChartsCore.Measure.LegendPosition.Hidden,
                AnimationsSpeed = TimeSpan.Zero,
                Cursor          = Cursors.Hand,
                Series =
                [
                    new LineSeries<DateTimePoint>
                    {
                        Values         = line.Points,
                        Name           = line.Name,
                        Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.8f },
                        Fill           = new SolidColorPaint(color.WithAlpha(40)),
                        GeometrySize   = 4,
                        GeometryFill   = new SolidColorPaint(color),
                        GeometryStroke = null,
                        LineSmoothness = 0.3,
                    },
                ],
                XAxes =
                [
                    new DateTimeAxis(view.Slot, dt => dt.ToString(view.AxisFormat, CultureInfo.CurrentCulture))
                    {
                        LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                        SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                        TextSize        = 11,
                        MinLimit        = view.From.Ticks,
                        MaxLimit        = view.To.Ticks,
                    },
                ],
                YAxes =
                [
                    new Axis
                    {
                        Name            = line.Name,
                        NamePaint       = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                        NameTextSize    = 11,
                        LabelsPaint     = new SolidColorPaint(ChartTheme.AxisColor) { IsAntialias = true },
                        SeparatorsPaint = new SolidColorPaint(ChartTheme.GridColor) { StrokeThickness = 1 },
                        TextSize        = 11,
                        MinLimit        = 0,
                    },
                ],
            };
            chart.DataPointerDown += Chart_DataPointerDown;
            ChartsPanel.Children.Add(chart);
        }
    }

    // A point opens Top Queries for its time slot.
    private async void Chart_DataPointerDown(IChartView chart, IEnumerable<ChartPoint>? points)
    {
        if (points?.FirstOrDefault()?.Context.DataSource is not DateTimePoint { Value: not null } point)
            return;
        await ViewModel.ReportErrorsOf(ViewModel.PickSlotAsync(point.DateTime));
    }
}
