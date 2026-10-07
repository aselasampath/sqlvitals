using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace SqlVitals.Desktop.Services;

/// <summary>How tightly every grid's rows are laid out (#148): Settings → Appearance.</summary>
public enum GridDensity
{
    /// <summary>The look grids have always had: 12 px text with room around it.</summary>
    Comfortable,

    /// <summary>The same 12 px text with less room: more rows on a small screen, or a shared one.</summary>
    Compact,
}

/// <summary>
/// The resources the theme's grid styles take their spacing from (#148). Both themes define the
/// comfortable values; the application's own dictionary overrides them for the density chosen, so
/// a change applies to every grid at once, and survives a theme switch, without a restart.
/// </summary>
public static class GridDensityResources
{
    /// <summary>A cell's padding: the room around its text.</summary>
    public const string CellPadding = "GridCellPadding";

    /// <summary>A column header's padding.</summary>
    public const string HeaderPadding = "GridHeaderPadding";

    /// <summary>The text size of the cells.</summary>
    public const string FontSize = "GridFontSize";

    public static Thickness CellPaddingOf(GridDensity density) =>
        density == GridDensity.Compact ? new Thickness(6, 1, 6, 1) : new Thickness(8, 5, 8, 5);

    public static Thickness HeaderPaddingOf(GridDensity density) =>
        density == GridDensity.Compact ? new Thickness(6, 3, 6, 3) : new Thickness(8, 6, 8, 6);

    /// <summary>12 px in both: compact takes room from around the text, not from the text.</summary>
    [SuppressMessage("Style", "IDE0060", Justification = "Same shape as PaddingOf, for when the densities' sizes differ")]
    public static double FontSizeOf(GridDensity density) => 12;

    /// <summary>Puts a density's values in a dictionary, the application's in the app.</summary>
    public static void Apply(ResourceDictionary resources, GridDensity density)
    {
        resources[CellPadding]   = CellPaddingOf(density);
        resources[HeaderPadding] = HeaderPaddingOf(density);
        resources[FontSize]      = FontSizeOf(density);
    }

    /// <summary>A saved density; comfortable for anything missing or unknown.</summary>
    public static GridDensity Parse(string? value) =>
        Enum.TryParse<GridDensity>(value, ignoreCase: true, out var density) && Enum.IsDefined(density) ? density : GridDensity.Comfortable;
}
