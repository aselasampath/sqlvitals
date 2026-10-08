namespace SqlVitals.Desktop.Services;

/// <summary>
/// Runs a page's timed refresh (#183) on the UI thread, on the app's <see cref="TimeProvider"/>,
/// so a test can step the time instead of waiting. <see cref="ViewModels.PageViewModel"/> uses it
/// for <c>RefreshInterval</c>; a page no longer needs a <c>DispatcherTimer</c> of its own (#169).
/// </summary>
public interface IRefreshScheduler
{
    /// <summary>
    /// Calls <paramref name="tick"/> every <paramref name="interval"/>, first one interval from now,
    /// until the result is disposed. A tick due while the last one still runs is skipped.
    /// </summary>
    IDisposable Every(TimeSpan interval, Func<Task> tick);
}
