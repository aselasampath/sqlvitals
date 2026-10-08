namespace SqlVitals.Desktop.Services;

/// <summary>How a page came to read: opened, the top bar's Refresh (or F5), or its own timer.</summary>
public enum PageRead
{
    /// <summary>The page was opened: a failure also says so in a message box.</summary>
    Load,

    /// <summary>Refresh in the top bar, or F5.</summary>
    Refresh,

    /// <summary>The page's timer: a failure only goes to the top bar and the log, so it doesn't pop up on every tick.</summary>
    Timed,
}

/// <summary>
/// What the top bar says about the page on screen (#183): that it read, what went wrong, and
/// that its kind or window changed. A page's view model reports here, through
/// <see cref="ViewModels.PageViewModel"/>, rather than to the window. A report from a page that
/// is no longer on screen is only logged.
/// </summary>
/// <remarks>
/// A code-behind page that reads on its own (a timer, Read again) reports here too, with itself
/// as the page: it takes the service in its constructor (#166). No page refers to the window.
/// </remarks>
public interface IShellStatus
{
    /// <summary>The page read: a snapshot's "Taken" or a live page's "As of" is now.</summary>
    /// <param name="page">The page's view model, or the page itself.</param>
    void ReportRead(object page, PageRead read);

    /// <summary>The page couldn't read: logged, and the error on the top bar while the page is on screen.</summary>
    /// <param name="page">The page's view model, or the page itself.</param>
    /// <param name="source">The page, in the log: its tag.</param>
    void ReportError(object page, string source, Exception exception, PageRead read);

    /// <summary>The page's kind, window or pause changed (<see cref="Pages.IPageContext"/>): the top bar follows.</summary>
    void ReportContextChanged(object page);
}
