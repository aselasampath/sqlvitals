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
/// The Checks table of the health score breakdown (#130) with a warning and a critical box per check
/// (#131) and a weight box (#173): Check, Now, Warning, Critical, Weight, Points. Page life
/// expectancy's row says whether its thresholds are per 4 GB of buffer pool, and disk free has a
/// second row for its GB thresholds (#174). In Settings for the
/// thresholds every connection shares, and on a connection's Health thresholds tab for its own,
/// where Now and Points show its latest sample graded on what's in the boxes.
/// </summary>
public partial class HealthThresholdsEditor : UserControl
{
    private sealed record Row(TextBox Warning, TextBox Critical, TextBox Weight, Ellipse Dot, TextBlock Now, TextBlock Points, TextBlock Default);

    private readonly Dictionary<HealthIndicator, Row> _rows = new();
    private TextBox   _gbWarning  = null!;
    private TextBox   _gbCritical = null!;
    private TextBlock _gbDefault  = null!;
    private HealthThresholds _defaults = HealthThresholds.Default;
    private HealthScore?     _latest;
    private bool             _showsSample = true;
    private bool             _isReadOnly;

    public HealthThresholdsEditor()
    {
        InitializeComponent();

        PleScaled = new CheckBox
        {
            Content   = "Per 4 GB of buffer pool",
            FontSize  = 11,
            Margin    = new Thickness(0, 4, 0, 0),
            IsChecked = true,
            ToolTip   = "On: the thresholds are seconds per 4 GB of buffer pool, so a 64 GB server is warned below " +
                        "4,800 s (300 × 64 ÷ 4) and a 2 GB one below 150 s. Off: the seconds are used as they are.",
        };
        PleScaled.SetResourceReference(ForegroundProperty, "TextPrimary");
        AutomationProperties.SetName(PleScaled, "Page life expectancy thresholds per 4 GB of buffer pool");
        PleScaled.Checked   += (_, _) => Changed?.Invoke();
        PleScaled.Unchecked += (_, _) => Changed?.Invoke();

        AddRow(null);
        foreach (var info in HealthThresholds.Indicators)
        {
            AddRow(info);
            if (info.Indicator == HealthIndicator.DiskFree)
                AddGbRow();
        }

        Show(HealthThresholds.Default);
    }

    /// <summary>A box was typed in, the page life expectancy toggle changed, or <see cref="Show"/> filled them.</summary>
    public event Action? Changed;

    /// <summary>Page life expectancy's thresholds are per 4 GB of buffer pool (#173), under its name.</summary>
    internal CheckBox PleScaled { get; }

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
                row.Default.Text = "Default: " + Describe(indicator, value);
            var gb = value.DiskFreeGbOrDefault;
            _gbDefault.Text = $"Default: {Level(HealthThresholds.DiskFreeGbInfo, gb.Warning)} / {Level(HealthThresholds.DiskFreeGbInfo, gb.Critical)}";
            UpdateBorders();
        }
    }

    /// <summary>The "Default: ≥ 75 % / ≥ 90 % · weight 20" line under each check's name.</summary>
    public bool ShowsDefaults
    {
        get => _rows.Values.First().Default.Visibility == Visibility.Visible;
        set
        {
            foreach (var row in _rows.Values)
                row.Default.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _gbDefault.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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
            foreach (var box in _rows.Values.SelectMany(row => new[] { row.Warning, row.Critical, row.Weight })
                                            .Append(_gbWarning).Append(_gbCritical))
                {
                    box.IsReadOnly = value;
                    box.Focusable  = !value;
                    if (value)
                        box.Background = Brushes.Transparent;
                    else
                        box.SetResourceReference(BackgroundProperty, "BgDeep");
                }
            PleScaled.IsEnabled = !value;
            UpdateBorders();
        }
    }

    /// <summary>Fills the boxes and the toggle; a level that is off shows blank.</summary>
    public void Show(HealthThresholds thresholds)
    {
        foreach (var (indicator, row) in _rows)
        {
            var threshold = thresholds.Get(indicator);
            row.Warning.Text  = HealthThresholds.Format(threshold.Warning);
            row.Critical.Text = HealthThresholds.Format(threshold.Critical);
            row.Weight.Text   = thresholds.Weight(indicator).ToString(CultureInfo.CurrentCulture);
        }
        _gbWarning.Text     = HealthThresholds.Format(thresholds.DiskFreeGbOrDefault.Warning);
        _gbCritical.Text    = HealthThresholds.Format(thresholds.DiskFreeGbOrDefault.Critical);
        PleScaled.IsChecked = thresholds.ScalesPageLifeExpectancy;
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

    /// <summary>
    /// Each check's dot and points, as graded on the boxes; null clears them. A check with no data
    /// or a weight of 0 shows a dash: it isn't scored (#173).
    /// </summary>
    public void ShowGrades(HealthScore? graded)
    {
        foreach (var (indicator, row) in _rows)
        {
            var check = graded?.Checks.FirstOrDefault(c => c.Indicator == indicator);
            row.Dot.Fill          = check is null ? Brushes.Transparent : HealthBrushes.For(check.Level);
            row.Points.Text       = check is null || !check.IsScored ? "—" : check.PointsLost > 0 ? $"−{check.PointsLost}" : "0";
            row.Points.FontWeight = check?.PointsLost > 0 ? FontWeights.SemiBold : FontWeights.Normal;
            row.Points.ToolTip    = check switch
            {
                null                => null,
                { IsUnknown: true } => "No data, so it's left out of the score.",
                { Weight: 0 }       => "Weight 0: not scored.",
                _                   => $"{check.Worth} of the 100 points among the checks that reported.",
            };
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

    /// <summary>Disk free's GB warning or critical box (#174).</summary>
    internal TextBox GbBox(bool critical = false) => critical ? _gbCritical : _gbWarning;

    /// <summary>A check's weight box.</summary>
    internal TextBox WeightBox(HealthIndicator indicator) => _rows[indicator].Weight;

    /// <summary>A check's "Now" and "Points" cells, as shown.</summary>
    internal (string Now, string Points) Cells(HealthIndicator indicator) => (_rows[indicator].Now.Text, _rows[indicator].Points.Text);

    /// <summary>Every box's text and the toggle, to tell whether anything was changed since it was last saved.</summary>
    public string Snapshot =>
        string.Join("|", _rows.Values.Select(r => $"{r.Warning.Text.Trim()};{r.Critical.Text.Trim()};{r.Weight.Text.Trim()}")) +
        $"|{_gbWarning.Text.Trim()};{_gbCritical.Text.Trim()}" +
        (PleScaled.IsChecked == true ? "|scaled" : "|fixed");

    /// <summary>
    /// Reads the boxes. Returns false with a message naming the first check that can't be used,
    /// and, when <paramref name="focus"/>, moves the focus to it.
    /// </summary>
    public bool TryRead(out HealthThresholds? thresholds, out string error, bool focus = true)
    {
        var result  = HealthThresholds.Default;
        var weights = HealthWeights.Default;
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
            if (!HealthWeights.TryParse(info.Indicator, row.Weight.Text, out var weight, out error))
            {
                thresholds = null;
                if (focus)
                    row.Weight.Focus();
                return false;
            }
            result  = result.With(info.Indicator, threshold!);
            weights = weights.With(info.Indicator, weight);
        }

        if (!HealthThresholds.TryParse(HealthThresholds.DiskFreeGbInfo, _gbWarning.Text, _gbCritical.Text, out var gb, out error))
        {
            thresholds = null;
            if (focus)
                _gbWarning.Focus();
            return false;
        }

        if (HealthThresholds.Indicators.All(i => weights.Get(i.Indicator) == 0))
        {
            thresholds = null;
            error      = "Give at least one check a weight above 0, or there is nothing to score.";
            if (focus)
                _rows[HealthIndicator.Cpu].Weight.Focus();
            return false;
        }

        thresholds = result with { Weights = weights, PageLifeExpectancyFixed = PleScaled.IsChecked != true, DiskFreeGb = gb };
        error      = string.Empty;
        return true;
    }

    // "≥ 75 % / ≥ 90 % · weight 20", "< 300 s per 4 GB / off · weight 16".
    private static string Describe(HealthIndicator indicator, HealthThresholds thresholds)
    {
        var threshold = thresholds.Get(indicator);
        var perGb     = indicator == HealthIndicator.PageLifeExpectancy && thresholds.ScalesPageLifeExpectancy ? " per 4 GB" : string.Empty;
        return $"{Level(indicator, threshold.Warning)}{(threshold.Warning is null ? "" : perGb)} / " +
               $"{Level(indicator, threshold.Critical)}{(threshold.Critical is null ? "" : perGb)} · weight {thresholds.Weight(indicator)}";
    }

    private static string Level(HealthIndicator indicator, double? value) =>
        value is null ? "off" : AlertText.Threshold(indicator, value);

    // "< 50 GB", or "off".
    private static string Level(HealthIndicatorInfo info, double? value) =>
        value is { } v ? $"< {HealthThresholds.Format(v)} {info.Unit}" : "off";

    // An accent border on a box that differs from the default, so an edited threshold or weight stands out.
    private void UpdateBorders()
    {
        foreach (var (indicator, row) in _rows)
        {
            var defaults = _defaults.Get(indicator);
            Mark(row.Warning,  HealthThresholds.Info(indicator), defaults.Warning);
            Mark(row.Critical, HealthThresholds.Info(indicator), defaults.Critical);
            MarkWeight(row.Weight, indicator);
        }
        Mark(_gbWarning,  HealthThresholds.DiskFreeGbInfo, _defaults.DiskFreeGbOrDefault.Warning);
        Mark(_gbCritical, HealthThresholds.DiskFreeGbInfo, _defaults.DiskFreeGbOrDefault.Critical);
    }

    private void Mark(TextBox box, HealthIndicatorInfo info, double? defaultValue) =>
        Highlight(box, !_isReadOnly && !Same(info, box.Text, defaultValue));

    private void MarkWeight(TextBox box, HealthIndicator indicator) =>
        Highlight(box, !_isReadOnly && !(int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var weight) &&
                                         weight == _defaults.Weight(indicator)));

    private static void Highlight(TextBox box, bool differs)
    {
        if (differs)
            box.SetResourceReference(BorderBrushProperty, "Accent");
        else
            box.SetResourceReference(BorderBrushProperty, "Border");
        box.BorderThickness = new Thickness(differs ? 2 : 1);
    }

    // Whether what's typed is the default value; text that can't be read differs.
    private static bool Same(HealthIndicatorInfo info, string text, double? value)
    {
        var both = HealthThresholds.TryParse(info, text, string.Empty, out var parsed, out _);
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
                if (column == 4)
                    header.ToolTip = "What each check is worth in the score. Weights are relative: the score is always out of 100, " +
                                     "shared over the checks that reported. 0 leaves a check out of the score.";
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
        if (info.Indicator == HealthIndicator.PageLifeExpectancy)
            names.Children.Add(PleScaled);
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

        var weight = NewWeightBox(info);
        Place(weight, row, 4);

        var points = Text("—", 12, right: true);
        points.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        points.VerticalAlignment = VerticalAlignment.Center;
        Place(points, row, 5);

        _rows[info.Indicator] = new Row(warning, critical, weight, dot, now, points, defaults);
    }

    // Disk free's GB thresholds (#174), under its row: "and free space below [ 50 ] GB", with no weight of their own.
    private void AddGbRow()
    {
        GridRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row  = GridRows.RowDefinitions.Count - 1;
        var info = HealthThresholds.DiskFreeGbInfo;

        var name = Text("and free space below", 12, right: false);
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _gbDefault = Text(string.Empty, 10, right: false);
        _gbDefault.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _gbDefault.Margin = new Thickness(0, 2, 0, 0);
        var names = new StackPanel { Margin = new Thickness(16, 0, 8, 9), ToolTip = info.Hint, Background = Brushes.Transparent,
                                     Children = { name, _gbDefault } };
        Place(names, row, 0);

        _gbWarning  = NewBox(info, "warning");
        _gbCritical = NewBox(info, "critical");
        Place(Cell(info, _gbWarning),  row, 2);
        Place(Cell(info, _gbCritical), row, 3);
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
        var box = StyledBox(64, $"{info.Name}, {level}: leave blank to turn this level off.");
        AutomationProperties.SetName(box, $"{info.Name} {level} threshold");

        var off = Muted("off", new Thickness(8, 0, 0, 0));
        off.IsHitTestVisible = false;
        var holder = new Grid { Children = { box, off } };
        box.TextChanged += (_, _) =>
        {
            off.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var defaults = info == HealthThresholds.DiskFreeGbInfo ? _defaults.DiskFreeGbOrDefault : _defaults.Get(info.Indicator);
            Mark(box, info, level == "warning" ? defaults.Warning : defaults.Critical);
            Changed?.Invoke();
        };
        return box;
    }

    // The check's weight (#173): a whole number from 0 to 100, 0 leaving it out of the score.
    private TextBox NewWeightBox(HealthIndicatorInfo info)
    {
        var box = StyledBox(44, $"{info.Name}'s weight in the score, 0 to 100. 0 leaves it out of the score; " +
                                "its thresholds still colour the dot and raise alerts.");
        box.HorizontalAlignment      = HorizontalAlignment.Right;
        box.HorizontalContentAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(box, $"{info.Name} weight");
        box.TextChanged += (_, _) =>
        {
            MarkWeight(box, info.Indicator);
            Changed?.Invoke();
        };
        return box;
    }

    private static TextBox StyledBox(double width, string toolTip)
    {
        var box = new TextBox
        {
            Width                    = width,
            Height                   = 28,
            FontSize                 = 12,
            Padding                  = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness          = new Thickness(1),
            ToolTip                  = toolTip,
        };
        box.SetResourceReference(BackgroundProperty,         "BgDeep");
        box.SetResourceReference(ForegroundProperty,         "TextPrimary");
        box.SetResourceReference(BorderBrushProperty,        "Border");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimary");
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
