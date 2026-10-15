using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The pages whose controls move to the top bar (#140): the three time range pickers and Live
/// Metrics' sample interval come out of the page with their handlers, and the page says what it is.
/// </summary>
public sealed class PageBarToolsTests
{
    private static readonly ConcurrentQueue<(string, string)> Calls = new();

    private static Engine.Repositories.IWaitStatsRepository Repo() =>
        MonitoringManagerTests.Counting("prod", Calls);

    public static TheoryData<string> TrendPages => new() { "LiveMetrics", "Waits", "Perfmon" };

    private static Page Create(string tag) => tag switch
    {
        "LiveMetrics" => new LiveMetricsDashboardPage(Repo()),
        "Waits"       => Wave4Pages.Waits(Repo()),
        "Perfmon"     => Wave4Pages.Perfmon(Repo()),
        _             => throw new ArgumentOutOfRangeException(nameof(tag)),
    };

    [Theory, MemberData(nameof(TrendPages))]
    public void TheRangePicker_MovesToTheBar_AndThePageIsLiveUntilAPastRangeIsPicked(string tag) => UiThread.Run(() =>
    {
        var page    = Create(tag);
        var context = (IPageContext)page;
        Assert.Equal(PageKind.Live, context.Kind);
        // Waits and Perfmon pause with their own Start and Stop; Live Metrics pauses its charts
        // while monitored, and isn't here.
        Assert.Equal(tag != "LiveMetrics", context.CanPause);

        var tools  = context.TakeBarTools();
        Assert.NotNull(tools);
        var picker = tools as TimeRangePicker ?? UiThread.Find<TimeRangePicker>(Laid(tools)).Single();

        // Out of the page, with nothing of it left behind.
        Assert.Empty(UiThread.Find<TimeRangePicker>(Laid(page)));
        Assert.Same(picker, PageCatalog.Types[tag].GetField("RangePicker",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page));
    });

    [Fact]
    public void LiveMetrics_HandsTheBarItsSampleInterval_AndReadsAsPausedWhileNotMonitored() => UiThread.Run(() =>
    {
        var page  = new LiveMetricsDashboardPage(Repo());
        var tools = Laid(page.TakeBarTools()!);

        var interval = UiThread.Find<ComboBox>(tools).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Sample every");
        Assert.StartsWith("every ", (string)interval.SelectedItem);
        Assert.Null(((IPageContext)page).LiveInterval);   // the box says it

        // Nothing is collected until monitoring starts, so nothing moves: paused, and only the
        // page's own Start monitoring resumes it, not the bar.
        Assert.True(((IPageContext)page).IsPaused);
        Assert.False(((IPageContext)page).CanPause);
    });

    // An element laid out off screen, so its visual tree exists to search.
    private static FrameworkElement Laid(FrameworkElement element)
    {
        UiThread.Layout(element, 1200, 800);
        return element;
    }
}
