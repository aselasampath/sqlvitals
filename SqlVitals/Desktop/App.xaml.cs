namespace SqlVitals.Desktop;

public partial class App : System.Windows.Application
{
    // One SqlVitals per Windows session: a second copy would monitor the same servers again,
    // write the same history file and raise every notification twice. Easy to start by
    // accident once closing the window leaves SqlVitals running in the tray (#35).
    private const string InstanceMutexName = @"Local\SqlVitals.Desktop.SingleInstance";
    private const string ShowEventName     = @"Local\SqlVitals.Desktop.Show";

    // Held for the life of the process; Windows releases it when the process ends.
    private static System.Threading.Mutex? _instanceMutex;

    public bool IsDarkTheme { get; private set; } = false;

    /// <summary>
    /// Set when SqlVitals is really quitting (Exit in the tray menu, Windows signing out or
    /// shutting down), so closing the window doesn't just hide it to the tray.
    /// </summary>
    public bool IsExiting { get; set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // Created before the mutex, so a second copy that finds the mutex always finds this too.
        var showEvent = new System.Threading.EventWaitHandle(
            false, System.Threading.EventResetMode.AutoReset, ShowEventName);
        _instanceMutex = new System.Threading.Mutex(true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // Let the running copy come to the front, then ask it to show its window.
            AllowSetForegroundWindow(AsfwAny);
            showEvent.Set();
            Shutdown();
            return;
        }

        var showListener = new System.Threading.Thread(() =>
        {
            while (showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        })
        { IsBackground = true, Name = "SqlVitals show listener" };
        showListener.Start();

        SessionEnding += (_, _) => IsExiting = true;

        Services.AppLog.DeleteExpiredFiles();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Failures off the UI thread never reach DispatcherUnhandledException.
        System.AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is System.Exception ex)
                Services.AppLog.Error("Unhandled exception on a background thread", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            Services.AppLog.Error("Unobserved task exception", args.Exception);

        new MainWindow().Show();
    }

    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Services.AppLog.Error("Unhandled exception", e.Exception);
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:\n\n{Services.ConnectionSettingsService.RedactSecrets(e.Exception.Message)}",
            "SqlVitals", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var uri = new System.Uri(IsDarkTheme
            ? "pack://application:,,,/Styles/DarkTheme.xaml"
            : "pack://application:,,,/Styles/LightTheme.xaml");
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = uri });
    }
}
