using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The fifth wave of #180 (#189), Home, Overview, Query Store and Live charts, bound to their view
/// models and rendered whole in both themes (to %TEMP%\SqlVitalsTests\page-template-rollout\
/// &lt;page&gt;-vm-&lt;theme&gt;.png). Each fails on any binding error, and checks the view code each
/// page kept: the colours its view model's levels take, a tile's menu, the charts drawn from the
/// view model, the columns that follow what was read, the cards and chips. What their view models
/// decide is tested without a page, in their own tests.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class Wave5PagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();

    private readonly ConnectionSettings _calm  = Conn("calm",  "Calm", background: true);
    private readonly ConnectionSettings _hot   = Conn("hot",   "Hot");
    private readonly ConnectionSettings _down  = Conn("down",  "Down", background: true);
    private readonly ConnectionSettings _quiet = Conn("quiet", "Quiet");

    private MonitoringManager?         _monitoring;
    private ConnectionSettingsService? _settings;

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static ConnectionSettings Conn(string server, string name, bool background = false) => new()
    {
        Server = server, Name = name, Database = "Sales", Authentication = SqlAuthMode.Windows, MonitorInBackground = background,
    };

    private IWaitStatsRepository Repository(ConnectionSettings conn) => conn.Server switch
    {
        "hot"  => MonitoringManagerTests.Hot(conn.Server, _calls),
        "calm" => OverviewPageTests.Calm(conn.Server, _calls),
        _      => MonitoringManagerTests.Counting(conn.Server, _calls),
    };

    // On the UI thread: the four saved, Hot active, monitoring started automatically, Hot alerting.
    private MonitoringManager Monitoring()
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in new[] { _calm, _hot, _down, _quiet })
            _settings.Upsert(conn);
        _settings.SetActive(_hot.Id);
        _settings.SetStartMonitoringAutomatically(true);
        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }), (conn, _) => Repository(conn));
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);
        for (var i = 0; i < 3; i++)
            UiThread.Wait(_monitoring.Get(_hot.Id)!.CollectNowAsync());
        UiThread.Wait(_monitoring.Get(_calm.Id)!.CollectNowAsync());
        MonitoringManagerTests.WaitForServerChecks(_monitoring.Get(_hot.Id)!);
        MonitoringManagerTests.WaitForServerChecks(_monitoring.Get(_calm.Id)!);
        try { UiThread.Wait(_monitoring.Get(_down.Id)!.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }
        return _monitoring;
    }

    private static T Connected<T>(T model, string tag) where T : PageViewModel
    {
        model.Connect(tag, new FakeShellStatus(), new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return model;
    }

    // Renders the page, its view model loading it, and fails on any binding error. The test puts
    // the theme on before it builds the page: its StaticResource brushes are looked up then.
    private static void Rendered(string theme, Page page, Func<Task> load, string name, Action whileShown)
    {
        using var errors = BindingErrors.Watch();
        TemplateRolloutTests.Hosted(page, load, $"{name}-{theme}.png", whileShown);
        errors.AssertNone();
    }

    private static Color Colour(FrameworkElement page, string key) => ((SolidColorBrush)page.FindResource(key)).Color;

    private static Color ColourOf(Brush brush) => ((SolidColorBrush)brush).Color;

    // ── Home ─────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void Home_Renders_TheTilesInTheirStatusColours_AndATilesMenu(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var monitoring = Monitoring();
        var page = new HomePage(Connected(new HomeViewModel(monitoring, _settings!, new DirectWatchStarter(monitoring), new FakeUiDispatcher()), "Home"));

        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "home-vm", () =>
        {
            Assert.Equal(["Down", "Hot", "Calm", "Quiet"], page.TilesShown.Select(t => t.Tile.Name));
            Assert.Equal(5, page.Tiles.Items.Count);   // and "+ Add a connection"
            Assert.Equal("4 connections · 3 monitored · sorted by most attention needed", page.TxtHeadline.Text);
            Assert.Equal(Visibility.Collapsed, page.TxtNote.Visibility);

            // A tile's colours are its status's.
            var hot  = page.TilesShown.Single(t => t.Tile.Name == "Hot");
            var line = UiThread.Find<TextBlock>(page).Single(t => t.DataContext == hot && t.Text == hot.Tile.Headline);
            Assert.Equal(ColourOf(Helpers.HealthBrushes.Red), ColourOf(line.Foreground));

            // ⋯ opens the tile's menu, beside it.
            var more = UiThread.Find<Button>(page).Single(b => b.DataContext == hot && b.Name == "MoreButton");
            TemplateRolloutTests.Click(more);
            Assert.Equal(["Open", "Stop monitoring", "Why this score…", "Edit connection…"],
                         page.TileMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header));
            Assert.Same(more, page.TileMenu.PlacementTarget);
            page.TileMenu.IsOpen = false;

            // "+ Add a connection" asks for Settings.
            var adds = 0;
            page.AddConnectionRequested += () => adds++;
            TemplateRolloutTests.Click(UiThread.Find<Button>(page).Single(b => Equals(b.Content, "+ Add a connection")));
            Assert.Equal(1, adds);
        });
    });

    // ── Overview ─────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void Overview_Renders_ItsLevelsInColour_AndTheChartsFromItsViewModel(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var monitoring = Monitoring();
        var model = Connected(new OverviewViewModel(_hot.Id, monitoring, _settings!, SinceLastLooked.Window(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow),
                                                    new DirectWatchStarter(monitoring), new FakeUiDispatcher(), new FakeClipboard()), "Overview");
        var page = new OverviewPage(model);
        ((IPageContext)page).TakeBarTools();

        Rendered(theme, page, async () =>
        {
            await model.OnNavigatedToAsync();
            await model.SinceLoaded;
        }, "overview-vm", () =>
        {
            Assert.Equal("CRITICAL", page.TxtStatus.Text);
            Assert.Equal(ColourOf(Helpers.HealthBrushes.Red), ColourOf(page.Dot.Fill));
            Assert.Equal(ColourOf(Helpers.HealthBrushes.Red), ColourOf(page.AlertsCard.BorderBrush));
            Assert.Equal(ColourOf(Helpers.HealthBrushes.Red), ColourOf(page.RunScore.Foreground));
            Assert.Equal(Visibility.Visible, page.ActiveChip.Visibility);
            Assert.Equal(Visibility.Collapsed, page.BtnMakeActive.Visibility);
            Assert.Equal(Visibility.Collapsed, page.TxtNoAlerts.Visibility);
            Assert.Equal(Visibility.Collapsed, page.OutageRow.Visibility);

            // The charts in the theme's colours, the legends' dots in the same.
            Assert.Equal(5, page.ChartWaits.Data!.Series.Count);
            Assert.Equal(Colour(page, "WaitIo"), ColourOf(page.ChartWaits.Data.Series[1].Brush));
            Assert.Single(page.ChartCpu.Data!.Markers!);
            Assert.Equal(11, page.LegendWaits.Inlines.Count);
            Assert.Equal("Live", page.TxtChartsTitle.Text);

            // A headline with no threshold keeps the text's colour.
            var waiting = UiThread.Find<TextBlock>(page.HeadlinesList).First(t => t.DataContext is OverviewViewModel.HeadlineTile { Label: "Waiting tasks" } && t.FontSize == 24);
            Assert.Equal(Colour(page, "TextPrimary"), ColourOf(waiting.Foreground));
        });
    });

    // ── Query Store ──────────────────────────────────────────────────────

    private static FakeRepository QueryStoreRepo()
    {
        var health = new QueryStoreHealth("SalesDb", "READ_WRITE", "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, "AUTO", "AUTO", null,
                                          200, 1482, 1630, 2, DateTime.UtcNow.AddDays(-20), DateTime.Now);
        return new FakeRepository
        {
            GetQueryStoreStatusAsync          = () => Task.FromResult(new QueryStoreStatus("SalesDb", health, QueryStoreFeatures.None)),
            GetQueryStoreTopQueriesAsync      = (_, _, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreQueryRow>(
            [
                new(4183, 0, null, "SELECT o.OrderId FROM dbo.Orders o", 2, 8_412, 0, 18_120, 18_120, 2_310, 41_000_000, null, 902, DateTime.UtcNow.AddMinutes(-3)),
                new(2210, 0, null, "EXEC dbo.usp_GetCustomerHistory @CustomerId = @p0", 1, 21_930, 0, 13_200, 13_200, 1_420, 22_000_000, null, 455, DateTime.UtcNow),
            ])),
            GetQueryStoreRegressionStatsAsync = _ => Task.FromResult(new QueryStoreRegressionStats("SalesDb", "READ_WRITE", true, [])),
            GetQueryStoreOverTimeAsync        = (_, slot, origin) => Task.FromResult(new QueryStoreRows<QueryStoreSlot>(
            [
                new(Engine.QueryStore.QueryStoreTimeSlots.SlotStartUtc(origin, slot, Engine.QueryStore.QueryStoreTimeSlots.SlotIndex(origin, slot, DateTime.UtcNow.AddHours(-3))),
                    new QueryStoreTotals(120, 9_000_000, 12_000_000, 4_000, 10, 30, 0, 500, 0, 0)),
            ])),
            GetQueryStoreHighVariationAsync   = (_, _, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreVariationRow>(
                [new(4183, 0, null, "SELECT 4183", 2, 400, 120, 2, 900, 310, 902)])),
        };
    }

    [Theory, MemberData(nameof(Themes))]
    public void QueryStore_Renders_TheCardsTheRowsAndTheirColumns_FromItsViewModel(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var settings = new ConnectionSettingsService(_dir);
        var page = Wave5Pages.QueryStore(QueryStoreRepo(), settings);
        Connected(page.ViewModel, "QueryStore");

        Rendered(theme, page, async () =>
        {
            await page.ViewModel.OnNavigatedToAsync();
            await page.ViewModel.VerdictLoaded;
        }, "query-store-vm", () =>
        {
            Assert.Equal("Query Store — SalesDb", page.Shell.Title);
            Assert.Equal(2, page.TopGrid.Items.Count);
            Assert.Equal(11, page.HealthPanel.Items.Count);   // no wait stats capture before 2017
            Assert.Equal("2 queries · Last 24 hours", page.TxtTopStatus.Text);
            Assert.Equal("Total CPU ms", page.TxtMetricHeader.Text);
            Assert.Equal(Visibility.Collapsed, page.ColTotalCpu.Visibility);
            Assert.Equal(Visibility.Collapsed, page.TopNotice.Visibility);
            Assert.Equal(Visibility.Collapsed, page.CustomPanel.Visibility);

            // A muted card in the muted colour; Forced plans in the warning one.
            var stale = UiThread.Find<TextBlock>(page.HealthPanel).First(t => t.DataContext is KpiCard { Label: "Stale after" } && t.Text == "30 days");
            Assert.Equal(Colour(page, "TextMuted"), ColourOf(stale.Foreground));

            // A row selected shows its query in the drawer.
            page.TopGrid.SelectedItem = page.TopGrid.Items[1];
            Assert.Equal("Query 2210", ((IDetailsSource)page).Details.Title);

            // Over Time draws a chart per metric ticked; High Variation's columns take its unit.
            page.Tabs.SelectedItem = page.TabOverTime;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Assert.Equal(page.ViewModel.ChartMetrics.Count(m => m.IsChecked), page.ChartsPanel.Children.Count);
            page.Tabs.SelectedItem = page.TabVariation;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Assert.Equal("Avg ms", page.ColVarAvg.Header);

            // Custom… opens the period's boxes.
            page.CmbPeriod.SelectedIndex = page.CmbPeriod.Items.Count - 1;
            Assert.Equal(Visibility.Visible, page.CustomPanel.Visibility);
        });
    });

    // ── Live charts ──────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void LiveMetrics_Renders_ACardPerChipOn_AndTheScoreInItsColour(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var monitoring = Monitoring();
        var repo = new FakeRepository { GetProcessesAsync = () => Task.FromResult<IEnumerable<ProcessNode>>([]) };
        var page = Wave5Pages.LiveMetrics(repo, monitoring, _hot.Id, monitoring.HistoryFor(_hot.Id));
        Connected(page.ViewModel, "LiveMetrics");
        page.TakeBarTools();

        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "live-metrics-vm", () =>
        {
            Assert.Equal(2, page.ChartGrid.Children.Count);   // waits and the process map
            Assert.Equal("84", page.RunHealthScore.Text);
            Assert.Equal(ColourOf(Helpers.HealthBrushes.Red), ColourOf(page.RunHealthScore.Foreground));
            Assert.Equal("■  Stop monitoring", page.BtnMonitor.Content);
            Assert.Equal(Visibility.Collapsed, page.MonitoringOffBanner.Visibility);
            Assert.Equal("every 10s", page.CmbInterval.SelectedItem);

            // A chip adds its card in its place; its ✕ takes it away and turns the chip off.
            page.ChipCpu.IsChecked = true;
            Assert.Equal(3, page.ChartGrid.Children.Count);
            var close = UiThread.Find<Button>(page.ChartGrid.Children[1]).Single(b => Equals(b.Content, "x"));
            TemplateRolloutTests.Click(close);
            Assert.Equal(2, page.ChartGrid.Children.Count);
            Assert.False(page.ChipCpu.IsChecked);

            // The waits card's boxes are the view model's categories.
            var boxes = UiThread.Find<CheckBox>(page.ChartGrid.Children[0]).ToList();
            Assert.Equal(6, boxes.Count);
            Assert.False(boxes[5].IsChecked);
            boxes[5].IsChecked = true;
            Assert.True(page.ViewModel.WaitsShown[5].IsShown);

            // The score opens its breakdown.
            TemplateRolloutTests.Click(page.BtnHealthScore);
            Assert.StartsWith("Prod · latest sample at", page.HealthScoreDetails.TxtSubtitle.Text);
        });
    });

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
