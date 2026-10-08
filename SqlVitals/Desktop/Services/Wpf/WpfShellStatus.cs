using Microsoft.Extensions.Logging;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary>
/// <see cref="IShellStatus"/> on the main window's top bar: every report is logged as before, and
/// shown when it is about the page on screen.
/// </summary>
internal sealed class WpfShellStatus(Func<MainWindow?> window, ILogger<WpfShellStatus> log) : IShellStatus
{
    public void ReportRead(object page, PageRead read) => window()?.ShowRead(page, read);

    public void ReportError(object page, string source, Exception exception, PageRead read)
    {
        // A read cancelled because the page was left, or its server switched, isn't an error (#167).
        if (exception is OperationCanceledException)
            return;
        log.Error(read switch
        {
            PageRead.Load    => $"{source}: load failed",
            PageRead.Refresh => $"{source}: refresh failed",
            _                => $"{source}: collection failed",
        }, exception);
        window()?.ShowError(page, exception, read);
    }

    public void ReportContextChanged(object page) => window()?.ShowContextOf(page);
}
