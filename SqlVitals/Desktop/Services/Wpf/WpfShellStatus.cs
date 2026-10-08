namespace SqlVitals.Desktop.Services.Wpf;

/// <summary>
/// <see cref="IShellStatus"/> on the main window's top bar: every report is logged as before, and
/// shown when it is about the page on screen.
/// </summary>
internal sealed class WpfShellStatus(Func<MainWindow?> window) : IShellStatus
{
    public void ReportRead(object page, PageRead read) => window()?.ShowRead(page, read);

    public void ReportError(object page, string source, Exception exception, PageRead read)
    {
        AppLog.Error(read switch
        {
            PageRead.Load    => $"{source}: load failed",
            PageRead.Refresh => $"{source}: refresh failed",
            _                => $"{source}: collection failed",
        }, exception);
        window()?.ShowError(page, exception, read);
    }

    public void ReportContextChanged(object page) => window()?.ShowContextOf(page);
}
