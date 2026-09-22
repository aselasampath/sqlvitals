namespace SqlVitals.Desktop;

public partial class App : System.Windows.Application
{
    public bool IsDarkTheme { get; private set; } = false;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
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
    }

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
