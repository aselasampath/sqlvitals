using System.Windows;
using System.Windows.Threading;
using SqlVitals.Desktop.Services.Wpf;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// What every Settings category's view model needs from the app (#131, #190), handed down by
/// Settings' view model. Nothing here is WPF to its users: the defaults that are (the grid density's
/// resources, the UI thread) are made here, and tests give their own.
/// </summary>
internal sealed class SettingsContext(
    ConnectionSettingsService service,
    MonitoringManager         monitoring,
    ChatNotificationService   chat,
    Func<bool, Task>          applyConnections,
    IDialogService            dialogs,
    CollectorLink?            collectors = null,
    PermissionChecker?        checkPermissions = null,
    IWatchStarter?            starter = null,
    IClipboard?               clipboard = null)
{
    public ConnectionSettingsService Service    { get; } = service;
    public MonitoringManager         Monitoring { get; } = monitoring;
    public ChatNotificationService   Chat       { get; } = chat;

    /// <summary>Questions: Save, discard or stay, and Yes or No before anything that can't be undone.</summary>
    public IDialogService Dialogs { get; } = dialogs;

    /// <summary>The collector service (#172); null in tests that don't need it.</summary>
    public CollectorLink? Collectors { get; } = collectors;

    /// <summary>
    /// Reads a connection's permissions (#177): the first-run flow and a card's Check permissions.
    /// Null in tests that don't need it: then there is no Check permissions.
    /// </summary>
    public PermissionChecker? CheckPermissions { get; } = checkPermissions;

    /// <summary>Start monitoring, as everywhere else: it may ask whether the collector service should (#172).</summary>
    public IWatchStarter Starter { get; } = starter ?? new DirectWatchStarter(monitoring);

    /// <summary>Copy script; the app's clipboard unless a test gives its own.</summary>
    public IClipboard Clipboard => clipboard ?? CompositionRoot.Get<IClipboard>();

    /// <summary>
    /// Tells the running app the saved connections changed, so it picks up a new or edited active
    /// connection. True asks it to open the active server's Overview afterwards.
    /// </summary>
    public Func<bool, Task> ApplyConnections { get; } = applyConnections;

    /// <summary>
    /// The UI thread, for a settings change told on another one. The dispatcher of the thread that
    /// made the context unless a test gives its own.
    /// </summary>
    public IUiDispatcher Ui
    {
        get => _ui ??= new WpfUiDispatcher(_thread ?? Dispatcher.CurrentDispatcher);
        init => _ui = value;
    }
    private IUiDispatcher? _ui;

    // The dispatcher of the thread that made it, if it has one; looked up, never made here.
    private readonly Dispatcher? _thread = Dispatcher.FromThread(Thread.CurrentThread);

    /// <summary>The app's clock for the history file's size, re-read while Monitoring &amp; history is shown (#169).</summary>
    public IRefreshScheduler? Scheduler { get; init; }

    /// <summary>The light or dark theme; the app's unless a test gives its own.</summary>
    public IThemeService Theme
    {
        get => _theme ?? CompositionRoot.Get<IThemeService>();
        init => _theme = value;
    }
    private readonly IThemeService? _theme;

    /// <summary>Signs in with a connection string and closes again: null on success, otherwise why not, redacted.</summary>
    public Func<string, Task<string?>> TestConnection { get; init; } = ConnectionTest.TryOpenAsync;

    /// <summary>The databases a connection string's login can open, for the editor's Database list.</summary>
    public Func<string, Task<IReadOnlyList<string>>> ListDatabases { get; init; } = ConnectionTest.ListDatabasesAsync;

    /// <summary>Every grid's density, at once (#148).</summary>
    public Action<GridDensity> ApplyDensity { get; init; } = density =>
    {
        if (Application.Current is { } app)
            GridDensityResources.Apply(app.Resources, density);
    };

    /// <summary>Opens a folder in Explorer: the app log's.</summary>
    public Action<string> OpenFolder { get; init; } = folder =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });

    /// <summary>Opens another category, or a connection's tab, in Settings; Settings' view model sets it.</summary>
    public Action<SettingsLocation> Open { get; set; } = _ => { };

    /// <summary>"View on Home": the server's Overview (#143); Settings' view model sets it.</summary>
    public Action<Guid> OpenServer { get; set; } = _ => { };

    /// <summary>An error's text may echo a secret back (#175).</summary>
    public static string Redact(string message) => ConnectionSettingsService.RedactSecrets(message);
}
