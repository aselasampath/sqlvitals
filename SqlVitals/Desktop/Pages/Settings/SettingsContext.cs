using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>A question with buttons, as <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/> asks it.</summary>
internal delegate MessageBoxResult Ask(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon);

/// <summary>A category of Settings (#131), shown in the settings page beside its side menu.</summary>
internal interface ISettingsCategory
{
    /// <summary>
    /// Whether the category can be left. With unsaved changes it asks to save, discard or stay;
    /// false when the user stays, or the save failed.
    /// </summary>
    Task<bool> TryLeaveAsync();

    /// <summary>
    /// Opened again after another category: built once per visit to Settings, it picks up what
    /// changed meanwhile.
    /// </summary>
    void OnShownAgain() { }
}

/// <summary>What every Settings category needs from the app, handed down by the settings page (#131).</summary>
internal sealed class SettingsContext(
    ConnectionSettingsService service,
    MonitoringManager         monitoring,
    ChatNotificationService   chat,
    Func<bool, Task>          applyConnections,
    Ask                       ask,
    Action<SettingsLocation>  open,
    Action<Guid>              openServer)
{
    public ConnectionSettingsService Service    { get; } = service;
    public MonitoringManager         Monitoring { get; } = monitoring;
    public ChatNotificationService   Chat       { get; } = chat;

    /// <summary>
    /// Tells the running app the saved connections changed, so it picks up a new or edited active
    /// connection. True asks it to open Live Metrics afterwards.
    /// </summary>
    public Func<bool, Task> ApplyConnections { get; } = applyConnections;

    /// <summary>Asks the user; tests answer instead of a message box.</summary>
    public Ask Ask { get; } = ask;

    /// <summary>Opens another category, or a connection's tab, in Settings.</summary>
    public Action<SettingsLocation> Open { get; } = open;

    /// <summary>"View on Home": the server's detail page (#130).</summary>
    public Action<Guid> OpenServer { get; } = openServer;

    /// <summary>
    /// A status line: "✓ Saved…" in green, "✗ Could not…" in red, anything else muted. Empty hides it.
    /// </summary>
    public static void ShowStatus(TextBlock target, string message, bool? success)
    {
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        target.Text = success switch
        {
            true  => "✓  " + message,
            false => "✗  " + message,
            null  => message,
        };
        target.Foreground = success switch
        {
            true  => Green,
            false => Red,
            null  => Muted,
        };
    }

    public static string Redact(string message) => ConnectionSettingsService.RedactSecrets(message);

    private static readonly Brush Green = Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush Red   = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush Muted = Frozen(0x94, 0xA3, 0xB8);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
