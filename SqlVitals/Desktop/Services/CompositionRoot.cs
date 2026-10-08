using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Services.Wpf;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Where the app's services are made (#182): the WPF-free services view models ask for (dialogs,
/// the clipboard, files, navigation, the theme and the UI thread), the top bar and the timed
/// refresh, and the pages with their factory (#183). #166 adds settings, monitoring and the
/// repositories.
/// </summary>
/// <remarks>
/// <c>App.OnStartup</c> builds it before the main window opens. The page factory hands a page its
/// services (<see cref="PageRegistrations"/>); a page built elsewhere takes them as optional
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
    internal static IServiceProvider Start()
    {
        lock (Gate)
            return _services = Build();
    }

    /// <summary>A new container with every service of the app's, checked as it is built.</summary>
    public static IServiceProvider Build() =>
        new ServiceCollection()
            .AddUiServices()
            .AddPages()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    /// <summary>The WPF implementations of the view models' services.</summary>
    public static IServiceCollection AddUiServices(this IServiceCollection services) =>
        services
            .AddSingleton<IDialogService, WpfDialogService>()
            .AddSingleton<IClipboard, WpfClipboard>()
            .AddSingleton<IFileDialogs, WpfFileDialogs>()
            .AddSingleton<IThemeService>(_ => new WpfThemeService(() => Application.Current?.Resources))
            .AddSingleton<INavigator>(_ => new ShellNavigator(() => Application.Current?.MainWindow as MainWindow))
            // The application's thread: where the app is built, which is the UI thread.
            .AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher))
            // The top bar, and the pages' timed refresh (#183).
            .AddSingleton<IShellStatus>(_ => new WpfShellStatus(() => Application.Current?.MainWindow as MainWindow))
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IRefreshScheduler, RefreshScheduler>();
}
