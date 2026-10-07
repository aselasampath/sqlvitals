using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// The current theme's brushes and colours by key, for code with no element to look them up on:
/// models bound in XAML and static helpers. The values live in <c>Styles/LightTheme.xaml</c> and
/// <c>DarkTheme.xaml</c>; code names a key and never a hex colour (#164, ColourGuardTests). An
/// element sets a brush with <c>SetResourceReference</c> instead, so it follows a theme switch.
/// </summary>
internal static class ThemeResources
{
    public static Brush BrushOf(string key) => (Brush)Application.Current.Resources[key];

    public static Color ColorOf(string key) => (Color)Application.Current.Resources[key];

    /// <summary>Colours <paramref name="text"/> with the theme brush <paramref name="key"/>, following a theme switch.</summary>
    public static TextBlock WithForeground(this TextBlock text, string key)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, key);
        return text;
    }

    /// <summary><paramref name="color"/>, see-through.</summary>
    public static SolidColorBrush Wash(Color color, byte alpha) => new(Color.FromArgb(alpha, color.R, color.G, color.B));

    /// <summary><paramref name="color"/> moved <paramref name="amount"/> (0 to 1) of the way to <paramref name="toward"/>.</summary>
    public static Color Mix(Color color, Color toward, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (toward.R - color.R) * amount),
        (byte)Math.Round(color.G + (toward.G - color.G) * amount),
        (byte)Math.Round(color.B + (toward.B - color.B) * amount));
}
