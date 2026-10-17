using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Shell;
using SqlVitals.Engine.Loading;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The loading bar (#117): its view model on a fake clock (when it shows, what it says, Stop, what
/// a screen reader hears), and the bar, the verdict's placeholder and an empty grid as WPF builds
/// them in each theme, off screen on an STA thread. The bar is also rendered to a PNG under
/// %TEMP%\SqlVitalsTests\loading-bar, to look at.
/// </summary>
public sealed class LoadingBarTests
{
    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static readonly Wait IndexHealth = new("Loading Index Health", "Index Health loaded",
        new WaitLimit(TimeSpan.FromSeconds(30), WaitLimitKind.EachQuery, "Settings → Command timeout"));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeUiDispatcher _ui = new();

    private LoadingBarViewModel Bar() => new(_time, new RefreshScheduler(_time, _ui));

    private void Step(double seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        _ui.RunPosted();
    }

    // ── The view model ───────────────────────────────────────────────────

    [Fact]
    public void AFastRead_ShowsNothing_AndSaysNothing()
    {
        var bar = Bar();
        using (var reading = bar.Begin(IndexHealth, stop: () => { }, dims: false))
        {
            Step(0.2);
            reading.Done();
        }
        Step(1);

        Assert.False(bar.IsShown);
        Assert.Equal(string.Empty, bar.Announcement);
    }

    [Fact]
    public void ASlowRead_Shows_CountsTheSeconds_SaysWhenItGivesUp_AndIsHeardComingAndGoing()
    {
        var bar   = Bar();
        var heard = new List<string>();
        bar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LoadingBarViewModel.Announcement)) heard.Add(bar.Announcement); };
        var reading = bar.Begin(IndexHealth, stop: () => { }, dims: false);

        Step(0.3);
        Assert.True(bar.IsShown);
        Assert.Equal("Loading Index Health…", bar.Text);
        Assert.True(bar.CanStop);
        Assert.Equal(["Loading Index Health"], heard);

        Step(2.7);
        Assert.Equal("Loading Index Health… 3 s", bar.Text);
        Step(4);
        Assert.Equal("Loading Index Health… 7 s", bar.Text);
        Step(5);
        Assert.Equal("Loading Index Health… 12 s · each query gives up after 30 s (Settings → Command timeout)", bar.Text);

        reading.Done();
        reading.Dispose();
        Assert.False(bar.IsShown);
        Assert.Equal(["Loading Index Health", "Index Health loaded"], heard);
    }

    [Fact]
    public void Stop_RunsTheReadsStop_Once_AndItsEndSaysItStopped_NotThatItLoaded()
    {
        var bar     = Bar();
        var stops   = 0;
        var stopped = new List<LoadingBarViewModel.StoppedWait>();
        bar.Stopped += stopped.Add;
        var reading = bar.Begin(IndexHealth, stop: () => stops++, dims: true, ofPage: true);
        Step(14.2);

        bar.StopCommand.Execute(null);
        Assert.Equal(1, stops);
        Assert.False(bar.CanStop);
        Assert.False(bar.StopCommand.CanExecute(null));

        reading.Dispose();
        var (wait, waited, ofPage) = Assert.Single(stopped);
        Assert.Same(IndexHealth.Doing, wait.Doing);
        Assert.Equal(TimeSpan.FromSeconds(14.2), waited);
        Assert.True(ofPage);
        Assert.NotEqual("Index Health loaded", bar.Announcement);
    }

    // Code review: only the bar appearing was heard, not another wait taking its place.
    [Fact]
    public void AnotherWaitTakingTheBar_IsHeard_ButNotTheBarLingering()
    {
        var bar   = Bar();
        var heard = new List<string>();
        bar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LoadingBarViewModel.Announcement)) heard.Add(bar.Announcement); };
        using var page = bar.Begin(IndexHealth, stop: () => { }, dims: false, ofPage: true);
        Step(1);

        var part = bar.Begin(new Wait("Checking fragmentation", "Fragmentation checked"), stop: () => { }, dims: false);
        Step(0.3);
        Assert.Equal("Checking fragmentation…", bar.Text);
        part.Done();
        part.Dispose();
        // Back to the page's read, which is heard again.
        Assert.Equal(["Loading Index Health", "Checking fragmentation", "Fragmentation checked", "Loading Index Health"], heard);
    }

    [Fact]
    public void ARefresh_DimsThePage_WhileShown_NotWhileItsShortestTimeRunsOut()
    {
        var bar     = Bar();
        var reading = bar.Begin(IndexHealth with { Doing = "Refreshing Index Health" }, stop: null, dims: true);
        Assert.False(bar.DimsPage);
        Step(0.3);
        Assert.True(bar.DimsPage);
        Assert.False(bar.CanStop);

        reading.Done();
        Assert.True(bar.IsShown);
        Assert.False(bar.DimsPage);
        Step(0.3);
        Assert.False(bar.IsShown);
    }

    [Fact]
    public void Cleared_ItGoesAtOnce_AndTheReadsLeftBehindChangeNothing()
    {
        var bar     = Bar();
        var stopped = 0;
        bar.Stopped += _ => stopped++;
        var left = bar.Begin(IndexHealth, stop: () => { }, dims: false);
        Step(1);
        bar.StopCommand.Execute(null);

        bar.Clear();
        Assert.False(bar.IsShown);
        var next = bar.Begin(IndexHealth with { Doing = "Loading Backups" }, stop: null, dims: false);
        Step(0.5);

        left.Dispose();
        Assert.Equal(0, stopped);
        Assert.Equal("Loading Backups…", bar.Text);
        next.Dispose();
    }

    // ── As WPF builds it ─────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void TheBar_SaysWhatItWaitsOn_UnderTheStripe_WithStop(string theme) => OnSta(theme, host =>
    {
        var model = Bar();
        var view  = new LoadingBar { ViewModel = model, AnimatesStripe = false };
        host.Child = new Grid { Height = 160, Children = { view } };
        Assert.Equal(string.Empty, view.Text);

        using var reading = model.Begin(IndexHealth, stop: () => { }, dims: false);
        Step(12);
        Layout(host);

        Assert.Equal("Loading Index Health… 12 s · each query gives up after 30 s (Settings → Command timeout) [Stop]", view.Text);
        Assert.Equal("Loading Index Health", view.Announced);
        // Without animations, the stripe is still: the whole track.
        Assert.Equal(view.Track.ActualWidth, view.Stripe.ActualWidth, precision: 1);
        Assert.True(view.Track.ActualWidth > 1000);
        // The page around the line stays usable.
        Assert.False(view.Track.IsHitTestVisible);
        Render(host, theme, "bar");

        reading.Done();
        Step(1);
        Layout(host);
        Assert.Equal(string.Empty, view.Text);
        Assert.Equal("Index Health loaded", view.Announced);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheVerdictsPlace_SaysLoading_UntilThePageHasAVerdict_OrThatAStoppedLoadReadNothing(string theme) => OnSta(theme, host =>
    {
        var shell = new PageShell { Title = "Backups" };
        host.Child = shell;
        LoadingPlaceholder.SetState(host, PageLoadState.Loading);
        Layout(host);
        Assert.Equal("Loading…", shell.PlaceholderText);

        shell.Verdict = new PageVerdict(VerdictLevel.Ok, "Every database is backed up");
        Assert.Equal(string.Empty, shell.PlaceholderText);
        shell.Verdict = null;
        Assert.Equal("Loading…", shell.PlaceholderText);

        LoadingPlaceholder.SetState(host, PageLoadState.Stopped);
        Assert.StartsWith("Not read: it was stopped before it finished.", shell.PlaceholderText, StringComparison.Ordinal);
        LoadingPlaceholder.SetState(host, PageLoadState.Ready);
        Assert.Equal(string.Empty, shell.PlaceholderText);
    });

    [Theory, MemberData(nameof(Themes))]
    public void AnEmptyGrid_SaysLoading_OnlyWhileThePageFirstReads(string theme) => OnSta(theme, host =>
    {
        // The app has the brush (UseTheme); here, one to tell it by.
        var loading = new SolidColorBrush(Colors.Orange);
        host.Resources[LoadingPlaceholder.GridBackgroundKey] = loading;
        var grid = new DataGrid();
        host.Child = grid;
        Layout(host);
        var card = grid.Background;
        Assert.NotSame(loading, card);

        LoadingPlaceholder.SetState(host, PageLoadState.Loading);
        Assert.Same(loading, grid.Background);

        // Rows arrive: no placeholder; read and empty: no rows, said by nothing.
        grid.Items.Add("a row");
        Assert.Same(card, grid.Background);
        grid.Items.Clear();
        Assert.Same(loading, grid.Background);
        LoadingPlaceholder.SetState(host, PageLoadState.Ready);
        Assert.Same(card, grid.Background);
    });

    [Theory, MemberData(nameof(Themes))]
    public void TheGridsLoading_IsMadeInTheThemesColours_EachTimeTheThemeIsUsed(string theme) => OnSta(theme, host =>
    {
        var resources = host.Resources;
        LoadingPlaceholder.UseTheme(resources);

        var brush = Assert.IsType<VisualBrush>(resources[LoadingPlaceholder.GridBackgroundKey]);
        var card  = Assert.IsType<Border>(brush.Visual);
        var text  = Assert.IsType<TextBlock>(card.Child);
        Assert.Same(resources["BgCard"], card.Background);
        Assert.Same(resources["TextMuted"], text.Foreground);
        Assert.Equal("Loading…", text.Text);

        // Again after a switch: a new brush, which the grids' DynamicResource follows.
        LoadingPlaceholder.UseTheme(resources);
        Assert.NotSame(brush, resources[LoadingPlaceholder.GridBackgroundKey]);
    });

    // ── Helpers ──────────────────────────────────────────────────────────

    private const double Width = 1088;   // the content area beside the pinned sidebar, at the default window width

    private static void Layout(FrameworkElement host)
    {
        host.Width = Width;
        host.Measure(new Size(Width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, Width, host.DesiredSize.Height));
        host.UpdateLayout();
    }

    private static void Render(FrameworkElement host, string theme, string state)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "loading-bar");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{state}-{theme}.png"));
        encoder.Save(file);
    }

    // A host in a theme, on an STA thread.
    private static void OnSta(string theme, Action<Border> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = (ResourceDictionary)Application.LoadComponent(
                    new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative));
                test(new Border { Resources = resources, Background = (Brush)resources["BgDeep"] });
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
