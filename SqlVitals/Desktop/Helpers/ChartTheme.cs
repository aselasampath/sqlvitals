using System.Windows;
using System.Windows.Media;
using SkiaSharp;

namespace SqlVitals.Desktop.Helpers;

// Reads chart colours from whatever theme dictionary is currently merged into
// Application.Current.Resources (see App.ToggleTheme), so LiveChartsCore axis/grid
// paint stays legible whether the app is in dark or light mode.
public static class ChartTheme
{
    // Strong-contrast label colour (near-white on dark theme, near-black on light theme).
    public static SKColor AxisColor => ToSk(ResourceColor("TextPrimaryColor"));

    // Softer label colour for less prominent axes.
    public static SKColor MutedAxisColor => ToSk(ResourceColor("TextMutedColor"));

    // Gridline / separator colour.
    public static SKColor GridColor => ToSk(ResourceColor("BorderColor"));

    public static Brush PanelBackground => (Brush)Application.Current.Resources["BgCard"];

    public static Brush PanelTitleColor => (Brush)Application.Current.Resources["TextPrimary"];

    private static Color ResourceColor(string key) => (Color)Application.Current.Resources[key];

    private static SKColor ToSk(Color c) => new(c.R, c.G, c.B, c.A);
}
