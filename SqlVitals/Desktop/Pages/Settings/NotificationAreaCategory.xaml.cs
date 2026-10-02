using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Notification area (#131): whether closing the window keeps SqlVitals running (#35).</summary>
internal partial class NotificationAreaCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;

    public NotificationAreaCategory(SettingsContext context)
    {
        _context = context;
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.NotificationArea).Subtitle;
        ChkKeepRunningInTray.IsChecked = _context.Service.Load().KeepRunningInTray;
    }

    public Task<bool> TryLeaveAsync() => Task.FromResult(true);

    private void ChkKeepRunningInTray_Click(object sender, RoutedEventArgs e)
    {
        var keep = ChkKeepRunningInTray.IsChecked == true;
        try
        {
            _context.Service.SetKeepRunningInTray(keep);
            SettingsContext.ShowStatus(TxtTrayStatus,
                keep
                    ? "Saved: minimizing or closing the window keeps SqlVitals running in the notification area."
                    : "Saved: closing the window exits SqlVitals and stops monitoring.",
                success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtTrayStatus, $"Could not save the setting: {ex.Message}", success: false);
            ChkKeepRunningInTray.IsChecked = !keep;
        }
    }
}
