using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Activity log &amp; diagnostics (#131): the activity log (#115), and the app log's folder.</summary>
internal partial class ActivityCategory : UserControl, ISettingsCategory
{
    public ActivityCategory(SettingsContext context)
    {
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Activity).Subtitle;

        var store = context.Service.Load();
        ActivityLogView.Initialize(context.Monitoring.HistoryReader, store.Connections.Select(c => (c.Id, c.DisplayName)));
        TxtLogInfo.Text = $"{FileLog.Directory} — one file per day, kept for {FileLog.RetentionDays} days. " +
                          "Passwords and connection strings are never written. Attach the latest file to a bug report.";
    }

    public Task<bool> TryLeaveAsync() => Task.FromResult(true);

    // What was logged meanwhile, e.g. the changes just made in another category.
    public void OnShownAgain() => _ = ActivityLogView.RefreshQuietlyAsync();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Created up front so the button works before anything has been logged.
            System.IO.Directory.CreateDirectory(FileLog.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = FileLog.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtLogStatus, $"Could not open the log folder: {ex.Message}", success: false);
        }
    }
}
