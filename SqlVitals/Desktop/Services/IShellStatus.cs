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

    /// <summary>
    /// The page began to read something the user waits on (#117): the loading bar shows it if it
    /// runs long enough, while the page is on screen. A timed read shows nothing.
    /// </summary>
    /// <param name="page">The page's view model, or the page itself.</param>
    /// <returns>What ends it: <see cref="IReading.Done"/> when it read, then dispose it, however it went.</returns>
    IReading ReportReading(object page, ReadingRequest request);
}

/// <summary>A read the loading bar shows (#117).</summary>
/// <param name="Read">How it came about: a timed read shows no bar.</param>
/// <param name="Doing">
/// What it reads, for a part of the page read on its own: "Checking fragmentation". Null for the
/// page's own read, which the bar names after the page: "Loading Backups".
/// </param>
/// <param name="Done">Said to a screen reader when it read: "Fragmentation checked"; null for a page's own "Backups loaded".</param>
/// <param name="LongestQuery">
/// The longest one of its queries may run, when some set their own limit
/// (<c>SqlTarget.ExtendedCommandTimeoutSeconds</c>); null when each has the Command timeout.
/// </param>
/// <param name="Stop">What the bar's Stop does; null offers no Stop.</param>
public sealed record ReadingRequest(PageRead Read, string? Doing = null, string? Done = null,
                                    TimeSpan? LongestQuery = null, Action? Stop = null);

/// <summary>A read the loading bar may be showing (#117): disposing it ends it, and the bar goes.</summary>
public interface IReading : IDisposable
{
    /// <summary>It read: a screen reader hears so. Disposed without it, it failed or was stopped, and says nothing here.</summary>
    void Done();
}

/// <summary>A read nothing shows: a timed one, or one from a page no longer on screen.</summary>
public sealed class NoReading : IReading
{
    public static readonly NoReading Instance = new();

    private NoReading() { }

    public void Done() { }

    public void Dispose() { }
}
