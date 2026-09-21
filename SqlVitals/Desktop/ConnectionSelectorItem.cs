using System.ComponentModel;
using System.Windows.Media;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop;

/// <summary>
/// Row in the sidebar connection selector: a saved connection plus the live health of its
/// background monitoring session. Never exposes credentials.
/// </summary>
public sealed class ConnectionSelectorItem : INotifyPropertyChanged
{
    private static readonly Brush Green = Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush Red   = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush Grey  = Frozen(0x94, 0xA3, 0xB8);

    public ConnectionSelectorItem(ConnectionSettings settings) => Settings = settings;

    public ConnectionSettings Settings { get; }

    public string DisplayName   => Settings.DisplayName;
    public string Server        => Settings.Server;
    public string DatabaseLabel => Settings.DatabaseLabel;

    /// <summary>Dot fill; transparent (outline only) when the connection isn't monitored.</summary>
    public Brush HealthFill { get; private set; } = Brushes.Transparent;

    public Brush HealthStroke { get; private set; } = Grey;

    public string HealthText { get; private set; } = string.Empty;

    public string ToolTipText => $"{Settings.Summary}\n{HealthText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateHealth(MonitoringManager monitoring)
    {
        var session = monitoring.Get(Settings.Id);
        if (session is null)
        {
            HealthFill   = Brushes.Transparent;
            HealthStroke = Grey;
            HealthText   = monitoring.NotMonitoredReason(Settings.Id);
        }
        else
        {
            var fill = session.Health switch
            {
                HealthLevel.Healthy     => Green,
                HealthLevel.Warning     => Amber,
                HealthLevel.Critical    => Red,
                HealthLevel.Unavailable => Red,
                _                       => Grey,
            };
            HealthFill   = session.IsRunning ? fill : Brushes.Transparent;
            HealthStroke = fill;
            HealthText   = session.IsRunning ? session.HealthText : "Paused · " + session.HealthText;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthFill)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthStroke)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTipText)));
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
