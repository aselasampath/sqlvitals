using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Appearance (#131): the light or dark theme, and the grid density (#148).</summary>
internal partial class AppearanceCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;
    private readonly IThemeService   _theme;

    /// <param name="theme">The theme switched; the app's (#182) when null.</param>
    public AppearanceCategory(SettingsContext context, IThemeService? theme = null)
    {
        _context = context;
        _theme   = theme ?? CompositionRoot.Get<IThemeService>();
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Appearance).Subtitle;

        var dark = _theme.Current == AppTheme.Dark;
        BtnThemeToggle.Content = dark ? "☀  Light" : "🌙  Dark";
        TxtTheme.Text = dark
            ? "Dark at the moment. Switch every page to the light theme."
            : "Light at the moment. Switch every page to the dark theme.";
        System.Windows.Automation.AutomationProperties.SetName(BtnThemeToggle, dark ? "Switch to the light theme" : "Switch to the dark theme");

        var density = context.Service.Load().GridDensity;
        _showingDensity = true;
        (density == GridDensity.Compact ? RbCompact : RbComfortable).IsChecked = true;
        _showingDensity = false;
        ShowDensity(density);
    }

    // While the saved density is put on the buttons, which isn't a change.
    private readonly bool _showingDensity;

    private void ShowDensity(GridDensity density) =>
        TxtDensity.Text = density == GridDensity.Compact
            ? "Compact: tighter rows with the same 12 px text, for more rows on a small or shared screen. Every grid, at once."
            : "Comfortable: rows with room around the text, the look grids have always had. Compact fits more rows.";

    // Applies to every grid at once, and is saved for the next start.
    private void Density_Checked(object sender, RoutedEventArgs e)
    {
        if (_showingDensity || RbComfortable is null || RbCompact is null)
            return;
        var density = RbCompact.IsChecked == true ? GridDensity.Compact : GridDensity.Comfortable;
        GridDensityResources.Apply(Application.Current.Resources, density);
        ShowDensity(density);
        try { _context.Service.SetGridDensity(density); }
        catch (Exception ex) { AppLog.Error("Appearance: could not save the grid density", ex); }
    }

    /// <summary>The theme was switched: the settings page is opened again in it.</summary>
    public event Action? ThemeChanged;

    public Task<bool> TryLeaveAsync() => Task.FromResult(true);

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var was = _theme.Current;
        _theme.Set(was == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
        if (_theme.Current == was)
            return;

        // Not in the settings file, so logged here rather than by the settings diff.
        _context.Monitoring.Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.SettingChanged, null, null, ActivitySources.Settings,
                                                  "Theme", was.ToString(), _theme.Current.ToString()));
        ThemeChanged?.Invoke();
    }
}
