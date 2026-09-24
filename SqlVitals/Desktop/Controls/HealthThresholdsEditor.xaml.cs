using System.Windows;
using System.Windows.Controls;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A warning and a critical box for each health indicator. Used in Settings for the thresholds
/// every connection shares, and in the connection form for one connection's own.
/// </summary>
public partial class HealthThresholdsEditor : UserControl
{
    private readonly Dictionary<HealthIndicator, (TextBox Warning, TextBox Critical)> _boxes = new();

    public HealthThresholdsEditor()
    {
        InitializeComponent();

        foreach (var info in HealthThresholds.Indicators)
            AddRow(info);

        Show(HealthThresholds.Default);
    }

    /// <summary>Fills the boxes; a level that is off shows blank.</summary>
    public void Show(HealthThresholds thresholds)
    {
        foreach (var (indicator, boxes) in _boxes)
        {
            var threshold = thresholds.Get(indicator);
            boxes.Warning.Text  = HealthThresholds.Format(threshold.Warning);
            boxes.Critical.Text = HealthThresholds.Format(threshold.Critical);
        }
    }

    /// <summary>
    /// Reads the boxes. Returns false with a message naming the first indicator that can't be
    /// used, and moves the focus to it.
    /// </summary>
    public bool TryRead(out HealthThresholds? thresholds, out string error)
    {
        var result = HealthThresholds.Default;
        foreach (var info in HealthThresholds.Indicators)
        {
            var boxes = _boxes[info.Indicator];
            if (!HealthThresholds.TryParse(info.Indicator, boxes.Warning.Text, boxes.Critical.Text, out var threshold, out error))
            {
                thresholds = null;
                boxes.Warning.Focus();
                return false;
            }
            result = result.With(info.Indicator, threshold!);
        }

        thresholds = result;
        error      = string.Empty;
        return true;
    }

    private void AddRow(HealthIndicatorInfo info)
    {
        GridRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = GridRows.RowDefinitions.Count - 1;

        var name = new TextBlock
        {
            Text              = info.Name,
            FontSize          = 12,
            Margin            = new Thickness(0, 7, 12, 8),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip           = info.Hint,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Place(name, row, 0);

        var warning  = Box(info);
        var critical = Box(info);
        Place(Cell(info, warning),  row, 1);
        Place(Cell(info, critical), row, 2);

        var hint = new TextBlock
        {
            Text              = info.Hint,
            FontSize          = 10,
            TextWrapping      = TextWrapping.Wrap,
            Margin            = new Thickness(0, 2, 0, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Place(hint, row, 3);

        _boxes[info.Indicator] = (warning, critical);
    }

    // "≥ [ 75 ] %", or "< [ 300 ] s" for an indicator where lower is worse.
    private static StackPanel Cell(HealthIndicatorInfo info, TextBox box)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 8) };
        cell.Children.Add(Muted(info.LowerIsWorse ? "<" : "≥", new Thickness(0, 0, 4, 0)));
        cell.Children.Add(box);
        if (info.Unit.Length > 0)
            cell.Children.Add(Muted(info.Unit, new Thickness(4, 0, 0, 0)));
        return cell;
    }

    private static TextBox Box(HealthIndicatorInfo info)
    {
        var box = new TextBox
        {
            Width                    = 64,
            Height                   = 28,
            FontSize                 = 12,
            Padding                  = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(1),
            ToolTip                  = $"{info.Name}: leave blank to turn this level off.",
        };
        box.SetResourceReference(BackgroundProperty,        "BgDeep");
        box.SetResourceReference(ForegroundProperty,        "TextPrimary");
        box.SetResourceReference(BorderBrushProperty,       "Border");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
        return box;
    }

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
