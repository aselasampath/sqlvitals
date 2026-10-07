using System.Diagnostics.CodeAnalysis;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// The light or dark theme (#182). Charts and brushes built in code read their colours when they
/// are drawn, so they follow <see cref="Changed"/> to draw again in the new theme.
/// </summary>
public interface IThemeService
{
    AppTheme Current { get; }

    /// <summary>Raised once the new theme's resources are in place.</summary>
    event EventHandler? Changed;

    /// <summary>Switches every page to <paramref name="theme"/>; nothing when it is already the theme.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "Only C# calls it; Set is a keyword in Visual Basic")]
    void Set(AppTheme theme);
}

public enum AppTheme
{
    Light,
    Dark,
}
