using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// One STA thread with a WPF <see cref="Application"/> holding the light theme, for tests of whole
/// pages (#130): a page's XAML looks its styles up in the application's resources as it loads, and
/// a process can only ever have one application, so every page test shares this thread.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Dispatcher = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/SqlVitals.Desktop;component/Styles/LightTheme.xaml", UriKind.Relative)));
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name         = "Page tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    /// <summary>Runs a test on the UI thread, one at a time, and rethrows what it threw.</summary>
    public static void Run(Action test)
    {
        Exception? error = null;
        Dispatcher.Value.Invoke(() =>
        {
            try { test(); }
            catch (Exception ex) { error = ex; }
        });
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Lets the dispatcher run until the task finishes, so its awaits on this thread can.</summary>
    public static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Lays an element out at a size, off screen, so its templates and bindings apply.</summary>
    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    /// <summary>Every element of a type under an element, depth first.</summary>
    public static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var deeper in Find<T>(child))
                yield return deeper;
        }
    }
}
