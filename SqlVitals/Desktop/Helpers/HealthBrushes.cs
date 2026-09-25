using System.Windows.Media;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// The health dot's colours, shared by the dot, the health score and its breakdown so they
/// always match. The same in both themes.
/// </summary>
public static class HealthBrushes
{
    public static readonly Brush Green = Frozen(0x22, 0xC5, 0x5E);
    public static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    public static readonly Brush Red   = Frozen(0xEF, 0x44, 0x44);
    public static readonly Brush Grey  = Frozen(0x94, 0xA3, 0xB8);

    public static Brush For(HealthLevel level) => level switch
    {
        HealthLevel.Healthy     => Green,
        HealthLevel.Warning     => Amber,
        HealthLevel.Critical    => Red,
        HealthLevel.Unavailable => Red,
        _                       => Grey,
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
