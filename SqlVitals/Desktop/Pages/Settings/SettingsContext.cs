using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ChatNotifications;

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
    Action<Guid>              openServer,
    CollectorLink?            collectors = null)
{
    /// <summary>The collector service (#172); null in tests that don't need it.</summary>
    public CollectorLink? Collectors { get; } = collectors;

    public ConnectionSettingsService Service    { get; } = service;
    public MonitoringManager         Monitoring { get; } = monitoring;
    public ChatNotificationService   Chat       { get; } = chat;

    /// <summary>
    /// Tells the running app the saved connections changed, so it picks up a new or edited active
    /// connection. True asks it to open the active server's Overview afterwards.
    /// </summary>
    public Func<bool, Task> ApplyConnections { get; } = applyConnections;

    /// <summary>Asks the user; tests answer instead of a message box.</summary>
    public Ask Ask { get; } = ask;

    /// <summary>Opens another category, or a connection's tab, in Settings.</summary>
    public Action<SettingsLocation> Open { get; } = open;

    /// <summary>"View on Home": the server's Overview (#143).</summary>
    public Action<Guid> OpenServer { get; } = openServer;

    /// <summary>
    /// A status line: "✓ Saved…" in green, "✗ Could not…" in red, anything else muted. Empty hides it.
    /// </summary>
    public static void ShowStatus(TextBlock target, string message, bool? success)
    {
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        // An error's text may echo a secret back (#175).
        message = Redact(message);
        target.Text = success switch
        {
            true  => "✓  " + message,
            false => "✗  " + message,
            null  => message,
        };
        target.SetResourceReference(TextBlock.ForegroundProperty, success switch
        {
            true  => "Success",
            false => "Danger",
            null  => "TextMuted",
        });
    }

    public static string Redact(string message) => ConnectionSettingsService.RedactSecrets(message);
}
