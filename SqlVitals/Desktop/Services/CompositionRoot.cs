using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlVitals.Desktop.Services.Wpf;
using SqlVitals.Desktop.ViewModels.Shell;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Where the app's services are made (#182, #166): the WPF-free services view models ask for
/// (dialogs, the clipboard, files, navigation, the theme and the UI thread), the top bar and the
/// timed refresh, the pages with their factory (#183), the app's own services (settings, the
/// history, monitoring, chat channels, export, the repositories and the log), and the shell's view
/// model (#191).
/// </summary>
/// <remarks>
/// <c>App.OnStartup</c> builds it with the main window, and asks it for the window. The page factory hands a page
/// its services (<see cref="PageRegistrations"/>); a page built elsewhere takes them as optional
/// constructor parameters and falls back to <see cref="Get{T}"/>; controls, windows and helpers
/// that WPF makes ask it directly. A test that builds a page passes its fakes, or gets the app's
/// services, built on first use.
/// </remarks>
public static class CompositionRoot
{
    private static IServiceProvider? _services;
    private static readonly object Gate = new();

    /// <summary>The app's services; built on first use when the app hasn't built them yet.</summary>
    public static IServiceProvider Services
    {
        get
        {
            lock (Gate)
                return _services ??= Build();
        }
    }

    /// <summary>A service of the app's; throws when none is registered.</summary>
    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Builds the app's services and makes them the ones <see cref="Get{T}"/> returns.</summary>
    /// <param name="window">Adds the main window, which only the app refers to.</param>
    internal static IServiceProvider Start(Func<IServiceCollection, IServiceCollection> window)
    {
        lock (Gate)
            return _services = Build(window);
    }

    /// <summary>A new container with every service of the app's, checked as it is built.</summary>
    /// <param name="window">Adds the main window, as the app does; none without it.</param>
    public static IServiceProvider Build(Func<IServiceCollection, IServiceCollection>? window = null) =>
        (window ?? (s => s))(new ServiceCollection()
            .AddAppLog()
            .AddUiServices()
            .AddAppServices()
            .AddPages()
            .AddShell())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    /// <summary>The shell's view model (#191): the window's logic, with its top bar, sidebar and quick switcher.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services) =>
        services.AddSingleton<ShellViewModel>();

    /// <summary>The WPF implementations of the view models' services.</summary>
    public static IServiceCollection AddUiServices(this IServiceCollection services) =>
        services
            .AddSingleton<IDialogService, WpfDialogService>()
            .AddSingleton<IClipboard, WpfClipboard>()
            .AddSingleton<IFileDialogs, WpfFileDialogs>()
            .AddSingleton<IThemeService>(_ => new WpfThemeService(() => Application.Current?.Resources))
            .AddSingleton<INavigator>(_ => new ShellNavigator(OpenShell))
            // The application's thread: where the app is built, which is the UI thread.
            .AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher))
            // The top bar, and the pages' timed refresh (#183).
            .AddSingleton<IShellStatus>(s => new WpfShellStatus(OpenShell, s.GetRequiredService<ILogger<WpfShellStatus>>()))
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IRefreshScheduler, RefreshScheduler>();

    // The shell's view model while the app's main window shows it: its DataContext (#191).
    private static ShellViewModel? OpenShell() => Application.Current?.MainWindow?.DataContext as ShellViewModel;

    /// <summary><c>ILogger&lt;T&gt;</c> on the log file, through the same factory as <see cref="AppLog"/> (#166).</summary>
    public static IServiceCollection AddAppLog(this IServiceCollection services) =>
        services
            .AddSingleton(AppLog.Factory)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>));

    /// <summary>
    /// The app's own services (#166), one each for the app: the saved settings, the local history,
    /// monitoring, the chat channels, when each Overview was last opened, export, and the
    /// repositories over appsettings.json. The main window starts and stops them.
    /// </summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services) =>
        services
            .AddSingleton<IConfiguration>(_ => RepositoryFactory.ReadAppSettings())
            .AddSingleton<IRepositoryFactory, RepositoryFactory>()
            .AddSingleton(s => new ConnectionSettingsService(log: s.GetRequiredService<ILogger<ConnectionSettingsService>>()))
            // The first-run and Settings permission check (#177).
            .AddSingleton(s => PermissionCheckers.From(s.GetRequiredService<IRepositoryFactory>(), s.GetRequiredService<ConnectionSettingsService>()))
            .AddSingleton(s =>
            {
                var log = s.GetRequiredService<ILogger<HistoryWriter>>();
                return new HistoryWriter(HistoryWriter.DefaultPath, log.Error, onActivityNotSaved: e => log.Info(MonitoringManager.NotSaved(e)));
            })
            .AddSingleton(s =>
            {
                var repositories = s.GetRequiredService<IRepositoryFactory>();
                // Monitoring gives up on a lock after 2 s and skips the sample (#167), rather than queue behind a schema change.
                return new MonitoringManager(s.GetRequiredService<HistoryWriter>(),
                                             (connection, timeout) => repositories.Create(repositories.TargetFor(connection, timeout) with
                                             {
                                                 LockTimeoutMilliseconds = SqlTarget.MonitoringLockTimeoutMilliseconds,
                                             }),
                                             s.GetRequiredService<ILogger<MonitoringManager>>());
            })
            .AddSingleton<IWebhookSender, HttpWebhookSender>()
            // The optional collector service (#172): what it watches, handing servers to it, starting it.
            .AddSingleton<ICollectorControl, WindowsCollectorControl>()
            .AddSingleton(s => new CollectorLink(s.GetRequiredService<ICollectorControl>(), s.GetRequiredService<ConnectionSettingsService>(),
                                                 s.GetRequiredService<IUiDispatcher>(), s.GetRequiredService<TimeProvider>(),
                                                 s.GetRequiredService<ILogger<CollectorLink>>()))
            .AddSingleton<IWatchStarter>(s => new WatchStarter(s.GetRequiredService<MonitoringManager>(), s.GetRequiredService<CollectorLink>(),
                                                               s.GetRequiredService<ConnectionSettingsService>(), s.GetRequiredService<IDialogService>(),
                                                               s.GetRequiredService<ILogger<WatchStarter>>()))
            // Status changes are raised where the app is built: the UI thread.
            .AddSingleton(s => new ChatNotificationService(s.GetRequiredService<IWebhookSender>(),
                                                           log: s.GetRequiredService<ILogger<ChatNotificationService>>().Info,
                                                           ui: SynchronizationContext.Current ?? new DispatcherSynchronizationContext()))
            .AddSingleton(s => new OverviewVisits(s.GetRequiredService<ConnectionSettingsService>(), s.GetRequiredService<TimeProvider>(),
                                                  s.GetRequiredService<ILogger<OverviewVisits>>()))
            // Export reads the connection open when the page is: one per page.
            .AddSingleton<Func<IWaitStatsRepository, ExportService>>(_ => repository => new ExportService(repository));
}
