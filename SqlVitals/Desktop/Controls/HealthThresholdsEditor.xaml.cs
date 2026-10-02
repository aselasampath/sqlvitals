using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The Checks table of a server's detail page (#130) with a warning and a critical box per check
/// (#131): Check, Now, Warning, Critical, Weight, Points. In Settings for the thresholds every
/// connection shares, and on a connection's Health thresholds tab for its own, where Now and
/// Points show its latest sample graded on what's in the boxes.
/// </summary>
public partial class HealthThresholdsEditor : UserControl
{
    private sealed record Row(TextBox Warning, TextBox Critical, Ellipse Dot, TextBlock Now, TextBlock Points, TextBlock Default);

    private readonly Dictionary<HealthIndicator, Row> _rows = new();
    private HealthThresholds _defaults = HealthThresholds.Default;
    private HealthScore?     _latest;
    private bool             _showsSample = true;
    private bool             _isReadOnly;

    public HealthThresholdsEditor()
    {
        InitializeComponent();

        AddRow(null);
        foreach (var info in HealthThresholds.Indicators)
            AddRow(info);

        Show(HealthThresholds.Default);
    }

    /// <summary>A box was typed in, or <see cref="Show"/> filled them.</summary>
    public event Action? Changed;

    /// <summary>
    /// What a box is compared with: one that differs has an accent border, and the line under each
    /// check's name shows these when <see cref="ShowsDefaults"/> is on.
    /// </summary>
    public HealthThresholds Defaults
    {
        get => _defaults;
        set
        {
            _defaults = value;
            foreach (var (indicator, row) in _rows)
                row.Default.Text = "Default: " + Describe(indicator, value.Get(indicator));
            UpdateBorders();
        }
    }

    /// <summary>The "Default: ≥ 75 % / ≥ 90 %" line under each check's name.</summary>
    public bool ShowsDefaults
    {
        get => _rows.Values.First().Default.Visibility == Visibility.Visible;
        set
        {
            foreach (var row in _rows.Values)
                row.Default.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The Now and Points columns, for one connection's sample. Off for the thresholds every connection shares.</summary>
    public bool ShowsSample
    {
        get => _showsSample;
        set
        {
            _showsSample = value;
            ColNow.Width    = value ? new GridLength(110) : new GridLength(0);
            ColPoints.Width = value ? new GridLength(64) : new GridLength(0);
            foreach (var cell in GridRows.Children.OfType<FrameworkElement>()
                                         .Where(c => Grid.GetColumn(c) is 1 or 5 && Grid.GetColumnSpan(c) == 1))
                cell.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in _rows.Values)
                row.Dot.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The boxes can't be typed in: a connection using the default thresholds shows them read-only.</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        set
        {
            _isReadOnly = value;
            foreach (var row in _rows.Values)
                foreach (var box in new[] { row.Warning, row.Critical })
                {
                    box.IsReadOnly = value;
                    box.Focusable  = !value;
                    if (value)
                        box.Background = Brushes.Transparent;
                    else
                        box.SetResourceReference(BackgroundProperty, "BgDeep");
                }
            UpdateBorders();
        }
    }

    /// <summary>Fills the boxes; a level that is off shows blank.</summary>
    public void Show(HealthThresholds thresholds)
    {
        foreach (var (indicator, row) in _rows)
        {
            var threshold = thresholds.Get(indicator);
            row.Warning.Text  = HealthThresholds.Format(threshold.Warning);
            row.Critical.Text = HealthThresholds.Format(threshold.Critical);
        }
        UpdateBorders();
    }

    /// <summary>
    /// The connection's latest score: Now shows its figures, and <see cref="ShowGrades"/> its checks
    /// graded on the boxes. Null with no sample: Now and Points show a dash.
    /// </summary>
    public void ShowSample(HealthScore? latest, HealthScore? graded)
    {
        _latest = latest is { Level: not HealthLevel.Unavailable, Checks.Count: > 0 } ? latest : null;
        foreach (var (indicator, row) in _rows)
            row.Now.Text = _latest?.Checks.FirstOrDefault(c => c.Indicator == indicator)?.ValueText ?? "—";
        ShowGrades(_latest is null ? null : graded);
    }

    /// <summary>Each check's dot and points, as graded on the boxes; null clears them.</summary>
    public void ShowGrades(HealthScore? graded)
    {
        foreach (var (indicator, row) in _rows)
        {
            var check = graded?.Checks.FirstOrDefault(c => c.Indicator == indicator);
            row.Dot.Fill          = check is null ? Brushes.Transparent : HealthBrushes.For(check.Level);
            row.Points.Text       = check is null ? "—" : check.PointsLost > 0 ? $"−{check.PointsLost}" : "0";
            row.Points.FontWeight = check?.PointsLost > 0 ? FontWeights.SemiBold : FontWeights.Normal;
            if (check is { PointsLost: > 0 })
                row.Points.Foreground = HealthBrushes.For(check.Level);
            else
                row.Points.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            row.Now.FontWeight = check?.PointsLost > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>A check's warning or critical box.</summary>
    internal TextBox Box(HealthIndicator indicator, bool critical = false) =>
        critical ? _rows[indicator].Critical : _rows[indicator].Warning;

    /// <summary>A check's "Now" and "Points" cells, as shown.</summary>
    internal (string Now, string Points) Cells(HealthIndicator indicator) => (_rows[indicator].Now.Text, _rows[indicator].Points.Text);

    /// <summary>Every box's text, to tell whether anything was typed since it was last saved.</summary>
    public string Snapshot => string.Join("|", _rows.Values.Select(r => $"{r.Warning.Text.Trim()};{r.Critical.Text.Trim()}"));

    /// <summary>
    /// Reads the boxes. Returns false with a message naming the first check that can't be used,
    /// and, when <paramref name="focus"/>, moves the focus to it.
    /// </summary>
    public bool TryRead(out HealthThresholds? thresholds, out string error, bool focus = true)
    {
        var result = HealthThresholds.Default;
        foreach (var info in HealthThresholds.Indicators)
        {
            var row = _rows[info.Indicator];
            if (!HealthThresholds.TryParse(info.Indicator, row.Warning.Text, row.Critical.Text, out var threshold, out error))
            {
                thresholds = null;
                if (focus)
                    row.Warning.Focus();
                return false;
            }
            result = result.With(info.Indicator, threshold!);
        }

        thresholds = result;
        error      = string.Empty;
        return true;
    }

    // "≥ 75 % / ≥ 90 %", "< 300 s / off".
    private static string Describe(HealthIndicator indicator, HealthThreshold threshold) =>
        $"{Level(indicator, threshold.Warning)} / {Level(indicator, threshold.Critical)}";

    private static string Level(HealthIndicator indicator, double? value) =>
        value is null ? "off" : AlertText.Threshold(indicator, value);

    // An accent border on a box that differs from the default, so an edited threshold stands out.
    private void UpdateBorders()
    {
        foreach (var (indicator, row) in _rows)
        {
            var defaults = _defaults.Get(indicator);
            Mark(row.Warning,  indicator, defaults.Warning);
            Mark(row.Critical, indicator, defaults.Critical);
        }
    }

    private void Mark(TextBox box, HealthIndicator indicator, double? defaultValue)
    {
        var differs = !_isReadOnly && !Same(indicator, box.Text, defaultValue);
        if (differs)
            box.SetResourceReference(BorderBrushProperty, "Accent");
        else
            box.SetResourceReference(BorderBrushProperty, "Border");
        box.BorderThickness = new Thickness(differs ? 2 : 1);
    }

    // Whether what's typed is the default value; text that can't be read differs.
    private static bool Same(HealthIndicator indicator, string text, double? value)
    {
        var both = HealthThresholds.TryParse(indicator, text, string.Empty, out var parsed, out _);
        return both ? parsed!.Warning == value : text.Trim().Length == 0 && value is null;
    }

    // ── Building the table ────────────────────────────────────────────────────

    // A row per check; null adds the header row.
    private void AddRow(HealthIndicatorInfo? info)
    {
        GridRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = GridRows.RowDefinitions.Count - 1;

        if (info is null)
        {
            string[] headers = ["CHECK", "NOW", "WARNING", "CRITICAL", "WEIGHT", "POINTS"];
            for (var column = 0; column < headers.Length; column++)
            {
                var header = Text(headers[column], 10, column >= 4);
                header.FontWeight = FontWeights.SemiBold;
                header.Margin     = new Thickness(0, 0, column >= 4 ? 0 : 8, 8);
                header.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                Place(header, row, column);
            }
            return;
        }

        // A line above each check, across the table.
        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
        line.SetResourceReference(Border.BorderBrushProperty, "Border");
        Place(line, row, 0);
        Grid.SetColumnSpan(line, 6);

        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 5, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                                Fill = Brushes.Transparent };
        var name = Text(info.Name, 12, right: false);
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var defaults = Text(string.Empty, 10, right: false);
        defaults.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        defaults.Margin = new Thickness(0, 2, 0, 0);
        var names = new StackPanel { Children = { name, defaults } };
        var nameCell = new DockPanel { Margin = new Thickness(0, 9, 8, 9), ToolTip = info.Hint, Background = Brushes.Transparent };
        DockPanel.SetDock(dot, Dock.Left);
        nameCell.Children.Add(dot);
        nameCell.Children.Add(names);
        Place(nameCell, row, 0);

        var now = Text("—", 12, right: false);
        now.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        now.VerticalAlignment = VerticalAlignment.Center;
        Place(now, row, 1);

        var warning  = NewBox(info, "warning");
        var critical = NewBox(info, "critical");
        Place(Cell(info, warning),  row, 2);
        Place(Cell(info, critical), row, 3);

        var weight = Text(HealthScore.Weight(info.Indicator).ToString(CultureInfo.CurrentCulture), 12, right: true);
        weight.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        weight.VerticalAlignment = VerticalAlignment.Center;
        Place(weight, row, 4);

        var points = Text("—", 12, right: true);
        points.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        points.VerticalAlignment = VerticalAlignment.Center;
        Place(points, row, 5);

        _rows[info.Indicator] = new Row(warning, critical, dot, now, points, defaults);
    }

    // "≥ [ 75 ] %", or "< [ 300 ] s" for a check where lower is worse.
    private static StackPanel Cell(HealthIndicatorInfo info, FrameworkElement box)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0),
                                    VerticalAlignment = VerticalAlignment.Center };
        cell.Children.Add(Muted(info.LowerIsWorse ? "<" : "≥", new Thickness(0, 0, 4, 0)));
        cell.Children.Add(box.Parent as FrameworkElement ?? box);
        if (info.Unit.Length > 0)
            cell.Children.Add(Muted(info.Unit, new Thickness(4, 0, 0, 0)));
        return cell;
    }

    // A box with "off" showing through while it's blank, which is what blank means.
    private TextBox NewBox(HealthIndicatorInfo info, string level)
    {
        var box = new TextBox
        {
            Width                    = 64,
            Height                   = 28,
            FontSize                 = 12,
            Padding                  = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(1),
            ToolTip                  = $"{info.Name}, {level}: leave blank to turn this level off.",
        };
        box.SetResourceReference(BackgroundProperty,         "BgDeep");
        box.SetResourceReference(ForegroundProperty,         "TextPrimary");
        box.SetResourceReference(BorderBrushProperty,        "Border");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
        AutomationProperties.SetName(box, $"{info.Name} {level} threshold");

        var off = Muted("off", new Thickness(8, 0, 0, 0));
        off.IsHitTestVisible = false;
        var holder = new Grid { Children = { box, off } };
        box.TextChanged += (_, _) =>
        {
            off.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Mark(box, info.Indicator, level == "warning" ? _defaults.Get(info.Indicator).Warning : _defaults.Get(info.Indicator).Critical);
            Changed?.Invoke();
        };
        return box;
    }

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
