using System.Diagnostics;
using System.Text;
using System.Windows.Threading;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The binding errors WPF reports while a test runs (#184): a path the view model doesn't have, an
/// element name that isn't there, a value that can't be converted. WPF only writes them to its
/// trace, and only with a debugger attached, so a broken binding otherwise fails silently: the
/// page just shows nothing. Only errors on the thread that started watching are kept, so a test on
/// another thread can't fail this one.
/// </summary>
internal sealed class BindingErrors : TraceListener
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly StringBuilder _line = new();
    private readonly List<string> _errors = [];
    private readonly SourceLevels _level;

    private BindingErrors()
    {
        // Every page test shares one thread: what an earlier test left queued on it (a layout, a
        // state put back at idle) would otherwise run as this one lets the dispatcher run, and its
        // errors would count as this test's (#185). Let it run first.
        if (Dispatcher.FromThread(Thread.CurrentThread) is not null)
            UiThread.Wait(IdleAsync());

        // Refresh turns WPF's tracing on without a debugger; it resets the level, so set it after.
        PresentationTraceSources.Refresh();
        var source = PresentationTraceSources.DataBindingSource;
        _level = source.Switch.Level;
        source.Switch.Level = SourceLevels.Warning;
        source.Listeners.Add(this);
    }

    private static async Task IdleAsync() => await Dispatcher.Yield(DispatcherPriority.SystemIdle);

    /// <summary>Starts keeping the binding errors of this thread, until disposed.</summary>
    public static BindingErrors Watch() => new();

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return [.. _errors]; }
    }

    /// <summary>Fails with every binding error seen so far.</summary>
    public void AssertNone()
    {
        var errors = Errors;
        Assert.True(errors.Count == 0, "Binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    public override void Write(string? message)
    {
        if (Environment.CurrentManagedThreadId == _thread)
            _line.Append(message);
    }

    public override void WriteLine(string? message)
    {
        if (Environment.CurrentManagedThreadId != _thread)
            return;
        _line.Append(message);
        var line = _line.ToString();
        _line.Clear();
        if (!WpfsOwn(line))
            lock (_errors) _errors.Add(line);
    }

    // WPF's own item styles bind HorizontalContentAlignment and VerticalContentAlignment to the
    // ItemsControl above them, and log error 4 when a ComboBoxItem (or MenuItem, ListBoxItem) is
    // made before it has one, depending on when its containers are generated. Not the page's
    // binding, so not counted; any other error 4 still is.
    private static bool WpfsOwn(string line) =>
        line.Contains("Error: 4 :", StringComparison.Ordinal)
        && line.Contains("AncestorType='System.Windows.Controls.ItemsControl'", StringComparison.Ordinal)
        && (line.Contains("Path=HorizontalContentAlignment;", StringComparison.Ordinal) || line.Contains("Path=VerticalContentAlignment;", StringComparison.Ordinal))
        && (line.Contains("target element is 'ComboBoxItem'", StringComparison.Ordinal)
            || line.Contains("target element is 'MenuItem'", StringComparison.Ordinal)
            || line.Contains("target element is 'ListBoxItem'", StringComparison.Ordinal));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Remove(this);
            source.Switch.Level = _level;
        }
        base.Dispose(disposing);
    }
}
