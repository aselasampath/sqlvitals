using System.Windows.Threading;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Runs a view model's test on the test's own thread with a dispatcher, as the app's UI thread has
/// one (#189): what the monitoring hears on the thread pool comes back to it, and an await in the
/// view model continues on it, while <see cref="UiThread.Wait"/> lets it run. Not an STA thread,
/// and no WPF element: what is tested is the view model alone.
/// </summary>
internal static class OnDispatcher
{
    public static void Run(Action test)
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            test();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }
}
