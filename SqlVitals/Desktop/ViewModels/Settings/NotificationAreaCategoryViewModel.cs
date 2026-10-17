using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>Settings → Notification area (#131): whether closing the window keeps SqlVitals running (#35).</summary>
public sealed partial class NotificationAreaCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;
    private bool _keepRunning;

    internal NotificationAreaCategoryViewModel(SettingsContext context)
    {
        _context     = context;
        _keepRunning = context.Service.Load().KeepRunningInTray;
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.NotificationArea).Subtitle;

    public bool KeepRunning => _keepRunning;

    public StatusLine Status { get; } = new();

    /// <summary>The switch: saved as it's clicked, and put back when it can't be.</summary>
    [RelayCommand]
    private void ToggleKeepRunning()
    {
        var keep = !_keepRunning;
        try
        {
            _context.Service.SetKeepRunningInTray(keep);
            _keepRunning = keep;
            Status.Show(keep
                    ? "Saved: minimizing or closing the window keeps SqlVitals running in the notification area."
                    : "Saved: closing the window exits SqlVitals and stops monitoring.",
                success: true);
        }
        catch (Exception ex)
        {
            Status.Show($"Could not save the setting: {ex.Message}", success: false);
        }
        OnPropertyChanged(nameof(KeepRunning));
    }
}
