using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Deadlocks;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A page tells the top bar what it read through <see cref="IShellStatus"/> (#166), not through
/// the main window: these run with no window at all, and a fake writes the reports down. Its own
/// Read again goes through its view model, as Refresh does (#169).
/// </summary>
public sealed class ShellStatusPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShellStatus _shell = new();

    private ConnectionSettingsService Settings()
    {
        Directory.CreateDirectory(_dir);
        return new ConnectionSettingsService(_dir);
    }

    private static readonly DeadlockHistory NoDeadlocks = new([], DeadlockSource.RingBuffer, 0, 0, null, null);

    // Lets the page's async click handler run to its end on this thread.
    private static void Until(Func<bool> done)
    {
        var giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < giveUp)
            UiThread.Wait(Task.Delay(10));
    }

    // The page as the page factory makes it: its view model reads through it and reports to the fake.
    private DeadlocksPage Connected(DeadlocksPage page)
    {
        page.ViewModel.LoadThrough(page);
        page.ViewModel.Connect("Deadlocks", _shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return page;
    }

    [Fact]
    public void ReadAgain_ReportsTheRead_ToTheShell_WithoutAWindow() => UiThread.Run(() =>
    {
        Assert.Null(Application.Current.MainWindow);
        var reads = 0;
        var page  = Connected(new DeadlocksPage(new FakeRepository { GetDeadlockHistoryAsync = () => { reads++; return Task.FromResult(NoDeadlocks); } },
                                                Settings()));

        page.BtnReload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Until(() => _shell.Reports.Count > 0);

        Assert.Equal(1, reads);
        Assert.Equal(["Read Refresh"], _shell.Reports);
        Assert.Same(page.ViewModel, _shell.From.Single());
    });

    [Fact]
    public void ReadAgain_ThatFails_IsReportedOnce_AsARefresh() => UiThread.Run(() =>
    {
        var failed = 0;
        var page   = Connected(new DeadlocksPage(new FakeRepository
        {
            GetDeadlockHistoryAsync = () => { failed++; return Task.FromException<DeadlockHistory>(new InvalidOperationException("Timeout expired.")); },
        }, Settings()));

        page.BtnReload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Until(() => _shell.Reports.Count > 0);

        Assert.Equal(1, failed);
        Assert.Equal(["Error Refresh: Timeout expired."], _shell.Reports);
        Assert.False(page.ViewModel.IsBusy);
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a file still open: the temp folder keeps it */ }
    }
}
