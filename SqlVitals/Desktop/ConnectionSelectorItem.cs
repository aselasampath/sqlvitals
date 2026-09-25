using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop;

/// <summary>
/// Row in the sidebar connection selector: a saved connection plus the live health of its
/// background monitoring session. Never exposes credentials.
/// </summary>
public sealed class ConnectionSelectorItem : INotifyPropertyChanged
{
    public ConnectionSelectorItem(ConnectionSettings settings) => Settings = settings;

    public ConnectionSettings Settings { get; }

    public string DisplayName   => Settings.DisplayName;
    public string Server        => Settings.Server;
    public string DatabaseLabel => Settings.DatabaseLabel;

    /// <summary>Dot fill; transparent (outline only) when the connection isn't monitored.</summary>
    public Brush HealthFill { get; private set; } = Brushes.Transparent;

    public Brush HealthStroke { get; private set; } = HealthBrushes.Grey;

    public string HealthText { get; private set; } = string.Empty;

    /// <summary>The latest health score (#36); null while not monitored or before the first sample.</summary>
    public HealthScore? Score { get; private set; }

    public string ScoreText => Score?.Value.ToString() ?? string.Empty;

    public Brush ScoreBrush => Score is { } score ? HealthBrushes.For(score.Level) : HealthBrushes.Grey;

    public Visibility ScoreVisibility => Score is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Dimmed while paused: the score is the last one taken, not a current one.</summary>
    public double ScoreOpacity { get; private set; } = 1;

    public string ToolTipText => Score is { } score
        ? $"{Settings.Summary}\n{score.Summary()}\n{HealthText}\nClick the score to see which checks lowered it."
        : $"{Settings.Summary}\n{HealthText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateHealth(MonitoringManager monitoring)
    {
        var session = monitoring.Get(Settings.Id);
        if (session is null)
        {
            HealthFill   = Brushes.Transparent;
            HealthStroke = HealthBrushes.Grey;
            HealthText   = monitoring.NotMonitoredReason(Settings.Id);
            Score        = null;
        }
        else
        {
            var fill = HealthBrushes.For(session.Health);
            HealthFill   = session.IsRunning ? fill : Brushes.Transparent;
            HealthStroke = fill;
            HealthText   = session.IsRunning ? session.HealthText : "Paused · " + session.HealthText;
            Score        = session.Score;
            ScoreOpacity = session.IsRunning ? 1 : 0.5;
        }

        foreach (var name in new[] { nameof(HealthFill), nameof(HealthStroke), nameof(HealthText), nameof(Score),
                                     nameof(ScoreText), nameof(ScoreBrush), nameof(ScoreVisibility), nameof(ScoreOpacity),
                                     nameof(ToolTipText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
