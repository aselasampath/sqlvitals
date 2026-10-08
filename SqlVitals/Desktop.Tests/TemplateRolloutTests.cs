using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The page template on the Activity, Waits and Queries pages (#149): each opens with its verdict,
/// its charts and cards fold, a row's details go to the drawer, and a page that can't read says so.
/// Each page is rendered whole (to %TEMP%\SqlVitalsTests\page-template-rollout).
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class TemplateRolloutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    internal static readonly string RenderDir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "page-template-rollout");

    private ConnectionSettingsService Settings() => new(_dir);

    // ── Hosting and rendering ────────────────────────────────────────────

    /// <summary>
    /// Shows the page in an off-screen window, as the app does (a DataGrid that isn't laid out while
    /// the dispatcher runs shrinks its columns), runs the load, and renders it.
    /// </summary>
    internal static void Hosted(Page page, Func<Task> load, string? png, Action? whileShown = null, double width = 1400, double height = 860)
    {
        // The page gets its size itself: Windows caps a window at the screen, and a CI runner's
        // screen (1024 × 768) is shorter than this window, which squeezed the grids below the counts.
        page.Width  = width;
        page.Height = height;
        var window = new Window { Content = page, Width = width, Height = height, Left = -20_000, Top = -20_000,
                                  WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            UiThread.Wait(load());
            UiThread.Wait(Task.Delay(150));   // the window lays out the rows read, and the charts draw
            window.UpdateLayout();
            if (png is not null)
                Render(page, png);
            whileShown?.Invoke();
        }
        finally
        {
            window.Close();
        }
    }

    internal static void Render(FrameworkElement page, string name)
    {
        Directory.CreateDirectory(RenderDir);
        var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(RenderDir, name));
        encoder.Save(file);
    }

    internal static void Press(VerdictView verdict, int index) =>
        verdict.ActionButtons[index].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // The verdict on top, the detail filling what is left below it.
    internal static void AssertLaidOut(PageShell shell, FrameworkElement detail, double minDetail = 250)
    {
        Assert.Equal(Visibility.Visible, shell.VerdictView.Visibility);
        Assert.True(shell.VerdictView.ActualHeight is > 0 and < 200, $"verdict {shell.VerdictView.ActualHeight}");
        Assert.True(detail.ActualHeight > minDetail, $"detail {detail.ActualHeight}");
    }

    private const string PlanXml = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
          <StmtSimple StatementText="SELECT * FROM dbo.Orders WHERE CustomerId = @p0" StatementType="SELECT" StatementSubTreeCost="2">
            <QueryPlan>
              <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" EstimatedTotalSubtreeCost="2">
                <NestedLoops>
                  <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="10" EstimatedTotalSubtreeCost="0.5"><IndexScan /></RelOp>
                  <RelOp NodeId="2" PhysicalOp="Key Lookup" LogicalOp="Key Lookup" EstimateRows="1" EstimatedTotalSubtreeCost="1.5"><IndexScan /></RelOp>
                </NestedLoops>
              </RelOp>
            </QueryPlan>
          </StmtSimple>
        </Statements></Batch></BatchSequence></ShowPlanXML>
        """;

    // ── Active Waits ─────────────────────────────────────────────────────

    private static ActiveWait Wait(int spid, string? type, long ms, int blockedBy = 0, string category = "Locks", string login = "svc_orders") =>
        new(spid, blockedBy, type, ms, ms / 1000.0, null, type is null ? null : "KEY: 7:72057594043039744 (8194443284a0)", "suspended", "UPDATE",
            1_240, ms + 40, 18_400, 12, 3, 0, 1, login, "APP-WEB03", "OrderService", "SalesDb", category, blockedBy > 0 ? 1 : 0,
            "UPDATE dbo.Orders SET Status = @p0 WHERE OrderId = @p1", DateTime.Now, ms > 60_000 ? "Critical" : ms > 10_000 ? "High" : "Low");

    private static readonly ActiveWait[] Waits =
    [
        Wait(112, "LCK_M_X", 252_000, blockedBy: 87), Wait(115, "LCK_M_U", 231_000, blockedBy: 87), Wait(130, "LCK_M_S", 40_000, blockedBy: 112),
        Wait(141, "PAGEIOLATCH_SH", 32, category: "I/O", login: "reporting"), Wait(150, "CXPACKET", 1_200, category: "CPU", login: "etl"),
        Wait(151, null, 0, login: "etl"),
    ];

    [Fact]
    public void ActiveWaits_OpensWithTheBlockedRequests() => UiThread.Run(() =>
    {
        var page = new ActiveWaitsPage(new FakeRepository { GetActiveWaitsAsync = () => Task.FromResult<IEnumerable<ActiveWait>>(Waits) }, Settings());
        Hosted(page, page.RefreshAsync, "active-waits.png", () => AssertLaidOut(page.Shell, page.ActiveGrid));

        var verdict = page.Shell.Verdict!;
        Assert.Equal(VerdictLevel.Critical, verdict.Level);
        Assert.Equal("3 requests are blocked; session 112 the longest, for 4.2 min", verdict.Headline);
        Assert.Equal("Blocking in Processes", Assert.Single(verdict.Actions).Text);
        Assert.Equal(6, page.ActiveGrid.Items.Count);
        Assert.Equal(12, page.ActiveGrid.Columns.Count);
    });

    // ── Deadlocks ────────────────────────────────────────────────────────

    private static DeadlockReport Deadlock(DateTime utc, string victimCode, string otherCode, params string[] objects) =>
        new(utc,
            [new("p1", true, 55, "svc_orders", "APP-WEB03", "OrderService", "SalesDb", "read committed (2)", "X", "KEY: 7:7205", 4_210, 2, "user_transaction",
                 victimCode, "UPDATE dbo.Orders SET Status = 2 WHERE OrderId = @id", null, utc.ToLocalTime().AddSeconds(-5)),
             new("p2", false, 61, "svc_billing", "APP-WEB04", "BillingService", "SalesDb", "read committed (2)", "U", "KEY: 7:7206", 3_100, 1, "user_transaction",
                 otherCode, "UPDATE dbo.OrderLines SET Billed = 1 WHERE OrderId = @id", null, utc.ToLocalTime().AddSeconds(-6))],
            objects.Select(o => new DeadlockResource("Key", o, "PK_" + o.Split('.')[^1], "hobt 72057594043039744",
                [new DeadlockLock("p1", 55, "X")], [new DeadlockLock("p2", 61, "U")])).ToList(),
            "<deadlock><victim-list /></deadlock>");

    private static DeadlockHistory DeadlockHistoryOf(DateTime now) => new(
    [
        Deadlock(now.AddMinutes(-12), "dbo.usp_ShipOrder", "dbo.usp_BillOrder", "SalesDb.dbo.Orders", "SalesDb.dbo.OrderLines"),
        Deadlock(now.AddHours(-3), "dbo.usp_ShipOrder", "dbo.usp_BillOrder", "SalesDb.dbo.Orders", "SalesDb.dbo.OrderLines"),
        Deadlock(now.AddHours(-7), "dbo.usp_ShipOrder", "dbo.usp_BillOrder", "SalesDb.dbo.Orders", "SalesDb.dbo.OrderLines"),
        Deadlock(now.AddHours(-20), "dbo.usp_Restock", "dbo.usp_Report", "SalesDb.dbo.Stock"),
        Deadlock(now.AddDays(-4), "dbo.usp_Restock", "dbo.usp_Report", "SalesDb.dbo.Stock"),
    ], DeadlockSource.EventFile, 5, 0, @"D:\MSSQL\Log\system_health*.xel", null);

    [Fact]
    public void Deadlocks_OpensWithHowManyAndTheObjectFoughtOverMost() => UiThread.Run(() =>
    {
        var settings = Settings();
        var page = new DeadlocksPage(new FakeRepository { GetDeadlockHistoryAsync = () => Task.FromResult(DeadlockHistoryOf(DateTime.UtcNow)) }, settings);
        page.CmbRange.SelectedIndex = 1;   // Last 24 hours
        Hosted(page, page.RefreshAsync, "deadlocks.png", () => AssertLaidOut(page.Shell, page.Tabs));

        var verdict = page.Shell.Verdict!;
        Assert.Equal("4 deadlocks in the last 24 hours; SalesDb.dbo.OrderLines in 3 of them", verdict.Headline);
        Assert.StartsWith("The same deadlock came back 3 times", verdict.Why);
        Assert.Equal(4, page.DeadlocksGrid.Items.Count);

        // Open the newest of them: the newest of the recurring pattern, selected on the Deadlocks tab.
        page.DeadlocksGrid.SelectedItem = page.DeadlocksGrid.Items[^1];
        Press(page.Shell.VerdictView, 0);
        Assert.Equal(DateTime.UtcNow.AddMinutes(-12), ((DeadlockReport)page.DeadlocksGrid.SelectedItem).TimestampUtc, TimeSpan.FromMinutes(1));
        Press(page.Shell.VerdictView, 1);
        Assert.True(page.TabRecurring.IsSelected);

        // Where it was read from folds to one line, remembered.
        page.SourceSection.Toggle.IsChecked = false;
        Assert.StartsWith("system_health's event files · oldest held ", page.SourceSection.FoldedSummary);
        Assert.Equal(["source"], settings.Folded("Deadlocks"));
    });

    [Fact]
    public void Deadlocks_NoneInTheRange_OffersTheOlderOnes() => UiThread.Run(() =>
    {
        var now  = DateTime.UtcNow;
        var page = new DeadlocksPage(new FakeRepository { GetDeadlockHistoryAsync = () => Task.FromResult(
            new DeadlockHistory([Deadlock(now.AddDays(-3), "a", "b", "t")], DeadlockSource.RingBuffer, 1, 0, null, null)) });
        page.CmbRange.SelectedIndex = 0;   // Last hour
        UiThread.Wait(page.RefreshAsync());

        Assert.Equal((VerdictLevel.Ok, "No deadlocks in the last hour"), (page.Shell.Verdict!.Level, page.Shell.Verdict.Headline));
        Press(page.Shell.VerdictView, 0);
        Assert.Single(page.DeadlocksGrid.Items);
        Assert.Equal("1 deadlock in what system_health holds, on t", page.Shell.Verdict.Headline);
    });

    // ── App Connections ──────────────────────────────────────────────────

    private static ApplicationConnection App(string name, int connections, int active, long cpu) =>
        new(name, connections, active, cpu, connections * 96L, cpu * 40, cpu / 10, cpu / 20, DateTime.Now.AddHours(-6), DateTime.Now.AddSeconds(-active * 3), DateTime.Now);

    [Fact]
    public void AppConnections_OpensWithWhoHoldsThem_AndItsCountsAndChartFold() => UiThread.Run(() =>
    {
        var settings = Settings();
        var page = new ApplicationConnectionsPage(new FakeRepository { GetApplicationConnectionsAsync = () => Task.FromResult<IEnumerable<ApplicationConnection>>(
        [
            App("OrderService", 120, 6, 1_840_000), App("BillingService", 64, 2, 912_000), App("Reporting (SSRS)", 22, 1, 2_310_000),
            App("Microsoft SQL Server Management Studio", 4, 1, 12_000), App("SQLAgent - TSQL JobStep", 3, 0, 4_000),
        ]) }, settings);
        Hosted(page, page.RefreshAsync, "app-connections.png", () => AssertLaidOut(page.Shell, page.ConnectionsGrid, minDetail: 150));

        Assert.Equal("5 applications hold 213 connections; OrderService the most, 120", page.Shell.Verdict!.Headline);
        Assert.Equal(4, page.KpiPanel.Children.Count);
        Assert.Equal(10, page.ConnectionsGrid.Columns.Count);

        page.SummarySection.Toggle.IsChecked = false;
        page.ChartSection.Toggle.IsChecked   = false;
        Assert.Equal("5 application(s) · 213 connections · 10 active requests", page.SummarySection.FoldedSummary);
        Assert.Equal("OrderService 120 · BillingService 64 · Reporting (SSRS) 22", page.ChartSection.FoldedSummary);
        Assert.Equal(["chart", "summary"], settings.Folded("AppConnections").Order());
    });

    // ── Top Waits ────────────────────────────────────────────────────────

    private static TopWaitType Top(int rank, string type, string category, long ms, double pct, double cumulative) =>
        new(rank, type, category, ms / 7, ms, ms / 50, ms / 9, ms - ms / 9, 11, ms / 1000.0, 7, pct, cumulative, DateTime.Now, "#000", "");

    private static readonly TopWaitType[] TopWaitTypes =
    [
        Top(1, "PAGEIOLATCH_SH", "I/O", 4_120_000, 38.2, 38.2), Top(2, "WRITELOG", "I/O", 1_510_000, 14.0, 52.2),
        Top(3, "SOS_SCHEDULER_YIELD", "CPU", 1_330_000, 12.3, 64.5), Top(4, "CXPACKET", "CPU", 1_010_000, 9.4, 73.9),
        Top(5, "LCK_M_X", "Locks", 820_000, 7.6, 81.5), Top(6, "ASYNC_NETWORK_IO", "Network", 640_000, 5.9, 87.4),
        Top(7, "RESOURCE_SEMAPHORE", "Memory", 410_000, 3.8, 91.2), Top(8, "PAGELATCH_EX", "Memory", 300_000, 2.8, 94.0),
        Top(9, "LCK_M_S", "Locks", 210_000, 1.9, 95.9), Top(10, "HADR_SYNC_COMMIT", "Other", 190_000, 1.8, 97.7),
    ];

    [Fact]
    public void TopWaits_OpensWithTheTopCategory_AndTheChartFolds() => UiThread.Run(() =>
    {
        var settings = Settings();
        var page = new TopWaitsPage(new FakeRepository { GetTopWaitTypesAsync = () => Task.FromResult<IEnumerable<TopWaitType>>(TopWaitTypes) }, settings);
        Hosted(page, page.RefreshAsync, "top-waits.png", () => AssertLaidOut(page.Shell, page.WaitsGrid, minDetail: 200));

        var verdict = page.Shell.Verdict!;
        Assert.Equal("Most waiting is on I/O: 53 % of the wait time, mostly PAGEIOLATCH_SH",
                     verdict.Headline.Replace(System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, "."));
        Assert.Equal(["File I/O", "Wait Trend"], verdict.Actions.Select(a => a.Text));
        Assert.Equal(10, page.WaitsGrid.Items.Count);

        page.ChartSection.Toggle.IsChecked = false;
        Assert.StartsWith("PAGEIOLATCH_SH 38", page.ChartSection.FoldedSummary);
        Assert.Equal(["chart"], settings.Folded("TopWaits"));

        // Opened again: still folded.
        var again = new TopWaitsPage(new FakeRepository(), settings);
        Assert.True(again.ChartSection.IsFolded);
    });

    // ── Wait Trend ───────────────────────────────────────────────────────

    private static WaitStatCumulative Cumulative(string type, string category, long ms, long tasks) =>
        new(type, category, 0, tasks, ms, ms / 10, ms / 8, ms - ms / 8, 1, 12, 10, ms / 1000.0, DateTime.Now, DateTime.Now.AddDays(-3), "#000", "");

    [Fact]
    public void WaitTrend_OpensWithTheWaitTypeThatWaitsMost_AndTheBarStartsAndStopsIt() => UiThread.Run(() =>
    {
        var reads = 0;
        var page = new WaitStatsTrendPage(new FakeRepository
        {
            GetCumulativeWaitsAsync = () =>
            {
                var n = reads++;
                // CXPACKET waits more and more; WRITELOG steadily.
                return Task.FromResult<IEnumerable<WaitStatCumulative>>(
                [
                    Cumulative("CXPACKET", "CPU", 1_000_000 + n * n * 40, 1000 + n * 10),
                    Cumulative("WRITELOG", "I/O", 800_000 + n * 60, 900 + n * 5),
                    Cumulative("PAGEIOLATCH_SH", "I/O", 600_000 + n * 30, 800 + n * 3),
                ]);
            },
        }, null, Settings());
        var context = (IPageContext)page;
        context.TakeBarTools();   // the range picker goes to the top bar, as in the app

        Assert.Null(page.Shell.Verdict);   // nothing read yet
        UiThread.Wait(page.RefreshAsync());
        Assert.Equal("Not trending yet", page.Shell.Verdict!.Headline);
        Assert.True(context.IsPaused);

        Hosted(page, async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                await page.RefreshAsync();
                await Task.Delay(120);
            }
        }, "wait-trend.png", () =>
        {
            AssertLaidOut(page.Shell, page.TrendChart, minDetail: 300);

            // The bar's Resume and Pause are what Start and Stop were.
            context.SetPaused(false);
            Assert.False(context.IsPaused);
            context.SetPaused(true);
            Assert.True(context.IsPaused);
            Assert.Equal("--", page.TxtCountdown.Text);
        });

        var verdict = page.Shell.Verdict!;
        Assert.StartsWith("CXPACKET waits the most: ", verdict.Headline);
        Assert.EndsWith(", and rising", verdict.Headline);
        Assert.Equal(VerdictLevel.Warning, verdict.Level);
    });

    [Fact]
    public void WaitTrend_ThatCantRead_SaysSoInTheVerdict() => UiThread.Run(() =>
    {
        var page = new WaitStatsTrendPage(new FakeRepository
        {
            GetCumulativeWaitsAsync = () => Task.FromException<IEnumerable<WaitStatCumulative>>(new InvalidOperationException("Timeout expired.")),
        });
        UiThread.Wait(page.RefreshAsync());   // the page reports the error in its status line, as before

        Assert.Equal("Couldn't read the wait statistics", page.Shell.Verdict!.Headline);
    });

    // ── Resource Queries ─────────────────────────────────────────────────

    private static ResourceIntensiveQuery Query(string? obj, string type, long runs, long cpuUs, long reads, string text) =>
        new(text, "SalesDb", obj, runs, reads, reads / runs, cpuUs, cpuUs / runs, cpuUs * 2, cpuUs * 2 / runs, reads / 40, reads / 90,
            DateTime.Now.AddMinutes(-runs % 50), DateTime.Now.AddHours(-5), DateTime.Now, PlanXml, reads / runs, type);

    private static readonly ResourceIntensiveQuery[] Queries =
    [
        Query("dbo.usp_GetOrderHistory", "Stored Proc", 41_210, 812_000_000, 98_120_000, "SELECT o.OrderId, o.Placed, l.Sku FROM dbo.Orders o JOIN dbo.OrderLines l ON …"),
        Query(null, "Ad-Hoc", 9_120, 241_000_000, 410_220_000, "SELECT * FROM dbo.Stock WHERE Sku LIKE @p0"),
        Query("dbo.usp_PriceBasket", "Stored Proc", 120_400, 160_000_000, 12_040_000, "EXEC dbo.usp_PriceBasket @BasketId"),
        Query("dbo.fn_Discount", "Scalar Func", 1_900_000, 98_000_000, 3_800_000, "RETURN (SELECT Pct FROM dbo.Discounts …"),
        Query(null, "Ad-Hoc", 300, 21_000_000, 61_000_000, "SELECT COUNT(*) FROM dbo.AuditLog WHERE At > @p0"),
    ];

    private static IWaitStatsRepository QueriesRepo() => new FakeRepository
    {
        GetResourceIntensiveQueriesAsync = _ => Task.FromResult<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
            (Queries.OrderByDescending(q => q.TotalLogicalReads), Queries.OrderByDescending(q => q.TotalCPUTime), Queries.OrderByDescending(q => q.TotalLogicalReads))),
        GetObjectQueryPlanAsync = _ => Task.FromResult<string?>(PlanXml),
    };

    [Fact]
    public void ResourceQueries_OpensWithTheCostliestQuery_AndShowsItInTheDrawer() => UiThread.Run(() =>
    {
        var page   = new ResourceQueriesPage(QueriesRepo(), folds: Settings());
        var source = (IDetailsSource)page;
        var opened = new List<bool>();
        source.DetailsChanged += opened.Add;
        Hosted(page, page.RefreshAsync, "resource-queries.png", () =>
        {
            AssertLaidOut(page.Shell, page.Tabs);

            // Show it: By CPU, its top row selected, and the drawer opened on it.
            Press(page.Shell.VerdictView, 0);
            Assert.True(page.TabCpu.IsSelected);
            Assert.Equal("dbo.usp_GetOrderHistory", ((ResourceIntensiveQuery)page.CpuGrid.SelectedItem).ObjectName);
        });

        Assert.Equal("dbo.usp_GetOrderHistory uses the most CPU: 61 % of the top 5 queries'", page.Shell.Verdict!.Headline);
        Assert.Contains(true, opened);
        Assert.Equal("dbo.usp_GetOrderHistory", source.Details.Title);
        var view = Assert.IsType<RowDetailsView>(source.Details.Body);
        Assert.Contains("Executions: " + 41_210.ToString("N0", CultureInfo.CurrentCulture), view.RowTexts);
        Assert.StartsWith("SELECT o.OrderId", view.Text);
        Assert.Equal(["Execution plan"], view.PlanHeadings);

        // The plan, read when asked and drawn in the drawer.
        UiThread.Wait(view.ShowPlanAsync(0));
        Assert.True(view.PlanShown(0));

        // Every column, with View Plan, is still there.
        Assert.Equal(12, page.ReadsGrid.Columns.Count);
        Assert.Equal(12, page.CpuGrid.Columns.Count);
        Assert.Equal(12, page.HighestReadsGrid.Columns.Count);
    });

    // ── Query Regressions ────────────────────────────────────────────────

    private static IWaitStatsRepository RegressionsRepo() => new FakeRepository
    {
        GetQueryStoreRegressionStatsAsync = _ => Task.FromResult(new QueryStoreRegressionStats("SalesDb", "READ_WRITE", true,
        [
            new("4183", IsRecent: false, PlanId: 311, Executions: 1000, DurationUs: 1000 * 500_000.0, CpuUs: 1000 * 100_000.0),
            new("4183", IsRecent: true,  PlanId: 902, Executions: 200,  DurationUs: 200 * 2_050_000.0, CpuUs: 200 * 100_000.0),
            new("2210", IsRecent: false, PlanId: 455, Executions: 5000, DurationUs: 5000 * 20_000.0, CpuUs: 5000 * 15_000.0),
            new("2210", IsRecent: true,  PlanId: 455, Executions: 900,  DurationUs: 900 * 41_000.0, CpuUs: 900 * 33_000.0),
            new("3391", IsRecent: false, PlanId: 120, Executions: 800,  DurationUs: 800 * 9_000.0, CpuUs: 800 * 8_000.0),
            new("3391", IsRecent: true,  PlanId: 120, Executions: 700,  DurationUs: 700 * 9_100.0, CpuUs: 700 * 8_100.0),
        ])),
        GetQueryStoreTextsAsync = _ => Task.FromResult<IReadOnlyDictionary<long, string>>(new Dictionary<long, string>
        {
            [4183] = "SELECT o.OrderId, c.Name, SUM(l.Qty) FROM dbo.Orders o JOIN dbo.Customers c ON …",
            [2210] = "EXEC dbo.usp_GetCustomerHistory @CustomerId = @p0",
        }),
        GetQueryStorePlanAsync = _ => Task.FromResult<string?>(PlanXml),
    };

    [Fact]
    public void QueryRegressions_OpensWithTheWorstQuery_AndItsPlansInTheDrawer() => UiThread.Run(() =>
    {
        var settings = Settings();
        var page   = new QueryRegressionsPage(RegressionsRepo(), null, settings);
        var source = (IDetailsSource)page;
        Hosted(page, page.RefreshAsync, "query-regressions.png", () =>
        {
            AssertLaidOut(page.Shell, page.RegressionsGrid);
            Press(page.Shell.VerdictView, 0);   // Show its plans
            Assert.Equal("4183", ((QueryRegression)page.RegressionsGrid.SelectedItem).QueryKey);
        });

        var verdict = page.Shell.Verdict!;
        Assert.Equal("2 queries got slower; query 4183 the most: runs 4.1× slower since it got a new plan",
                     verdict.Headline.Replace(System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, "."));
        Assert.Equal(["Show its plans", "Open query 4183 in Query Store"], verdict.Actions.Select(a => a.Text));

        Assert.Equal("Query 4183", source.Details.Title);
        var view = Assert.IsType<RowDetailsView>(source.Details.Body);
        Assert.Equal(["Plan used most now (plan 902)", "Plan used most before (plan 311)"], view.PlanHeadings);
        UiThread.Wait(view.ShowPlanAsync(1));
        Assert.True(view.PlanShown(1));

        // What is compared folds to one line; the Check button stays beside the title.
        page.CriteriaSection.Toggle.IsChecked = false;
        Assert.StartsWith("Last 24 hours compared with the 7 days before · Query Store · more than 50", page.CriteriaSection.FoldedSummary);
        Assert.Equal(Visibility.Visible, page.BtnCheck.Visibility);
        Assert.Equal(15, page.RegressionsGrid.Columns.Count);
    });

    // ── Plan Cache Health ────────────────────────────────────────────────

    [Fact]
    public void PlanCacheHealth_OpensWithTheWorstCheck_AndTheSummaryFolds() => UiThread.Run(() =>
    {
        var settings = Settings();
        var page = new PlanCacheHealthPage(new FakeRepository
        {
            GetPlanCacheHealthAsync = () => Task.FromResult<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                                                       IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>((
                new PlanCacheHealthSummary(false, 4, 18_420, 0, 2, "WARNING"),
                [new(42, 1_204, "0x8F3A1B2C4D5E6F70", "SELECT * FROM dbo.Orders WHERE OrderId = 1001", "SalesDb", "WARNING"),
                 new(17, 380, "0x1122334455667788", "SELECT Name FROM dbo.Customers WHERE Email = 'a@b.c'", "SalesDb", "INFO")],
                [new("Key Lookup", 81_200, 9_410_000, 115.9, "SalesDb", "usp_GetOrderHistory", "SELECT o.OrderId, o.Placed FROM dbo.Orders o WHERE CustomerId = @c")],
                [], [], [])),
        }, settings);
        Hosted(page, page.RefreshAsync, "plan-cache-health.png", () =>
        {
            AssertLaidOut(page.Shell, page.Tabs);
            page.Tabs.SelectedIndex = 3;
            Press(page.Shell.VerdictView, 0);   // Multi-plan queries
            Assert.Equal(0, page.Tabs.SelectedIndex);
        });

        Assert.Equal((VerdictLevel.Warning, "4 queries have more than 5 cached plans each"), (page.Shell.Verdict!.Level, page.Shell.Verdict.Headline));
        Assert.Equal(5, page.KpiStrip.Children.Count);
        page.SummarySection.Toggle.IsChecked = false;
        Assert.Equal($"Plan cache OK · {18_420:N0} plans · 4 multi-plan queries · 0 plan guides · WARNING", page.SummarySection.FoldedSummary);
        Assert.Equal(["summary"], settings.Folded("PlanCacheHealth"));
    });

    // ── Implicit Conversions ─────────────────────────────────────────────

    [Fact]
    public void ImplicitConversions_OpensWithHowManyConvert_TheMostUsedFirst() => UiThread.Run(() =>
    {
        var page = new ImplicitConversionsPage(new FakeRepository
        {
            GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>(
            [
                new("SalesDb", "usp_FindCustomer", 48_120, 96, "SELECT CustomerId FROM dbo.Customers WHERE Email = @email -- @email NVARCHAR(256), Email VARCHAR(256)", DateTime.Now),
                new("SalesDb", "", 3_410, 40, "SELECT * FROM dbo.Orders WHERE OrderRef = @p0", DateTime.Now),
                new("HR", "usp_GetEmployee", 120, 64, "SELECT * FROM dbo.Employees WHERE BadgeNo = @badge", DateTime.Now),
            ]),
        }, Settings());
        Hosted(page, page.RefreshAsync, "implicit-conversions.png", () =>
        {
            AssertLaidOut(page.Shell, page.ConvGrid, minDetail: 200);
            Press(page.Shell.VerdictView, 0);
            Assert.Equal("usp_FindCustomer", ((ImplicitConversion)page.ConvGrid.SelectedItem).ObjectName);
        });

        Assert.Equal((VerdictLevel.Warning, $"3 cached plans convert implicitly; the most used ran {48_120:N0} times"),
                     (page.Shell.Verdict!.Level, page.Shell.Verdict.Headline));
        Assert.Equal(5, page.ConvGrid.Columns.Count);
    });

    // ── SP Trace ─────────────────────────────────────────────────────────

    private static SpTraceEvent Call(DateTime at, string proc, long ms, int spid, string app) =>
        new(at, "rpc_completed", proc, "P", "SalesDb", ms, ms / 3, ms * 41, 0, ms / 100, 12, spid, "svc_orders", app, "APP-WEB03",
            $"EXEC {proc} @CustomerId = 1042", Guid.NewGuid().ToString());

    [Fact]
    public void SpTrace_SaysWhatTheCaptureIsDoing_AndACallsDetailsGoToTheDrawer() => UiThread.Run(() =>
    {
        var now = DateTime.Now;
        var running = new TraceSessionStatus(true, TraceMode.ExtendedEvents, "SqlVitals_SpTrace", null, 5, 0, false);
        var page = new SpTracePage(new FakeRepository
        {
            StartTraceAsync         = _ => Task.FromResult(running),
            StopTraceAsync          = () => Task.FromResult(TraceSessionStatus.Stopped("SqlVitals_SpTrace")),
            GetTraceStatusAsync     = () => Task.FromResult(running),
            PollTraceEventsAsync    = () => Task.FromResult<IReadOnlyList<SpTraceEvent>>(
            [
                Call(now.AddSeconds(-9), "dbo.usp_GetOrderHistory", 1_840, 61, "OrderService"),
                Call(now.AddSeconds(-7), "dbo.usp_PriceBasket", 42, 64, "OrderService"),
                Call(now.AddSeconds(-4), "dbo.usp_BillOrder", 310, 72, "BillingService"),
                Call(now.AddSeconds(-2), "dbo.usp_PriceBasket", 38, 64, "OrderService"),
                Call(now.AddSeconds(-1), "dbo.usp_ShipOrder", 96, 77, "OrderService"),
            ]),
            PollProcedureStatsAsync = () => Task.FromResult<IReadOnlyList<SpAggregateRow>>(
            [
                new("SalesDb", "dbo", "usp_GetOrderHistory", 1, 1_840, 1_840, 1_840, 610, 75_440, 75_440, now),
                new("SalesDb", "dbo", "usp_PriceBasket", 2, 40, 42, 80, 13, 1_640, 3_280, now),
            ]),
            GetObjectQueryPlanAsync = _ => Task.FromResult<string?>(PlanXml),
        }, Settings());
        var source = (IDetailsSource)page;

        UiThread.Wait(page.RefreshAsync());
        Assert.Equal("Not capturing", page.Shell.Verdict!.Headline);

        Hosted(page, async () =>
        {
            Press(page.Shell.VerdictView, 0);   // Start Trace
            await Task.Delay(50);
            page.PrepareForShutdown();   // no timed poll during the test: one read, below
            await page.RefreshAsync();
        }, "sp-trace.png", () =>
        {
            AssertLaidOut(page.Shell, page.CallsGrid, minDetail: 300);
            Press(page.Shell.VerdictView, 1);   // Show the slowest
            Assert.Equal("dbo.usp_GetOrderHistory", ((SpTraceEvent)page.CallsGrid.SelectedItem).ObjectName);
        });

        Assert.Equal((VerdictLevel.Ok, "Capturing: 5 calls so far"), (page.Shell.Verdict!.Level, page.Shell.Verdict.Headline));
        Assert.Equal("dbo.usp_GetOrderHistory", source.Details.Title);
        var view = Assert.IsType<RowDetailsView>(source.Details.Body);
        Assert.StartsWith("EXEC dbo.usp_GetOrderHistory", view.Text);
        Assert.Contains("Duration: " + 1_840.ToString("N0", CultureInfo.CurrentCulture) + " ms", view.RowTexts);
        Assert.Equal(["Cached plan of the procedure"], view.PlanHeadings);
        Assert.Equal(12, page.CallsGrid.Columns.Count);
        Assert.Equal(9, page.AggregateGrid.Columns.Count);
    });

    // ── A page that can't read says so in its verdict ────────────────────

    public static TheoryData<string> Unreadable => new() { "ActiveWaits", "Deadlocks", "AppConnections", "TopWaits", "ResourceQueries",
                                                           "QueryRegressions", "PlanCacheHealth", "ImplicitConv", "SpTrace" };

    [Theory, MemberData(nameof(Unreadable))]
    public void APageThatCantRead_SaysSoInItsVerdict(string tag) => UiThread.Run(() =>
    {
        var error = new InvalidOperationException("Timeout expired.");
        var repo  = new FakeRepository
        {
            GetActiveWaitsAsync               = () => Task.FromException<IEnumerable<ActiveWait>>(error),
            GetDeadlockHistoryAsync           = () => Task.FromException<DeadlockHistory>(error),
            GetApplicationConnectionsAsync    = () => Task.FromException<IEnumerable<ApplicationConnection>>(error),
            GetTopWaitTypesAsync              = () => Task.FromException<IEnumerable<TopWaitType>>(error),
            GetResourceIntensiveQueriesAsync  = _ => Task.FromException<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(error),
            GetQueryStoreRegressionStatsAsync = _ => Task.FromException<QueryStoreRegressionStats>(error),
            GetPlanCacheHealthAsync           = () => Task.FromException<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                                                    IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>(error),
            GetImplicitConversionsAsync       = () => Task.FromException<IEnumerable<ImplicitConversion>>(error),
            PollProcedureStatsAsync           = () => Task.FromException<IReadOnlyList<SpAggregateRow>>(error),
        };
        ICodeBehindPage page = tag switch
        {
            "ActiveWaits"      => new ActiveWaitsPage(repo),
            "Deadlocks"        => new DeadlocksPage(repo),
            "AppConnections"   => new ApplicationConnectionsPage(repo),
            "TopWaits"         => new TopWaitsPage(repo),
            "ResourceQueries"  => new ResourceQueriesPage(repo),
            "QueryRegressions" => new QueryRegressionsPage(repo, null, Settings()),
            "PlanCacheHealth"  => new PlanCacheHealthPage(repo),
            "ImplicitConv"     => new ImplicitConversionsPage(repo),
            _                  => new SpTracePage(repo),
        };

        Assert.Throws<InvalidOperationException>(() => UiThread.Wait(page.RefreshAsync()));

        var verdict = ((PageShell)((Page)page).FindName("Shell")).Verdict!;
        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.StartsWith("Couldn't read ", verdict.Headline);
        Assert.Equal("Error: Timeout expired. Refresh to try again.", verdict.Why);
    });

    // ── Every page on the template, its state kept by the Navigator ──────

    [Fact]
    public void EveryPage_IsOnTheTemplate_WithItsKeyAndKind()
    {
        UiThread.Run(() =>
        {
            var repo = new FakeRepository();
            (string Key, Page Page, PageKind Kind)[] pages =
            [
                ("ActiveWaits", new ActiveWaitsPage(repo, Settings()), PageKind.Snapshot),
                ("Deadlocks", new DeadlocksPage(repo, Settings()), PageKind.Snapshot),
                ("AppConnections", new ApplicationConnectionsPage(repo, Settings()), PageKind.Snapshot),
                ("TopWaits", new TopWaitsPage(repo, Settings()), PageKind.Snapshot),
                ("WaitTrend", new WaitStatsTrendPage(repo, null, Settings()), PageKind.Live),
                ("ResourceQueries", new ResourceQueriesPage(repo, null, Settings()), PageKind.Snapshot),
                ("QueryRegressions", new QueryRegressionsPage(repo, null, Settings()), PageKind.History),
                ("PlanCacheHealth", new PlanCacheHealthPage(repo, Settings()), PageKind.Snapshot),
                ("ImplicitConv", new ImplicitConversionsPage(repo, Settings()), PageKind.Snapshot),
                ("SpTrace", new SpTracePage(repo, Settings()), PageKind.Live),
            ];
            foreach (var (key, page, kind) in pages)
            {
                Assert.Equal(page.GetType(), PageCatalog.Types[key]);
                Assert.Equal(kind, ((IPageContext)page).Kind);
                var shell = Assert.IsType<PageShell>(page.Content);
                Assert.Equal(key, shell.PageKey);
                Assert.NotNull(shell.Folds);
            }
        });
    }

    [Fact]
    public void ResourceQueries_OpenedAgain_GetsItsTabSortAndSelectionBack() => UiThread.Run(() =>
    {
        ResourceQueriesPage first = null!, again = null!;
        ViewState state = null!;
        first = new ResourceQueriesPage(QueriesRepo());
        Hosted(first, first.RefreshAsync, null, () =>
        {
            first.Tabs.SelectedItem = first.TabCpu;
            first.CpuGrid.SelectedItem = first.CpuGrid.Items[2];
            state = ViewState.Capture(first);
        });

        again = new ResourceQueriesPage(QueriesRepo());
        Hosted(again, async () =>
        {
            await again.RefreshAsync();
            state.Restore(again);
            // The state is put back at ContextIdle: wait for the dispatcher to get there, not a
            // fixed time, which a busy test run can outlast.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }, null);

        Assert.Same(again.TabCpu, again.Tabs.SelectedItem);
        Assert.Equal(Queries.OrderByDescending(q => q.TotalCPUTime).ElementAt(2).ObjectName, ((ResourceIntensiveQuery)again.CpuGrid.SelectedItem).ObjectName);
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
