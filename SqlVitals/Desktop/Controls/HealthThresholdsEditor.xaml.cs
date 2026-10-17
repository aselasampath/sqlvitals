using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The Checks table (#130, #131, #173, #174) drawn from its <see cref="HealthThresholdsViewModel"/>,
/// the <c>DataContext</c>: a row per check, each cell bound to the row (#190). Only view code here:
/// the grid, the Now and Points columns shown or not, and the focus on a box that can't be read.
/// </summary>
public partial class HealthThresholdsEditor : UserControl
{
    private readonly Dictionary<ThresholdField, TextBox> _boxes = new();
    private HealthThresholdsViewModel? _model;

    public HealthThresholdsEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Build(DataContext as HealthThresholdsViewModel);
    }

    /// <summary>The table drawn; null before it has a view model.</summary>
    public HealthThresholdsViewModel? Model => _model;

    /// <summary>A box as drawn, for tests.</summary>
    internal TextBox BoxOf(ThresholdField field) => _boxes[field];

    private void Build(HealthThresholdsViewModel? model)
    {
        if (ReferenceEquals(model, _model))
            return;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.FocusRequested  -= Focus;
        }

        _model = model;
        _boxes.Clear();
        GridRows.Children.Clear();
        GridRows.RowDefinitions.Clear();
        if (model is null)
            return;

        model.PropertyChanged += OnModelChanged;
        model.FocusRequested  += Focus;

        AddHeader();
        foreach (var row in model.Rows)
        {
            AddRow(model, row);
            if (row.Indicator == HealthIndicator.DiskFree)
                AddGbRow(model);
        }
        ShowSampleColumns(model.ShowsSample);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HealthThresholdsViewModel.ShowsSample) && _model is { } model)
            ShowSampleColumns(model.ShowsSample);
    }

    // Once its tab is on screen: a failed Save moves to the Health thresholds tab first.
    private void Focus(ThresholdField field)
    {
        if (_boxes.TryGetValue(field, out var box))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => box.Focus());
    }

    // The Now and Points columns, for one connection's sample.
    private void ShowSampleColumns(bool shown)
    {
        ColNow.Width    = shown ? new GridLength(110) : new GridLength(0);
        ColPoints.Width = shown ? new GridLength(64) : new GridLength(0);
        foreach (var cell in GridRows.Children.OfType<FrameworkElement>()
                                     .Where(c => Grid.GetColumn(c) is 1 or 5 && Grid.GetColumnSpan(c) == 1))
            cell.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        foreach (var dot in GridRows.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<Ellipse>()))
            dot.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Building the table ────────────────────────────────────────────────────

    private int NewRow()
    {
        GridRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return GridRows.RowDefinitions.Count - 1;
    }

    private void AddHeader()
    {
        var row = NewRow();
        string[] headers = ["CHECK", "NOW", "WARNING", "CRITICAL", "WEIGHT", "POINTS"];
        for (var column = 0; column < headers.Length; column++)
        {
            var header = Text(headers[column], 10, column >= 4);
            header.FontWeight = FontWeights.SemiBold;
            header.Margin     = new Thickness(0, 0, column >= 4 ? 0 : 8, 8);
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            if (column == 4)
                header.ToolTip = "What each check is worth in the score. Weights are relative: the score is always out of 100, " +
                                 "shared over the checks that reported. 0 leaves a check out of the score.";
            Place(header, row, column);
        }
    }

    // A row per check: its name, default and dot; Now; the boxes; Points.
    private void AddRow(HealthThresholdsViewModel model, ThresholdRow vm)
    {
        var row = NewRow();

        // A line above each check, across the table.
        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
        line.SetResourceReference(Border.BorderBrushProperty, "Border");
        Place(line, row, 0);
        Grid.SetColumnSpan(line, 6);

        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.SetBinding(Shape.FillProperty, new Binding(nameof(ThresholdRow.Grade))
        {
            Converter     = (IValueConverter)Resources["HealthBrush"],
            FallbackValue = Brushes.Transparent,
        });
        var name = Text(vm.Name, 12, right: false);
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var defaults = Text(string.Empty, 10, right: false);
        defaults.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        defaults.Margin = new Thickness(0, 2, 0, 0);
        defaults.SetBinding(TextBlock.TextProperty, nameof(ThresholdRow.DefaultText));
        BindShown(defaults, model, nameof(HealthThresholdsViewModel.ShowsDefaults));
        var names = new StackPanel { Children = { name, defaults } };
        if (vm.IsPageLifeExpectancy)
            names.Children.Add(PleToggle(model));
        var nameCell = new DockPanel { Margin = new Thickness(0, 9, 8, 9), ToolTip = vm.Hint, Background = Brushes.Transparent, DataContext = vm };
        DockPanel.SetDock(dot, Dock.Left);
        nameCell.Children.Add(dot);
        nameCell.Children.Add(names);
        Place(nameCell, row, 0);

        var now = new TextBlock { Style = (Style)Resources["Now"], DataContext = vm };
        Place(now, row, 1);

        Place(Cell(vm.Info, Box(vm.Warning, 64)),  row, 2);
        Place(Cell(vm.Info, Box(vm.Critical, 64)), row, 3);

        var weight = Box(vm.Weight, 44);
        weight.HorizontalAlignment        = HorizontalAlignment.Right;
        weight.HorizontalContentAlignment = HorizontalAlignment.Right;
        Place(weight, row, 4);

        var points = new TextBlock { Style = (Style)Resources["Points"], DataContext = vm };
        Place(points, row, 5);
    }

    // Disk free's GB thresholds (#174), under its row: "and free space below [ 50 ] GB", with no weight of their own.
    private void AddGbRow(HealthThresholdsViewModel model)
    {
        var row  = NewRow();
        var info = HealthThresholdsViewModel.GbInfo;

        var name = Text("and free space below", 12, right: false);
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var defaults = Text(string.Empty, 10, right: false);
        defaults.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        defaults.Margin = new Thickness(0, 2, 0, 0);
        defaults.SetBinding(TextBlock.TextProperty, nameof(HealthThresholdsViewModel.GbDefaultText));
        BindShown(defaults, model, nameof(HealthThresholdsViewModel.ShowsDefaults));
        var names = new StackPanel { Margin = new Thickness(16, 0, 8, 9), ToolTip = info.Hint, Background = Brushes.Transparent,
                                     Children = { name, defaults }, DataContext = model };
        Place(names, row, 0);

        Place(Cell(info, Box(model.GbWarning, 64)),  row, 2);
        Place(Cell(info, Box(model.GbCritical, 64)), row, 3);
    }

    // "Per 4 GB of buffer pool" under page life expectancy's name (#173).
    private static CheckBox PleToggle(HealthThresholdsViewModel model)
    {
        var toggle = new CheckBox
        {
            Content     = "Per 4 GB of buffer pool",
            FontSize    = 11,
            Margin      = new Thickness(0, 4, 0, 0),
            DataContext = model,
            ToolTip     = "On: the thresholds are seconds per 4 GB of buffer pool, so a 64 GB server is warned below " +
                          "4,800 s (300 × 64 ÷ 4) and a 2 GB one below 150 s. Off: the seconds are used as they are.",
        };
        toggle.SetResourceReference(ForegroundProperty, "TextPrimary");
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(HealthThresholdsViewModel.PleScaled));
        toggle.SetBinding(IsEnabledProperty, nameof(HealthThresholdsViewModel.CanEdit));
        AutomationProperties.SetName(toggle, "Page life expectancy thresholds per 4 GB of buffer pool");
        return toggle;
    }

    // "≥ [ 75 ] %", or "< [ 300 ] s" for a check where lower is worse; "off" through a blank box.
    private StackPanel Cell(HealthIndicatorInfo info, TextBox box)
    {
        var off    = new TextBlock { Style = (Style)Resources["OffHint"], DataContext = box.DataContext };
        var holder = new Grid { Children = { box, off } };
        var cell   = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0),
                                      VerticalAlignment = VerticalAlignment.Center };
        cell.Children.Add(Muted(info.LowerIsWorse ? "<" : "≥", new Thickness(0, 0, 4, 0)));
        cell.Children.Add(holder);
        if (info.Unit.Length > 0)
            cell.Children.Add(Muted(info.Unit, new Thickness(4, 0, 0, 0)));
        return cell;
    }

    private TextBox Box(ThresholdField field, double width)
    {
        var box = new TextBox { Width = width, DataContext = field, Style = (Style)Resources["ThresholdBox"] };
        _boxes[field] = box;
        return box;
    }

    private void BindShown(FrameworkElement element, HealthThresholdsViewModel model, string path) =>
        element.SetBinding(VisibilityProperty, new Binding(path) { Source = model, Converter = (IValueConverter)Resources["Shown"] });

    private static TextBlock Text(string text, double size, bool right) => new()
    {
        Text                = text,
        FontSize            = size,
        TextWrapping        = TextWrapping.Wrap,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
    };

    private static TextBlock Muted(string text, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return block;
    }

    private void Place(UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        GridRows.Children.Add(element);
    }
}
