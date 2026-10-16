using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One check's row of the Checks table in Settings (#131, #173, #190): its name, its default, its
/// figure now and the points it loses, and its warning, critical and weight boxes.
/// </summary>
public sealed partial class ThresholdRow : ObservableObject
{
    public ThresholdRow(HealthIndicatorInfo info, Action changed)
    {
        Info     = info;
        Warning  = new ThresholdField($"{info.Name} warning threshold", $"{info.Name}, warning: leave blank to turn this level off.", true, changed);
        Critical = new ThresholdField($"{info.Name} critical threshold", $"{info.Name}, critical: leave blank to turn this level off.", true, changed);
        Weight   = new ThresholdField($"{info.Name} weight",
                                      $"{info.Name}'s weight in the score, 0 to 100. 0 leaves it out of the score; " +
                                      "its thresholds still colour the dot and raise alerts.", false, changed);
    }

    public HealthIndicatorInfo Info { get; }

    public HealthIndicator Indicator => Info.Indicator;

    public string Name => Info.Name;

    /// <summary>What the check is, as its name's tooltip.</summary>
    public string Hint => Info.Hint;

    /// <summary>"&lt;" for a check where lower is worse, "≥" otherwise: in front of each box.</summary>
    public string Comparison => Info.LowerIsWorse ? "<" : "≥";

    /// <summary>"%", "s", "ms": after each box; empty for none.</summary>
    public string Unit => Info.Unit;

    /// <summary>Page life expectancy's row says whether its thresholds are per 4 GB of buffer pool (#173).</summary>
    public bool IsPageLifeExpectancy => Indicator == HealthIndicator.PageLifeExpectancy;

    public ThresholdField Warning  { get; }
    public ThresholdField Critical { get; }
    public ThresholdField Weight   { get; }

    /// <summary>"Default: ≥ 75 % / ≥ 90 % · weight 20".</summary>
    [ObservableProperty]
    private string _defaultText = string.Empty;

    /// <summary>Its figure in the latest sample, or a dash.</summary>
    [ObservableProperty]
    private string _nowText = "—";

    /// <summary>How the latest sample grades on what's typed; null while there's nothing to grade (no dot).</summary>
    [ObservableProperty]
    private HealthLevel? _grade;

    /// <summary>"−8", "0", or a dash for a check that isn't scored.</summary>
    [ObservableProperty]
    private string _pointsText = "—";

    /// <summary>It loses points on what's typed: Now and Points are bold, Points in its grade's colour.</summary>
    [ObservableProperty]
    private bool _losesPoints;

    [ObservableProperty]
    private string? _pointsToolTip;
}
