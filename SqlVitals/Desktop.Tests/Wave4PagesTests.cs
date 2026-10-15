using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The fourth wave of #180 (#188), the chart pages Waits, Perfmon and SP Trace, bound to their view
/// models and rendered whole in both themes (to %TEMP%\SqlVitalsTests\page-template-rollout\
/// &lt;page&gt;-&lt;theme&gt;.png). Each fails on any binding error, and checks the view code each page
/// kept: the charts drawn from the view model, the controls bound to it, the trace button and the
/// mode badge in their colours, the drawer. What their view models decide is tested without a
/// page, in their own tests.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class Wave4PagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Settings() => new(_dir);

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    // Renders the page, its view model loading it, and fails on any binding error. The test puts
    // the theme on before it builds the page: its StaticResource brushes are looked up then.
    private static void Rendered(string theme, Page page, Func<Task> load, string name, Action whileShown)
    {
        using var errors = BindingErrors.Watch();
        TemplateRolloutTests.Hosted(page, load, $"{name}-{theme}.png", whileShown);
        errors.AssertNone();
    }

    private static Color Colour(FrameworkElement page, string key) => ((SolidColorBrush)page.FindResource(key)).Color;

    // ── Waits ────────────────────────────────────────────────────────────

    private static TopWaitType Top(int rank, string type, string category, long ms, double pct) =>
        new(rank, type, category, ms / 7, ms, ms / 50, ms / 9, ms - ms / 9, 11, ms / 1000.0, 7, pct, pct, DateTime.Now, "#000", "");

    private static WaitStatCumulative Totals(string type, long ms) =>
        new(type, "I/O", 0, 10, ms, 0, 0, ms, 1, 1, 1, ms / 1000.0, DateTime.Now, DateTime.Now.AddDays(-3), "#000", "");

    private static FakeRepository WaitsRepo()
    {
        var n = 0;
        return new FakeRepository
        {
            GetTopWaitTypesAsync    = () => Task.FromResult<IEnumerable<TopWaitType>>(
                [Top(1, "PAGEIOLATCH_SH", "I/O", 4_120_000, 38.2), Top(2, "WRITELOG", "I/O", 1_510_000, 14.0), Top(3, "CXPACKET", "CPU", 1_010_000, 9.4)]),
            GetCumulativeWaitsAsync = () =>
            {
                var i = n++;
                return Task.FromResult<IEnumerable<WaitStatCumulative>>([Totals("WRITELOG", 800_000 + i * 9_000), Totals("CXPACKET", 300_000 + i * i * 4_000)]);
            },
        };
    }

    [Theory, MemberData(nameof(Themes))]
    public void Waits_Renders_TheTrendAndTheTopWaits_FromItsViewModel(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave4Pages.Waits(WaitsRepo(), null, Settings());
        ((IPageContext)page).TakeBarTools();

        Rendered(theme, page, async () =>
        {
            await page.ViewModel.OnNavigatedToAsync();
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(60);
                await page.ViewModel.RefreshAsync();
            }
        }, "waits-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.TrendChart, minDetail: 180);
            Assert.Equal(3, page.WaitsGrid.Items.Count);
            Assert.Equal("Top wait types since the server started", page.TxtTopHeader.Text);
            Assert.Single(page.TopChart.Series);
            Assert.Equal(2, page.TrendChart.Series.Count());
            Assert.Equal(Visibility.Collapsed, page.TxtNoData.Visibility);
            Assert.Equal(2, page.WaitTypeList.Items.Count);
            Assert.Equal("2 / 2 selected", page.TxtSelCount.Text);
            Assert.Equal("Wait Time (ms/sec)", page.CmbMetric.SelectedItem);
            Assert.Equal("10s", page.CmbInterval.SelectedItem);
            Assert.Equal("--", page.TxtCountdown.Text);
            Assert.StartsWith("Updated ", page.TxtStatus.Text);

            // The list's buttons and boxes reach the view model, and the chart follows.
            TemplateRolloutTests.Click(page.BtnClearAll);
            Assert.Empty(page.TrendChart.Series);
            Assert.Equal(Visibility.Visible, page.TxtNoData.Visibility);
            Assert.Equal("0 / 2 selected", page.TxtSelCount.Text);
            TemplateRolloutTests.Click(page.BtnTopWaits);
            Assert.Equal(2, page.TrendChart.Series.Count());
            page.TxtSearch.Text = "write";
            Assert.Single(page.WaitTypeList.Items);

            // Resume and Pause in the top bar reach the view model (its countdown is in its own tests).
            ((IPageContext)page).SetPaused(false);
            Assert.False(page.ViewModel.IsPaused);
            ((IPageContext)page).SetPaused(true);
            Assert.True(page.ViewModel.IsPaused);
        });
    });

    // ── Perfmon ──────────────────────────────────────────────────────────

    private static PerfmonCounter Counter(string name, long value, bool rate = false) =>
        new("SQLServer:SQL Statistics", name, "", value, rate ? LiveMetricsRepository.PerfmonRateCounterType : 65792, DateTime.Now);

    private static FakeRepository PerfmonRepo()
    {
        var n = 0;
        return new FakeRepository
        {
            GetPerfmonCountersAsync = () =>
            {
                var i = n++;
                return Task.FromResult<IEnumerable<PerfmonCounter>>(
                [
                    Counter("Batch Requests/sec", 1_200_000 + i * 900, rate: true),
                    Counter("SQL Compilations/sec", 40_000 + i * 60, rate: true),
                    Counter("Memory Grants Pending", 3),
                ]);
            },
        };
    }

    [Theory, MemberData(nameof(Themes))]
    public void Perfmon_Renders_TheCountersTicked_FromItsViewModel(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave4Pages.Perfmon(PerfmonRepo(), null, Settings());
        ((IPageContext)page).TakeBarTools();

        Rendered(theme, page, async () =>
        {
            await page.ViewModel.OnNavigatedToAsync();
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(60);
                await page.ViewModel.RefreshAsync();
            }
        }, "perfmon-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.PerfmonChart);
            Assert.Equal("General Throughput", page.CmbGroup.SelectedItem);
            Assert.Equal(3, page.CounterList.Items.Count);
            Assert.Equal(2, page.PerfmonChart.Series.Count());
            Assert.Equal(Visibility.Collapsed, page.TxtNoData.Visibility);
            Assert.Equal("10s", page.CmbInterval.SelectedItem);
            Assert.StartsWith("Last refresh: ", page.TxtStatus.Text);

            page.CmbGroup.SelectedItem = "Memory Pressure";
            Assert.Single(page.PerfmonChart.Series);
            Assert.Equal("Memory Grants Pending is 3: queries wait for a memory grant", page.Shell.Verdict!.Headline);

            page.TxtSearch.Text = "batch";
            Assert.Single(page.CounterList.Items);
            page.TxtSearch.Text = "";
            TemplateRolloutTests.Click(UiThread.Find<Button>(page).Single(b => (string)b.Content == "Select All"));
            Assert.Equal(3, page.PerfmonChart.Series.Count());
            TemplateRolloutTests.Click(UiThread.Find<Button>(page).Single(b => (string)b.Content == "Clear All"));
            Assert.Empty(page.PerfmonChart.Series);
            Assert.Equal(Visibility.Visible, page.TxtNoData.Visibility);
        });
    });

    // ── SP Trace ─────────────────────────────────────────────────────────

    private static SpTraceEvent Call(int secondsAgo, string proc, long ms, int spid, string app) =>
        new(DateTime.Now.AddSeconds(-secondsAgo), "rpc_completed", proc, "P", "SalesDb", ms, ms / 3, ms * 41, 0, ms / 100, 12, spid, "svc_orders",
            app, "APP-WEB03", $"EXEC {proc} @CustomerId = 1042", Guid.NewGuid().ToString());

    [Theory, MemberData(nameof(Themes))]
    public void SpTrace_Renders_TheButtonAndBadgeInTheirColours_AndTheDrawerFollowsTheCall(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var running = new TraceSessionStatus(true, TraceMode.ExtendedEvents, "SqlVitals_SpTrace", null, 4, 0, false);
        var stops   = 0;
        var page = Wave4Pages.SpTrace(new FakeRepository
        {
            StartTraceAsync         = _ => Task.FromResult(running),
            StopTraceAsync          = () => { stops++; return Task.FromResult(TraceSessionStatus.Stopped("SqlVitals_SpTrace")); },
            GetTraceStatusAsync     = () => Task.FromResult(running),
            PollTraceEventsAsync    = () => Task.FromResult<IReadOnlyList<SpTraceEvent>>(
            [
                Call(9, "dbo.usp_GetOrderHistory", 1_840, 61, "OrderService"),
                Call(6, "dbo.usp_PriceBasket", 42, 64, "OrderService"),
                Call(3, "dbo.usp_BillOrder", 310, 72, "BillingService"),
                Call(1, "dbo.usp_ShipOrder", 96, 77, "OrderService"),
            ]),
            PollProcedureStatsAsync = () => Task.FromResult<IReadOnlyList<SpAggregateRow>>(
                [new("SalesDb", "dbo", "usp_GetOrderHistory", 1, 1_840, 1_840, 1_840, 610, 75_440, 75_440, DateTime.Now)]),
            GetObjectQueryPlanAsync = _ => Task.FromResult<string?>("<ShowPlanXML/>"),
        }, Settings());
        var source = (IDetailsSource)page;

        Rendered(theme, page, async () =>
        {
            await page.ViewModel.OnNavigatedToAsync();
            Assert.Equal("● Start Trace", page.BtnTrace.Content);
            Assert.Equal(Colour(page, "Success"), ((SolidColorBrush)page.BtnTrace.Foreground).Color);
            TemplateRolloutTests.Click(page.BtnTrace);   // Start Trace
            page.PrepareForShutdown();   // no timed poll during the test: one read, below
            await page.ViewModel.RefreshAsync();
        }, "sp-trace-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.CallsGrid, minDetail: 300);
            Assert.Equal(("■ Stop Trace", true), (page.BtnTrace.Content, page.BtnTrace.IsChecked));
            Assert.Equal(Colour(page, "Warning"), ((SolidColorBrush)page.BtnTrace.Foreground).Color);
            Assert.Equal("Extended Events", page.TxtModeBadge.Text);
            Assert.Equal(Colour(page, "Success"), ((SolidColorBrush)page.TxtModeBadge.Foreground).Color);
            Assert.StartsWith("Capturing live calls via session 'SqlVitals_SpTrace'", page.TxtTraceStatus.Text);
            Assert.Equal(4, page.CallsGrid.Items.Count);
            Assert.Equal("dbo.usp_ShipOrder", ((SpTraceEvent)page.CallsGrid.Items[0]).ObjectName);   // newest first
            Assert.Equal(Visibility.Collapsed, page.TxtNoCalls.Visibility);
            Assert.Single(page.AggregateGrid.Items);
            Assert.Equal(Visibility.Collapsed, page.TxtNoAggregates.Visibility);
            Assert.Equal("2s", page.CmbInterval.SelectedItem);

            // A filter box picked narrows the grid; Clear filters puts it back.
            page.CmbApp.SelectedItem = "BillingService";
            Assert.Single(page.CallsGrid.Items);
            Assert.Equal("Showing 1 of 4", page.TxtFilterCount.Text);
            TemplateRolloutTests.Click(page.BtnClearFilters);
            Assert.Equal(4, page.CallsGrid.Items.Count);
            Assert.Equal("(All)", page.CmbApp.SelectedItem);

            // A call selected goes to the drawer; the verdict's Show the slowest opens it there.
            page.CallsGrid.SelectedItem = page.CallsGrid.Items[1];
            Assert.Equal("dbo.usp_BillOrder", source.Details.Title);
            TemplateRolloutTests.Press(page.Shell.VerdictView, 1);   // Show the slowest
            Assert.Equal("dbo.usp_GetOrderHistory", ((SpTraceEvent)page.CallsGrid.SelectedItem).ObjectName);
            Assert.Equal("dbo.usp_GetOrderHistory", source.Details.Title);
            var view = Assert.IsType<RowDetailsView>(source.Details.Body);
            Assert.Equal(["Cached plan of the procedure"], view.PlanHeadings);

            // A poll that keeps the call selected leaves the drawer as it is (its plan, its scroll).
            var drawn = 0;
            source.DetailsChanged += _ => drawn++;
            UiThread.Wait(page.ViewModel.RefreshAsync());
            Assert.Equal(8, page.CallsGrid.Items.Count);
            Assert.Equal("dbo.usp_GetOrderHistory", ((SpTraceEvent)page.CallsGrid.SelectedItem).ObjectName);
            Assert.Equal(0, drawn);

            // Stop, then Clear: the button and the badge go back, the drawer says to select a call.
            TemplateRolloutTests.Click(page.BtnTrace);
            Assert.Equal(1, stops);
            Assert.Equal(("● Start Trace", false), (page.BtnTrace.Content, page.BtnTrace.IsChecked));
            Assert.Equal(Colour(page, "Success"), ((SolidColorBrush)page.BtnTrace.Foreground).Color);
            Assert.Equal("Idle", page.TxtModeBadge.Text);
            Assert.Equal(Colour(page, "Info"), ((SolidColorBrush)page.TxtModeBadge.Foreground).Color);
            TemplateRolloutTests.Click(page.BtnClear);
            Assert.Equal(Visibility.Visible, page.TxtNoCalls.Visibility);
            Assert.Equal("No calls captured yet.", page.TxtNoCalls.Text);
            Assert.Equal("Call details", source.Details.Title);
        });
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
