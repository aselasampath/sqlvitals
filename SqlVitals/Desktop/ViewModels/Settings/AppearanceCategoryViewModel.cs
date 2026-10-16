using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>Settings → Appearance (#131): the light or dark theme, and the grid density (#148).</summary>
public sealed partial class AppearanceCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;
    private GridDensity _density;

    internal AppearanceCategoryViewModel(SettingsContext context)
    {
        _context = context;
        _density = context.Service.Load().GridDensity;
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Appearance).Subtitle;

    /// <summary>The theme was switched: the settings page is opened again in it.</summary>
    public event Action? ThemeChanged;

    private bool IsDark => _context.Theme.Current == AppTheme.Dark;

    public string ThemeButtonText => IsDark ? "☀  Light" : "🌙  Dark";

    public string ThemeButtonName => IsDark ? "Switch to the light theme" : "Switch to the dark theme";

    public string ThemeText => IsDark
        ? "Dark at the moment. Switch every page to the light theme."
        : "Light at the moment. Switch every page to the dark theme.";

    [RelayCommand]
    private void ToggleTheme()
    {
        var was = _context.Theme.Current;
        _context.Theme.Set(was == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
        if (_context.Theme.Current == was)
            return;

        // Not in the settings file, so logged here rather than by the settings diff.
        _context.Monitoring.Log(new ActivityEntry(DateTime.UtcNow, ActivityEvents.SettingChanged, null, null, ActivitySources.Settings,
                                                  "Theme", was.ToString(), _context.Theme.Current.ToString()));
        ThemeChanged?.Invoke();
    }

    /// <summary>Tighter rows (#148): every grid at once, and saved for the next start.</summary>
    public bool IsCompact
    {
        get => _density == GridDensity.Compact;
        set => SetDensity(value ? GridDensity.Compact : GridDensity.Comfortable);
    }

    public bool IsComfortable
    {
        get => _density == GridDensity.Comfortable;
        set => SetDensity(value ? GridDensity.Comfortable : GridDensity.Compact);
    }

    public string DensityText => _density == GridDensity.Compact
        ? "Compact: tighter rows with the same 12 px text, for more rows on a small or shared screen. Every grid, at once."
        : "Comfortable: rows with room around the text, the look grids have always had. Compact fits more rows.";

    private void SetDensity(GridDensity density)
    {
        if (density == _density)
            return;
        _density = density;
        _context.ApplyDensity(density);
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsComfortable));
        OnPropertyChanged(nameof(DensityText));
        try { _context.Service.SetGridDensity(density); }
        catch (Exception ex) { AppLog.Error("Appearance: could not save the grid density", ex); }
    }
}
