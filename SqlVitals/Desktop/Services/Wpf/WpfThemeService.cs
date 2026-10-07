using System.Windows;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary>
/// <see cref="IThemeService"/> over the application's resources: <c>Styles/LightTheme.xaml</c>, which
/// <c>App.xaml</c> starts with, or <c>DarkTheme.xaml</c>. Every <c>DynamicResource</c> follows the swap.
/// </summary>
/// <param name="resources">Where the theme dictionary is merged: the application's.</param>
internal sealed class WpfThemeService(Func<ResourceDictionary?> resources) : IThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Light;

    public event EventHandler? Changed;

    public void Set(AppTheme theme)
    {
        if (theme == Current || resources() is not { } target)
            return;

        Current = theme;
        var uri = new Uri(theme == AppTheme.Dark
            ? "pack://application:,,,/SqlVitals.Desktop;component/Styles/DarkTheme.xaml"
            : "pack://application:,,,/SqlVitals.Desktop;component/Styles/LightTheme.xaml");
        target.MergedDictionaries.Clear();
        target.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
