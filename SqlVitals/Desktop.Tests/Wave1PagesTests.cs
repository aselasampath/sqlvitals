using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The first wave of #180 (#185), Plan Cache Health with its Implicit Conversions tab, App
/// Connections and DB Storage, bound to their view models and rendered whole in both themes (to
/// %TEMP%\SqlVitalsTests\page-template-rollout\&lt;page&gt;-&lt;theme&gt;.png). Each fails on any
/// binding error. What their view models decide is tested without a page, in their own tests.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class Wave1PagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Settings() => new(_dir);

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static readonly DateTime Now = DateTime.Now;

    private static Color Colour(FrameworkElement element, string key) => ((SolidColorBrush)element.FindResource(key)).Color;

    // The value of each card: the first text of each.
    private static List<TextBlock> Values(ItemsControl cards) =>
        Enumerable.Range(0, cards.Items.Count)
            .Select(i => Descendants<TextBlock>(cards.ItemContainerGenerator.ContainerFromIndex(i)).First())
            .ToList();

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                yield return found;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }

    // ── Plan Cache Health ────────────────────────────────────────────────

    private static FakeRepository PlanCacheRepo(bool erased = false) => new()
    {
        GetPlanCacheHealthAsync = () => Task.FromResult<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                          IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>((
            new PlanCacheHealthSummary(erased, 0, 18_420, 1, 0, erased ? "CRITICAL" : "WARNING"),
            [new(42, 1_204, "0x8F3A1B2C4D5E6F70", "SELECT * FROM dbo.Orders WHERE OrderId = 1001", "SalesDb", "WARNING")],
            [new("Key Lookup", 81_200, 9_410_000, 115.9, "SalesDb", "usp_GetOrderHistory", "SELECT o.OrderId FROM dbo.Orders o WHERE CustomerId = @c")],
            [new(61, 4, 10, 48_000, 4_800, "SalesDb", "SELECT * FROM dbo.Orders o JOIN dbo.OrderLines l ON l.OrderId = o.OrderId", "WARNING")],
            [new(64, 7, 92_000, 1_200, 76.7, "SalesDb", "SELECT COUNT(*) FROM dbo.OrderLines", "CRITICAL")],
            [new("PG_Orders", "OBJECT", "SalesDb", "usp_GetOrderHistory", "SELECT …", false)])),
        GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>(
            [new("SalesDb", "usp_FindCustomer", 48_120, 96, "SELECT CustomerId FROM dbo.Customers WHERE Email = @email", Now),
             new("HR", "usp_GetEmployee", 120, 64, "SELECT * FROM dbo.Employees WHERE BadgeNo = @badge", Now)]),
    };

    [Theory, MemberData(nameof(Themes))]
    public void PlanCacheHealth_Renders_WithItsCardsAndEveryTab(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var repo  = PlanCacheRepo(erased: true);
        var model = new PlanCacheHealthViewModel(repo, repo);
        var page  = new PlanCacheHealthPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, $"plan-cache-health-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.Tabs);
            Assert.Equal(Visibility.Visible, page.BannerCacheErased.Visibility);
            var values = Values(page.KpiStrip);
            Assert.Equal(["ERASED", $"{18_420:N0}", "0", "1", "CRITICAL"], values.Select(v => v.Text));
            Assert.Equal([Colour(page, "Danger"), Colour(page, "Accent"), Colour(page, "Success"), Colour(page, "Warning"), Colour(page, "Danger")],
                         values.Select(v => ((SolidColorBrush)v.Foreground).Color));
            Assert.Equal(model.FoldedSummary, page.SummarySection.FoldedSummary);

            // Each tab's grid shows its rows: the tabs are laid out as they are opened.
            foreach (var (tab, grid) in new (int, DataGrid)[] { (0, page.MultiPlanGrid), (1, page.KeyLookupGrid), (2, page.CardinalityGrid),
                                                               (3, page.SkewGrid), (4, page.PlanGuidesGrid), (5, page.ConvGrid) })
            {
                page.Tabs.SelectedIndex = tab;
                Assert.Equal(tab, model.SelectedTab);
                UiThread.Wait(Task.Delay(50));
                page.UpdateLayout();
                Assert.True(grid.Items.Count > 0, $"tab {tab} is empty");
            }
            Assert.Equal(Visibility.Collapsed, page.TxtConvProblem.Visibility);
            TemplateRolloutTests.Render(page, $"plan-cache-implicit-conversions-{theme}.png");
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    [Fact]
    public void PlanCacheHealth_ShowIt_SelectsTheMostUsedConversion_InView() => UiThread.Run(() =>
    {
        using var errors = BindingErrors.Watch();
        var repo  = PlanCacheRepo();
        // Nothing worse than the plans converting implicitly: the verdict is about them.
        repo.GetPlanCacheHealthAsync = () => Task.FromResult<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                               IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>((
            new PlanCacheHealthSummary(false, 0, 18_420, 0, 0, "OK"), [], [], [], [], []));
        var model = new PlanCacheHealthViewModel(repo, repo);
        var page  = new PlanCacheHealthPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            Assert.Equal(Visibility.Collapsed, page.BannerCacheErased.Visibility);
            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);   // Show it
            Assert.Same(page.TabImplicitConversions, page.Tabs.SelectedItem);
            Assert.Equal("usp_FindCustomer", ((ImplicitConversion)page.ConvGrid.SelectedItem).ObjectName);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    [Fact]
    public void PlanCacheHealth_TheConversionsUnread_TheTabSaysWhy() => UiThread.Run(() =>
    {
        using var errors = BindingErrors.Watch();
        var repo = PlanCacheRepo();
        repo.GetImplicitConversionsAsync = () => Task.FromException<IEnumerable<ImplicitConversion>>(new InvalidOperationException("Timeout expired."));
        var model = new PlanCacheHealthViewModel(repo, repo, NavMenu.ImplicitConversionsView);
        var page  = new PlanCacheHealthPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            Assert.Same(page.TabImplicitConversions, page.Tabs.SelectedItem);
            Assert.Equal(Visibility.Visible, page.TxtConvProblem.Visibility);
            Assert.Equal(model.ConversionsProblem, page.TxtConvProblem.Text);
            Assert.Empty(page.ConvGrid.Items);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    // ── App Connections ──────────────────────────────────────────────────

    private static ApplicationConnection App(string name, int connections, int active, long cpu) =>
        new(name, connections, active, cpu, connections * 96L, cpu * 40, cpu / 10, cpu / 20, Now.AddHours(-6), Now.AddSeconds(-active * 3), Now);

    [Theory, MemberData(nameof(Themes))]
    public void AppConnections_Renders_WithItsCardsChartAndGrid(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var model = new ApplicationConnectionsViewModel(new FakeRepository
        {
            GetApplicationConnectionsAsync = () => Task.FromResult<IEnumerable<ApplicationConnection>>(
                [App("OrderService", 120, 6, 1_840_000), App("BillingService", 64, 2, 912_000), App("Reporting (SSRS)", 22, 1, 2_310_000)]),
        });
        var page = new ApplicationConnectionsPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, $"app-connections-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.ConnectionsGrid, minDetail: 150);
            Assert.Equal(3, page.ConnectionsGrid.Items.Count);
            Assert.Equal(model.Summary, page.TxtSummary.Text);
            var values = Values(page.KpiPanel);
            Assert.Equal(["3", "206", "9", $"{5_062_000:N0}"], values.Select(v => v.Text));
            Assert.Equal([Colour(page, "Accent"), Colour(page, "Info"), Colour(page, "Success"), Colour(page, "Warning")],
                         values.Select(v => ((SolidColorBrush)v.Foreground).Color));
            Assert.Single(page.ConnectionsChart.Series);
            Assert.Equal(model.ChartSummary, page.ChartSection.FoldedSummary);

            // The row selected in the grid is the view model's, and stays through a refresh.
            page.ConnectionsGrid.SelectedItem = page.ConnectionsGrid.Items[1];
            UiThread.Wait(model.RefreshAsync());
            Assert.Equal("BillingService", ((ApplicationConnection)page.ConnectionsGrid.SelectedItem).ApplicationName);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    // ── DB Storage ───────────────────────────────────────────────────────

    private static DatabaseFile DbFile(string db, string type, string logical, decimal sizeMb, decimal maxMb = -1) =>
        new(db, "ONLINE", "FULL", 160, "Latin1_General_CI_AS", false, true, true, true, false, false, type, logical,
            $@"D:\MSSQL\{logical}.{(type == "LOG" ? "ldf" : "mdf")}", sizeMb, sizeMb * 0.8m, sizeMb * 0.2m, maxMb, type == "LOG" ? "512 MB" : "10%");

    private static FakeRepository StorageRepo() => new()
    {
        GetDatabaseStorageAsync = () => Task.FromResult<(IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>)>((
            [new("max degree of parallelism", 8, 0, 32767, "maximum degree of parallelism", true, true)],
            [DbFile("SalesDb", "ROWS", "SalesDb", 412_000), DbFile("SalesDb", "LOG", "SalesDb_log", 30_720, maxMb: 32_768), DbFile("HrDb", "ROWS", "HrDb", 2_048)],
            [new("tempdev", "ROWS", @"T:\tempdb.mdf", 8_192, 2_100, 6_092)],
            [new("dbo", "Orders", 48_000_000, 92_000, 31_000, 123_000, 122_400, 600, 6, "Clustered")])),
    };

    [Theory, MemberData(nameof(Themes))]
    public void DbStorage_Renders_WithItsSizesAndEveryTab(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var model = new DatabaseStorageViewModel(StorageRepo());
        var page  = new DatabaseStoragePage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, $"db-storage-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.Tabs);
            Assert.Equal((model.TotalSize, "8", "—"), (page.KpiTotalSize.Text, page.KpiMaxDop.Text, page.KpiCostThreshold.Text));
            Assert.Equal(model.SizesSummary, page.SizesSection.FoldedSummary);
            foreach (var (tab, grid) in new (TabItem, DataGrid)[] { (page.TabFiles, page.GridFiles), (page.TabTempDb, page.GridTempDb),
                                                                   (page.TabConfig, page.GridConfig), (page.TabTables, page.GridTables) })
            {
                page.Tabs.SelectedItem = tab;
                UiThread.Wait(Task.Delay(50));
                page.UpdateLayout();
                Assert.True(grid.Items.Count > 0, $"{tab.Header} is empty");
            }
            Assert.Equal((int)DatabaseStorageViewModel.Tab.Tables, model.SelectedTab);

            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);   // Show the file
            Assert.Same(page.TabFiles, page.Tabs.SelectedItem);
            Assert.Equal("SalesDb_log", ((DatabaseStorageViewModel.FileRow)page.GridFiles.SelectedItem).LogicalFileName);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    [Fact]
    public void DbStorage_ShowTheFile_FromAnotherTab_ScrollsALongListToIt() => UiThread.Run(() =>
    {
        // 80 files, the one near its maximum last: the files' tab isn't laid out when it is opened.
        var files = Enumerable.Range(1, 79).Select(i => DbFile($"Db{i:00}", "ROWS", $"Db{i:00}", 1_024))
                              .Append(DbFile("SalesDb", "LOG", "SalesDb_log", 30_720, maxMb: 32_768)).ToList();
        var model = new DatabaseStorageViewModel(new FakeRepository
        {
            GetDatabaseStorageAsync = () => Task.FromResult<(IEnumerable<ServerConfigParam>, IEnumerable<DatabaseFile>, IEnumerable<TempDbFileSummary>, IEnumerable<TableStorage>)>((
                [], files, [], [])),
        });
        var page = new DatabaseStoragePage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            page.Tabs.SelectedItem = page.TabTables;
            UiThread.Wait(Task.Delay(50));

            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);   // Show the file
            UiThread.Wait(Task.Delay(100));
            page.UpdateLayout();

            var row = model.SelectedFile!;
            Assert.Equal("SalesDb_log", row.LogicalFileName);
            Assert.NotNull(page.GridFiles.ItemContainerGenerator.ContainerFromItem(row));
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void DbStorage_OpenedFromALogSpaceAlert_ListsThatDatabasesFiles_UntilShowAll() => UiThread.Run(() =>
    {
        var model = new DatabaseStorageViewModel(StorageRepo());
        var page  = new DatabaseStoragePage(model, Settings());
        IFocusPage focus = page;
        focus.ShowFocus(new PageFocus(new FocusFilter(FocusKind.Database, Name: "SalesDb"), new FocusOrigin(FocusSource.Page, "SalesDb", "Overview")));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            Assert.Equal(2, page.GridFiles.Items.Count);
            Assert.Equal("showing the files of SalesDb only", focus.FocusNote);
            Assert.Equal("Show all 3 files", focus.ShowAllText);

            focus.ClearFocus();

            Assert.Equal(3, page.GridFiles.Items.Count);
            Assert.Null(focus.FocusNote);
        });
        model.OnNavigatedFrom();
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
