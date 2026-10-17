using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Activity log &amp; diagnostics (#131): the activity log (#115), read by its own control
/// from <see cref="HistoryReader"/>, and the app log's folder.
/// </summary>
public sealed partial class ActivityCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;

    internal ActivityCategoryViewModel(SettingsContext context)
    {
        _context    = context;
        Connections = context.Service.Load().Connections.Select(c => (c.Id, c.DisplayName)).ToList();
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Activity).Subtitle;

    /// <summary>What was logged meanwhile, e.g. the changes just made in another category: the log reads again.</summary>
    public event Action? Reread;

    public override void OnShownAgain() => Reread?.Invoke();

    /// <summary>Where the activity log reads from.</summary>
    public IHistoryReader HistoryReader => _context.Monitoring.HistoryReader;

    /// <summary>The connections the log can be filtered on.</summary>
    public IReadOnlyList<(Guid Id, string Name)> Connections { get; }

    public string LogInfo { get; } = $"{FileLog.Directory} — one file per day, kept for {FileLog.RetentionDays} days. " +
                                     "Passwords and connection strings are never written. Attach the latest file to a bug report.";

    public StatusLine LogStatus { get; } = new();

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            // Created up front so the button works before anything has been logged.
            System.IO.Directory.CreateDirectory(FileLog.Directory);
            _context.OpenFolder(FileLog.Directory);
        }
        catch (Exception ex)
        {
            LogStatus.Show($"Could not open the log folder: {ex.Message}", success: false);
        }
    }
}
