using System.Windows;
using System.Windows.Media;
using SkiaSharp;

namespace SqlVitals.Desktop.Helpers;

// Reads chart colours from whatever theme dictionary is currently merged into
// Application.Current.Resources (see IThemeService), so LiveChartsCore axis/grid
// paint stays legible whether the app is in dark or light mode.
public static class ChartTheme
{
    // Strong-contrast label colour (near-white on dark theme, near-black on light theme).
    public static SKColor AxisColor => ToSk(ResourceColor("TextPrimaryColor"));

    // Softer label colour for less prominent axes.
    public static SKColor MutedAxisColor => ToSk(ResourceColor("TextMutedColor"));

    // Gridline / separator colour.
    public static SKColor GridColor => ToSk(ResourceColor("BorderColor"));

    public static Brush PanelTitleColor => (Brush)Application.Current.Resources["TextPrimary"];

    /// <summary>A theme colour by key, e.g. <c>"WaitCpuColor"</c>: a series that means something (#164).</summary>
    public static SKColor Of(string colorKey) => ToSk(ResourceColor(colorKey));

    /// <summary>How many colours the series palette has before it repeats.</summary>
    public const int SeriesCount = 12;

    /// <summary>The <paramref name="index"/>th line of a chart whose lines have no meaning of their own (counters, wait types, plans).</summary>
    public static SKColor Series(int index) => Of($"ChartSeries{index % SeriesCount}Color");

    private static Color ResourceColor(string key) => (Color)Application.Current.Resources[key];

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
}
