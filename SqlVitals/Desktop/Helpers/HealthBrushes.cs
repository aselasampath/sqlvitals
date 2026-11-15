using System.Windows.Media;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// The health dot's colours, shared by the dot, the health score and its breakdown so they
/// always match. The theme's Health keys, which are the same in both themes.
/// </summary>
public static class HealthBrushes
{
    public static Brush Green => ThemeResources.BrushOf("HealthGood");
    public static Brush Amber => ThemeResources.BrushOf("HealthWarning");
    public static Brush Red   => ThemeResources.BrushOf("HealthCritical");
    public static Brush Grey  => ThemeResources.BrushOf("HealthNone");

    public static Brush For(HealthLevel level) => ThemeResources.BrushOf(KeyFor(level));

    /// <summary>A level's theme key, for an element that follows the theme by resource reference (#254).</summary>
    public static string KeyFor(HealthLevel level) => level switch
    {
        HealthLevel.Healthy     => "HealthGood",
        HealthLevel.Warning     => "HealthWarning",
        HealthLevel.Critical    => "HealthCritical",
        HealthLevel.Unavailable => "HealthCritical",
        _                       => "HealthNone",
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
        TileStatus.Unreachable or TileStatus.Critical => ThemeResources.BrushOf("HealthCriticalTint"),
        TileStatus.Warning                            => ThemeResources.BrushOf("HealthWarningTint"),
        TileStatus.Healthy                            => ThemeResources.BrushOf("HealthGoodTint"),
        _                                             => ThemeResources.BrushOf("HealthNoneTint"),
    };
}
