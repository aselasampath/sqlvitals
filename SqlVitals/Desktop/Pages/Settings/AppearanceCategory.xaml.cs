using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Appearance (#131): the light or dark theme.</summary>
internal partial class AppearanceCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;

    public AppearanceCategory(SettingsContext context)
    {
        _context = context;
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Appearance).Subtitle;

        var dark = Application.Current is App { IsDarkTheme: true };
        BtnThemeToggle.Content = dark ? "☀  Light" : "🌙  Dark";
        TxtTheme.Text = dark
            ? "Dark at the moment. Switch every page to the light theme."
            : "Light at the moment. Switch every page to the dark theme.";
        System.Windows.Automation.AutomationProperties.SetName(BtnThemeToggle, dark ? "Switch to the light theme" : "Switch to the dark theme");
    }

    /// <summary>The theme was switched: the settings page is opened again in it.</summary>
    public event Action? ThemeChanged;

    public Task<bool> TryLeaveAsync() => Task.FromResult(true);

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app)
            return;
        app.ToggleTheme();

        // Not in the settings file, so logged here rather than by the settings diff.
        _context.Monitoring.Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.SettingChanged, null, null, ActivitySources.Settings,
                                                  "Theme", app.IsDarkTheme ? "Light" : "Dark", app.IsDarkTheme ? "Dark" : "Light"));
        ThemeChanged?.Invoke();
    }
}
