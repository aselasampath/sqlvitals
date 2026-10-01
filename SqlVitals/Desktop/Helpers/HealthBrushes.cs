using System.Windows.Media;
using SqlVitals.Engine.Home;
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

    /// <summary>A Home tile's status (#130), as the dot's colour for its level; grey while off or waiting.</summary>
    public static Brush For(TileStatus status) => status switch
    {
        TileStatus.Unreachable or TileStatus.Critical => Red,
        TileStatus.Warning                            => Amber,
        TileStatus.Healthy                            => Green,
        _                                             => Grey,
    };

    /// <summary>A pale wash of a status's colour, behind its label, readable in either theme.</summary>
    public static Brush Tint(TileStatus status) => status switch
    {
        TileStatus.Unreachable or TileStatus.Critical => RedTint,
        TileStatus.Warning                            => AmberTint,
        TileStatus.Healthy                            => GreenTint,
        _                                             => GreyTint,
    };

    private static readonly Brush RedTint   = Frozen(0xEF, 0x44, 0x44, 0x33);
    private static readonly Brush AmberTint = Frozen(0xFB, 0xBF, 0x24, 0x40);
    private static readonly Brush GreenTint = Frozen(0x22, 0xC5, 0x5E, 0x33);
    private static readonly Brush GreyTint  = Frozen(0x94, 0xA3, 0xB8, 0x33);

    private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
