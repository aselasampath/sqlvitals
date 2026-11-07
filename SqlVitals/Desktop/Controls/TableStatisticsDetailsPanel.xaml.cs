using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Table Statistics' Statistics tab (#243), bound to <see cref="TableStatisticsDetailsViewModel"/>. The
/// code-behind applies the choice of steps to the histogram grid's view, says how many rows show, and
/// shows the verdict, which has no binding of its own.
/// </summary>
public partial class TableStatisticsDetailsPanel : UserControl
{
    private TableStatisticsDetailsViewModel? _model;

    public TableStatisticsDetailsPanel()
    {
        InitializeComponent();

        HistogramFilter.FilterChanged += (_, _) => _model?.StepsShown(HistogramGrid.Items.Count);
        ((INotifyCollectionChanged)HistogramGrid.Items).CollectionChanged += (_, _) => _model?.StepsShown(HistogramGrid.Items.Count);
        DensityFilter.FilterChanged += (_, _) => _model?.DensityShown(DensityGrid.Items.Count);
        ((INotifyCollectionChanged)DensityGrid.Items).CollectionChanged += (_, _) => _model?.DensityShown(DensityGrid.Items.Count);

        DataContextChanged += (_, e) => Follow(e.NewValue as TableStatisticsDetailsViewModel);

        KeepColumnWidths(HistogramGrid);
        KeepColumnWidths(DensityGrid);
    }

    // A DataGrid takes width from its fixed columns when it's briefly given too little, as the page first
    // lays out, and doesn't give it back (#243). None to take: a window truly too narrow scrolls the grid.
    private static void KeepColumnWidths(DataGrid grid)
    {
        foreach (var column in grid.Columns.Where(c => c.Width.IsAbsolute))
            column.MinWidth = column.Width.Value;
    }

    private void Follow(TableStatisticsDetailsViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= Model_PropertyChanged;
            _model.StepFiltersChanged -= ApplyStepFilters;
        }
        _model = model;
        if (model is null)
            return;
        model.PropertyChanged += Model_PropertyChanged;
        model.StepFiltersChanged += ApplyStepFilters;
        ObjectVerdict.Show(model.ObjectVerdict);
        ApplyStepFilters();
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableStatisticsDetailsViewModel.ObjectVerdict))
            ObjectVerdict.Show(_model?.ObjectVerdict);
    }

    private void ApplyStepFilters()
    {
        if (_model is { } model)
            HistogramFilter.Refilter(model.HasStepFilters ? model.ShowsStep : null);
    }
}
