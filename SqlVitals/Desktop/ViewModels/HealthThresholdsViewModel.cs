using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// The Checks table of the health score breakdown (#130) with a warning and a critical box per check
/// (#131) and a weight box (#173): Check, Now, Warning, Critical, Weight, Points. Page life
/// expectancy's row says whether its thresholds are per 4 GB of buffer pool, and disk free has a
/// second row for its GB thresholds (#174). In Settings for the thresholds every connection shares,
/// and on a connection's Health thresholds tab for its own, where Now and Points show its latest
/// sample graded on what's in the boxes. <c>Controls/HealthThresholdsEditor</c> draws it (#190).
/// </summary>
public sealed partial class HealthThresholdsViewModel : ObservableObject
{
    private HealthThresholds _defaults = HealthThresholds.Default;
    private bool _filling;

    public HealthThresholdsViewModel()
    {
        Rows = HealthThresholds.Indicators.Select(info => new ThresholdRow(info, FieldChanged)).ToList();
        var gb = HealthThresholds.DiskFreeGbInfo;
        GbWarning  = new ThresholdField($"{gb.Name} warning threshold", $"{gb.Name}, warning: leave blank to turn this level off.", true, FieldChanged);
        GbCritical = new ThresholdField($"{gb.Name} critical threshold", $"{gb.Name}, critical: leave blank to turn this level off.", true, FieldChanged);
        Show(HealthThresholds.Default);
        Defaults = HealthThresholds.Default;
    }

    /// <summary>A box was typed in, the page life expectancy toggle changed, or <see cref="Show"/> filled them.</summary>
    public event Action? Changed;

    /// <summary>A box that can't be read: the view moves the keyboard focus to it.</summary>
    public event Action<ThresholdField>? FocusRequested;

    /// <summary>A row per check, in <see cref="HealthThresholds.Indicators"/>' order.</summary>
    public IReadOnlyList<ThresholdRow> Rows { get; }

    /// <summary>Disk free's GB thresholds (#174), under its row, with no weight of their own.</summary>
    public static HealthIndicatorInfo GbInfo => HealthThresholds.DiskFreeGbInfo;

    public ThresholdField GbWarning  { get; }
    public ThresholdField GbCritical { get; }

    /// <summary>"Default: &lt; 50 GB / &lt; 20 GB".</summary>
    [ObservableProperty]
    private string _gbDefaultText = string.Empty;

    /// <summary>Page life expectancy's thresholds are per 4 GB of buffer pool (#173).</summary>
    [ObservableProperty]
    private bool _pleScaled = true;

    /// <summary>The "Default: ≥ 75 % / ≥ 90 % · weight 20" line under each check's name.</summary>
    [ObservableProperty]
    private bool _showsDefaults = true;

    /// <summary>The Now and Points columns, for one connection's sample. Off for the thresholds every connection shares.</summary>
    [ObservableProperty]
    private bool _showsSample = true;

    /// <summary>The boxes can't be typed in: a connection using the default thresholds shows them read-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isReadOnly;

    public bool CanEdit => !IsReadOnly;

    partial void OnPleScaledChanged(bool value)
    {
        if (!_filling)
            Changed?.Invoke();
    }

    partial void OnIsReadOnlyChanged(bool value)
    {
        foreach (var field in Fields())
            field.IsReadOnly = value;
        UpdateEdited();
    }

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
            foreach (var row in Rows)
                row.DefaultText = "Default: " + Describe(row.Indicator, value);
            var gb = value.DiskFreeGbOrDefault;
            GbDefaultText = $"Default: {Level(GbInfo, gb.Warning)} / {Level(GbInfo, gb.Critical)}";
            UpdateEdited();
        }
    }

    /// <summary>Fills the boxes and the toggle; a level that is off shows blank. Raises <see cref="Changed"/> once.</summary>
    public void Show(HealthThresholds thresholds)
    {
        _filling = true;
        try
        {
            foreach (var row in Rows)
            {
                var threshold = thresholds.Get(row.Indicator);
                row.Warning.Text  = HealthThresholds.Format(threshold.Warning);
                row.Critical.Text = HealthThresholds.Format(threshold.Critical);
                row.Weight.Text   = thresholds.Weight(row.Indicator).ToString(CultureInfo.CurrentCulture);
            }
            GbWarning.Text  = HealthThresholds.Format(thresholds.DiskFreeGbOrDefault.Warning);
            GbCritical.Text = HealthThresholds.Format(thresholds.DiskFreeGbOrDefault.Critical);
            PleScaled       = thresholds.ScalesPageLifeExpectancy;
        }
        finally
        {
            _filling = false;
        }
        UpdateEdited();
        Changed?.Invoke();
    }

    /// <summary>
    /// The connection's latest score: Now shows its figures, and <see cref="ShowGrades"/> its checks
    /// graded on the boxes. Null with no sample: Now and Points show a dash.
    /// </summary>
    public void ShowSample(HealthScore? latest, HealthScore? graded)
    {
        var sample = latest is { Level: not HealthLevel.Unavailable, Checks.Count: > 0 } ? latest : null;
        foreach (var row in Rows)
            row.NowText = sample?.Checks.FirstOrDefault(c => c.Indicator == row.Indicator)?.ValueText ?? "—";
        ShowGrades(sample is null ? null : graded);
    }

    /// <summary>
    /// Each check's dot and points, as graded on the boxes; null clears them. A check with no data
    /// or a weight of 0 shows a dash: it isn't scored (#173).
    /// </summary>
    public void ShowGrades(HealthScore? graded)
    {
        foreach (var row in Rows)
        {
            var check = graded?.Checks.FirstOrDefault(c => c.Indicator == row.Indicator);
            row.Grade         = check?.Level;
            row.PointsText    = check is null || !check.IsScored ? "—" : check.PointsLost > 0 ? $"−{check.PointsLost}" : "0";
            row.LosesPoints   = check?.PointsLost > 0;
            row.PointsToolTip = check switch
            {
                null                => null,
                { IsUnknown: true } => "No data, so it's left out of the score.",
                { Weight: 0 }       => "Weight 0: not scored.",
                _                   => $"{check.Worth} of the 100 points among the checks that reported.",
            };
        }
    }

    /// <summary>A check's row.</summary>
    public ThresholdRow Row(HealthIndicator indicator) => Rows.Single(r => r.Indicator == indicator);

    /// <summary>Every box's text and the toggle, to tell whether anything was changed since it was last saved.</summary>
    public string Snapshot =>
        string.Join("|", Rows.Select(r => $"{r.Warning.Text.Trim()};{r.Critical.Text.Trim()};{r.Weight.Text.Trim()}")) +
        $"|{GbWarning.Text.Trim()};{GbCritical.Text.Trim()}" +
        (PleScaled ? "|scaled" : "|fixed");

    /// <summary>
    /// Reads the boxes. Returns false with a message naming the first check that can't be used,
    /// and, when <paramref name="focus"/>, asks the view to move the focus to it.
    /// </summary>
    public bool TryRead(out HealthThresholds? thresholds, out string error, bool focus = true)
    {
        thresholds = null;
        var result  = HealthThresholds.Default;
        var weights = HealthWeights.Default;
        foreach (var row in Rows)
        {
            if (!HealthThresholds.TryParse(row.Indicator, row.Warning.Text, row.Critical.Text, out var threshold, out error))
                return Fail(row.Warning, focus);
            if (!HealthWeights.TryParse(row.Indicator, row.Weight.Text, out var weight, out error))
                return Fail(row.Weight, focus);
            result  = result.With(row.Indicator, threshold!);
            weights = weights.With(row.Indicator, weight);
        }

        if (!HealthThresholds.TryParse(GbInfo, GbWarning.Text, GbCritical.Text, out var gb, out error))
            return Fail(GbWarning, focus);

        if (Rows.All(r => weights.Get(r.Indicator) == 0))
        {
            error = "Give at least one check a weight above 0, or there is nothing to score.";
            return Fail(Row(HealthIndicator.Cpu).Weight, focus);
        }

        thresholds = result with { Weights = weights, PageLifeExpectancyFixed = !PleScaled, DiskFreeGb = gb };
        error      = string.Empty;
        return true;
    }

    private bool Fail(ThresholdField field, bool focus)
    {
        if (focus)
            FocusRequested?.Invoke(field);
        return false;
    }

    private void FieldChanged()
    {
        if (_filling)
            return;
        UpdateEdited();
        Changed?.Invoke();
    }

    private IEnumerable<ThresholdField> Fields() =>
        Rows.SelectMany(r => new[] { r.Warning, r.Critical, r.Weight }).Append(GbWarning).Append(GbCritical);

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
    private void UpdateEdited()
    {
        foreach (var row in Rows)
        {
            var defaults = _defaults.Get(row.Indicator);
            row.Warning.IsEdited  = Differs(row.Info, row.Warning.Text, defaults.Warning);
            row.Critical.IsEdited = Differs(row.Info, row.Critical.Text, defaults.Critical);
            row.Weight.IsEdited   = !IsReadOnly &&
                                    !(int.TryParse(row.Weight.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var weight) &&
                                      weight == _defaults.Weight(row.Indicator));
        }
        GbWarning.IsEdited  = Differs(GbInfo, GbWarning.Text, _defaults.DiskFreeGbOrDefault.Warning);
        GbCritical.IsEdited = Differs(GbInfo, GbCritical.Text, _defaults.DiskFreeGbOrDefault.Critical);
    }

    private bool Differs(HealthIndicatorInfo info, string text, double? defaultValue) => !IsReadOnly && !Same(info, text, defaultValue);

    // Whether what's typed is the default value; text that can't be read differs.
    private static bool Same(HealthIndicatorInfo info, string text, double? value)
    {
        var both = HealthThresholds.TryParse(info, text, string.Empty, out var parsed, out _);
        return both ? parsed!.Warning == value : text.Trim().Length == 0 && value is null;
    }
}
