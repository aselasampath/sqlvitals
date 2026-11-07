using System.IO;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.TableStatistics;
using static SqlVitals.Desktop.Tests.TableStatisticsViewModelTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Table Statistics' page (#242), bound to its view model and rendered in both themes (to
/// %TEMP%\SqlVitalsTests\page-template-rollout\table-statistics-&lt;theme&gt;.png): its filters together
/// on the grids' views, a filter that hides the selected table, and what Copy takes. The Statistics
/// tab (#243) renders to table-statistics-details-&lt;theme&gt;.png and table-statistics-log-&lt;theme&gt;.png.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class TableStatisticsPageTests : IDisposable
{
    private const long Mb = 128;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FakeRepository Sample() => Repo(
        Table(1, "OrderLines", 9_400 * Mb, 1_100 * Mb, "dbo"),
        Table(2, "AuditTrail", 2_000 * Mb, 300 * Mb, "audit"),
        Table(3, "Orders", 384 * Mb, 245 * Mb, "dbo"),
        Table(4, "Payments", 74 * Mb, 62 * Mb, "sales"),
        new TableSize(5, "dbo", "Customers", null, null, null, true));

    private (TableStatisticsPage Page, TableStatisticsViewModel Model) Build(FakeRepository repo, FakeNavigator? navigator = null)
    {
        var model = new TableStatisticsViewModel(repo, navigator ?? new FakeNavigator(), new FakeDialogService());
        return (new TableStatisticsPage(model, new ConnectionSettingsService(_dir)), model);
    }

    private static List<string> Names(DataGrid grid) => grid.Items.Cast<TableSizeRow>().Select(r => r.TableName).ToList();

    private static string Count(GridFilterBox box) => box.TxtCount.Text;

    [Theory, MemberData(nameof(Themes))]
    public void Renders_WithATableSelected(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var (page, model) = Build(Sample());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            page.TablesGrid.SelectedItem = page.TablesGrid.Items.Cast<TableSizeRow>().Single(r => r.TableName == "Orders");
            UiThread.Wait(Task.Delay(150));
            page.UpdateLayout();
            TemplateRolloutTests.Render(page, $"table-statistics-{theme}.png");

            Assert.Equal("Selected dbo.Orders", page.TxtSelected.Text);
            Assert.Equal(3, page.IndexesGrid.Items.Count);
            Assert.True(page.CmbDatabase.IsVisible);
            // Readable in both themes: the box is the theme's, and shows the database.
            Assert.Equal("SalesDb", UiThread.Find<TextBlock>(page.CmbDatabase).Single(t => t.Text == "SalesDb").Text);
            Assert.Contains(UiThread.Find<Border>(page.CmbDatabase), b => ReferenceEquals(b.Background, page.FindResource("BgCard")));
            Assert.False(page.TxtConnectedDatabase.IsVisible);
            Assert.True(page.SpaceBarParts.Items.Count == 3);
            Assert.True(page.TablesGrid.ActualHeight > 60, $"tables {page.TablesGrid.ActualHeight}");
            Assert.True(page.IndexesGrid.ActualHeight > 60, $"indexes {page.IndexesGrid.ActualHeight}");

            // Appearing doesn't squeeze its columns: each fixed one has its width.
            Assert.All(page.IndexesGrid.Columns.Where(c => c.Width.IsAbsolute),
                       c => Assert.Equal(c.Width.Value, c.ActualWidth, 1));

            // The box opens, on a template of its own.
            page.CmbDatabase.IsDropDownOpen = true;
            UiThread.Wait(Task.Delay(50));
            Assert.Equal(2, page.CmbDatabase.Items.Count);
            Assert.NotNull(page.CmbDatabase.ItemContainerGenerator.ContainerFromIndex(1));
            page.CmbDatabase.IsDropDownOpen = false;
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    [Fact]
    public void OnAzureSqlDatabase_ThereIsNoDropdown() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var repo = Sample();
        repo.GetTableDatabasesAsync = () => Task.FromResult(new TableDatabases("AzureDb", ["AzureDb"], false));
        var (page, model) = Build(repo);

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            Assert.False(page.CmbDatabase.IsVisible);
            Assert.True(page.TxtConnectedDatabase.IsVisible);
            Assert.Equal("AzureDb", page.TxtConnectedDatabase.Text);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void TheSchemaAndTheBox_FilterTogether_AndTheCountSaysSo() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Sample());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            Assert.Equal(5, page.TablesGrid.Items.Count);
            Assert.Equal("", Count(page.TablesFilter));

            model.Schema.Items.Single(i => i.Value == "dbo").IsChecked = true;
            Assert.Equal(["OrderLines", "Orders", "Customers"], Names(page.TablesGrid));
            Assert.Equal("3 of 5 rows", Count(page.TablesFilter));

            page.TablesFilter.Text = "orders";
            Assert.Equal(["Orders"], Names(page.TablesGrid));
            Assert.Equal("1 of 5 rows", Count(page.TablesFilter));

            // Nothing left: the grid says why.
            page.TablesFilter.Text = "nothing";
            Assert.Empty(Names(page.TablesGrid));
            Assert.Equal("No tables match the filters.", model.NoTablesText);
            page.UpdateLayout();
            Assert.True(page.TxtNoTables.IsVisible);

            model.ClearTableFiltersCommand.Execute(null);
            Assert.Equal(5, page.TablesGrid.Items.Count);
            Assert.Equal("", page.TablesFilter.Text);
            Assert.Equal("", Count(page.TablesFilter));
            Assert.Null(model.NoTablesText);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void AFilterThatHidesTheSelectedTable_LeavesItsDetailsAlone_AndItIsSelectedAgainAfter() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var calls = 0;
        var repo = Sample();
        var indexes = repo.GetTableIndexesAsync!;
        repo.GetTableIndexesAsync = (database, id) =>
        {
            calls++;
            return indexes(database, id);
        };
        var (page, model) = Build(repo);

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            var orders = page.TablesGrid.Items.Cast<TableSizeRow>().Single(r => r.TableName == "Orders");
            page.TablesGrid.SelectedItem = orders;
            UiThread.Wait(Task.Delay(50));
            Assert.Equal(1, calls);

            model.Schema.Items.Single(i => i.Value == "audit").IsChecked = true;

            Assert.DoesNotContain("Orders", Names(page.TablesGrid));
            Assert.Null(page.TablesGrid.SelectedItem);
            Assert.Same(orders, model.SelectedTable);
            Assert.Equal(3, page.IndexesGrid.Items.Count);
            Assert.Equal(1, calls);

            model.ClearTableFiltersCommand.Execute(null);

            Assert.Same(orders, page.TablesGrid.SelectedItem);
            Assert.Equal(1, calls);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void TheIndexFilters_WorkTogether_AndTheBarDoesNotFollowThem() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Sample());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            page.TablesGrid.SelectedItem = page.TablesGrid.Items[0];
            UiThread.Wait(Task.Delay(50));
            var parts = page.SpaceBarParts.Items.Count;

            page.ChkFlaggedOnly.IsChecked = true;
            Assert.Equal(["IX_Legacy"], page.IndexesGrid.Items.Cast<TableIndexRow>().Select(i => i.Name));
            Assert.Equal("1 of 3 rows", Count(page.IndexesFilter));
            Assert.Equal(parts, page.SpaceBarParts.Items.Count);

            model.FlaggedOnly = false;
            model.IndexType.Items.Single(i => i.Value == "Clustered").IsChecked = true;
            Assert.Equal(["PK"], page.IndexesGrid.Items.Cast<TableIndexRow>().Select(i => i.Name));

            page.IndexesFilter.Text = "no such index";
            Assert.Equal("No indexes match the filters.", model.NoIndexesText);

            model.ClearIndexFiltersCommand.Execute(null);
            Assert.Equal(3, page.IndexesGrid.Items.Count);
            Assert.Null(model.NoIndexesText);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void Refresh_KeepsTheFiltersAndTheSelection() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Sample());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            model.Schema.Items.Single(i => i.Value == "dbo").IsChecked = true;
            page.TablesFilter.Text = "orders";
            page.TablesGrid.SelectedItem = page.TablesGrid.Items.Cast<TableSizeRow>().Single(r => r.TableName == "Orders");
            UiThread.Wait(Task.Delay(50));

            UiThread.Wait(model.RefreshAsync());

            Assert.Equal(["Orders"], Names(page.TablesGrid));
            Assert.Equal("1 of 5 rows", Count(page.TablesFilter));
            Assert.Equal("Orders", ((TableSizeRow)page.TablesGrid.SelectedItem).TableName);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void Copy_TakesOnlyTheRowsShown() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Sample());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            model.Schema.Items.Single(i => i.Value == "sales").IsChecked = true;

            // Copy, Copy All and export all take the grid's Items, and the columns' clipboard text.
            var row = Assert.Single(page.TablesGrid.Items.Cast<TableSizeRow>());
            var cells = page.TablesGrid.Columns.Where(c => c.ClipboardContentBinding is not null)
                            .Select(c => c.OnCopyingCellClipboardContent(row)?.ToString()).ToList();
            Assert.Contains("Payments", cells);
            Assert.Contains("136 MB", cells);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void SortingBySize_IsByThePages_NotTheWords() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Sample());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            var total = page.TablesGrid.Columns.Single(c => (string)c.Header == "Total size");
            Assert.Equal("TotalPages", total.SortMemberPath);

            page.TablesGrid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription(total.SortMemberPath, System.ComponentModel.ListSortDirection.Descending));

            Assert.Equal(["OrderLines", "AuditTrail", "Orders", "Payments", "Customers"], Names(page.TablesGrid));
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void ALargeTableList_StaysVirtualised() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var tables = Enumerable.Range(1, 5000).Select(i => Table(i, $"T{i:0000}", 5001 - i, 0)).ToArray();
        var (page, model) = Build(Repo(tables));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            Assert.Equal(5000, page.TablesGrid.Items.Count);
            var realised = UiThread.Find<DataGridRow>(page.TablesGrid).Count();
            Assert.InRange(realised, 1, 100);
        });
        model.OnNavigatedFrom();
    });

    // ── Statistics health (#243) ─────────────────────────────────────────

    private static FakeRepository Rated()
    {
        var repo = Sample();
        repo.GetTableStatisticsHealthAsync = (_, ids) => Task.FromResult(new TableHealthBatch(ids.Select(id => id switch
        {
            1 => new TableStatisticsHealth(id, StatisticsHealthState.Rated, StatisticsRating.Poor, 41.5, new DateTime(2026, 2, 3)),
            2 => new TableStatisticsHealth(id, StatisticsHealthState.Rated, StatisticsRating.Fair, 12, new DateTime(2026, 8, 1)),
            3 => new TableStatisticsHealth(id, StatisticsHealthState.Rated, StatisticsRating.Good, 0.5, new DateTime(2026, 11, 1)),
            4 => new TableStatisticsHealth(id, StatisticsHealthState.NoStatistics, null, null, null),
            _ => new TableStatisticsHealth(id, StatisticsHealthState.Locked, null, null, null),
        }).ToList(), []));
        return repo;
    }

    [Fact]
    public void TheHealthChips_FilterWithTheBox_AndTheCountSaysSo() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Rated());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            UiThread.WaitUntil(() => model.HealthProgress is null);

            model.Health.Items.Single(i => i.Value == "Poor").IsChecked = true;
            model.Health.Items.Single(i => i.Value == "Locked").IsChecked = true;
            Assert.Equal(["OrderLines", "Customers"], Names(page.TablesGrid));
            Assert.Equal("2 of 5 rows", Count(page.TablesFilter));

            page.TablesFilter.Text = "customers";
            Assert.Equal(["Customers"], Names(page.TablesGrid));

            model.ClearTableFiltersCommand.Execute(null);
            Assert.Equal(5, page.TablesGrid.Items.Count);
            Assert.Equal("", Count(page.TablesFilter));
            Assert.Null(model.Health.Values);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void WordsTyped_MatchTheHealth_AsItArrives() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var repo = Rated();
        var rated = repo.GetTableStatisticsHealthAsync!;
        var hold = new TaskCompletionSource();
        repo.GetTableStatisticsHealthAsync = async (database, ids) =>
        {
            await hold.Task;
            return await rated(database, ids);
        };
        var (page, model) = Build(repo);

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            page.TablesFilter.Text = "poor";
            Assert.Empty(Names(page.TablesGrid));

            hold.SetResult();
            UiThread.WaitUntil(() => model.HealthProgress is null);

            Assert.Equal(["OrderLines"], Names(page.TablesGrid));
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void SortingByHealth_PutsTheWorstFirst_AndFollowsWhatArrives() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var repo = Rated();
        var rated = repo.GetTableStatisticsHealthAsync!;
        var hold = new TaskCompletionSource();
        repo.GetTableStatisticsHealthAsync = async (database, ids) =>
        {
            await hold.Task;
            return await rated(database, ids);
        };
        var (page, model) = Build(repo);

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            var health = page.TablesGrid.Columns.Single(c => (string)c.Header == "Statistics health");
            Assert.Equal("HealthOrder", health.SortMemberPath);
            page.TablesGrid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription(health.SortMemberPath, System.ComponentModel.ListSortDirection.Ascending));
            page.TablesGrid.SelectedItem = page.TablesGrid.Items.Cast<TableSizeRow>().Single(r => r.TableName == "Orders");
            UiThread.Wait(Task.Delay(50));

            hold.SetResult();
            UiThread.WaitUntil(() => model.HealthProgress is null);

            Assert.Equal(["OrderLines", "AuditTrail", "Customers", "Orders", "Payments"], Names(page.TablesGrid));
            Assert.Equal("Orders", ((TableSizeRow)page.TablesGrid.SelectedItem).TableName);
        });
        model.OnNavigatedFrom();
    });

    // ── The Statistics tab (#243) ────────────────────────────────────────

    /// <summary>A hundred-odd distinct keys of a realistic shape, with the NULL step and the longest kinds of key.</summary>
    private static StatisticsDetails Large()
    {
        var steps = new List<HistogramStep> { new(1, null, 0, 180_000, 0, 0), new(2, "", 0, 900, 0, 0) };
        for (var i = 3; i <= 201; i++)
        {
            var eq = 200.0 + (i * 137 % 900);
            var range = 8_000.0 + (i * 311 % 5_000);
            if (i == 60) eq = 620_000;
            if (i == 140) range = 240_000;
            steps.Add(new HistogramStep(i, i == 90 ? "a key far too long for the axis, which the tooltip has whole" : $"cust-{i * 37:00000}", range, eq, 40, range / 40));
        }
        return new StatisticsDetails(new StatisticsProperties(new DateTime(2026, 8, 14, 2, 10, 0), 4_200_000, 84_000, 201, 910_000), steps,
            [new DensityEntry("CustomerKey", 0.00012, 12), new DensityEntry("CustomerKey, OrderDate", 0.0000031, 20), new DensityEntry("CustomerKey, OrderDate, OrderId", 0.00000024, 28)],
            160, false, []);
    }

    private static FakeRepository WithStatistics(StatisticsDetails details)
    {
        var repo = Rated();
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList(
            [TableStatisticsDetailsTests.Pk, TableStatisticsDetailsTests.Status, TableStatisticsDetailsTests.Region], false, []));
        repo.GetStatisticsDetailsAsync = (_, _, _) => Task.FromResult(details);
        return repo;
    }

    private static void OpenStatistics(TableStatisticsPage page, TableStatisticsViewModel model)
    {
        UiThread.WaitUntil(() => model.HealthProgress is null);
        page.TablesGrid.SelectedItem = page.TablesGrid.Items.Cast<TableSizeRow>().Single(r => r.TableName == "Orders");
        page.DetailTabs.SelectedItem = page.TabStatistics;
        UiThread.WaitUntil(() => model.Statistics.SelectedObject is not null && model.Statistics.LastUpdatedText != TableSpace.Unknown
                                 || model.Statistics.NoHistogramText is not null || model.Statistics.Problem is not null);
        UiThread.Wait(Task.Delay(150));
        page.UpdateLayout();
    }

    [Theory, MemberData(nameof(Themes))]
    public void TheStatisticsTab_Renders(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var (page, model) = Build(WithStatistics(Large()));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            OpenStatistics(page, model);
            var panel = page.StatisticsPanel;
            TemplateRolloutTests.Render(page, $"table-statistics-details-{theme}.png");

            Assert.Contains(UiThread.Find<TextBlock>(panel.CmbObject), t => t.Text == "PK_Orders");
            Assert.Equal(201, panel.HistogramGrid.Items.Count);
            Assert.Equal(3, panel.DensityGrid.Items.Count);
            Assert.True(panel.Chart.ActualHeight > 200 && panel.Chart.ActualWidth > 600, $"chart {panel.Chart.ActualWidth} x {panel.Chart.ActualHeight}");
            Assert.Equal(201, panel.Chart.Layout().Bars.Count);
            Assert.NotNull(panel.Chart.Layout().ReferenceY);
            Assert.True(panel.ObjectVerdict.IsVisible);
            Assert.NotEmpty(panel.FindingsList.Items);
            Assert.False(panel.TxtNoHistogram.IsVisible);

            // Its grids have a height of their own, and a real scrollbar for what's below it.
            Assert.Equal(300, panel.HistogramGrid.ActualHeight, 1);
            Assert.InRange(UiThread.Find<DataGridRow>(panel.HistogramGrid).Count(), 1, 60);

            // The names keep their underscores: no access keys in the headers or the choice of steps.
            var headers = UiThread.Find<TextBlock>(panel.HistogramGrid).Select(t => t.Text).ToList();
            foreach (var name in new[] { "RANGE_HI_KEY", "RANGE_ROWS", "EQ_ROWS", "DISTINCT_RANGE_ROWS", "AVG_RANGE_ROWS" })
                Assert.Contains(name, headers);
            panel.CmbSteps.SelectedItem = TableStatisticsDetailsViewModel.AboveAverageSteps;
            page.UpdateLayout();
            Assert.Contains(UiThread.Find<TextBlock>(panel.CmbSteps), t => t.Text == "Above-average AVG_RANGE_ROWS");
            panel.CmbSteps.SelectedItem = TableStatisticsDetailsViewModel.AllSteps;

            // The window is shorter than all this: the page scrolls, and the tables keep their height.
            Assert.True(page.PageScroller.ScrollableHeight > 300, $"scrolls {page.PageScroller.ScrollableHeight}");
            Assert.Equal(TableStatisticsPage.TablesHeightOnStatistics, page.TablesRow.ActualHeight, 1);
            Assert.False(page.DetailSplitter.IsVisible);
            Assert.Equal(0, UiThread.Find<ScrollViewer>(page.TablesGrid).First().ScrollableWidth, 1);   // the page's scrollbar costs the grid no scrolling of its own
            page.PageScroller.ScrollToVerticalOffset(page.PageScroller.ScrollableHeight);
            UiThread.Wait(Task.Delay(100));
            page.UpdateLayout();
            TemplateRolloutTests.Render(page, $"table-statistics-details-scrolled-{theme}.png");

            // Scrolled to, the grids' columns have their widths: appearing hasn't squeezed them.
            Assert.All(new[] { panel.HistogramGrid, panel.DensityGrid }.SelectMany(g => g.Columns).Where(c => c.Width.IsAbsolute),
                       c => Assert.Equal(c.Width.Value, c.ActualWidth, 1));
            Assert.True(panel.HistogramGrid.Columns[1].ActualWidth > 150);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    [Theory, MemberData(nameof(Themes))]
    public void OnTheLogScale_TheChartAndGridsRender(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var (page, model) = Build(WithStatistics(Large()));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            OpenStatistics(page, model);
            model.Statistics.LogScale = true;
            page.PageScroller.ScrollToVerticalOffset(770);
            UiThread.Wait(Task.Delay(100));
            page.UpdateLayout();
            TemplateRolloutTests.Render(page, $"table-statistics-log-{theme}.png");
            Assert.True(model.Statistics.LogScale);
        }, height: 1000);
        model.OnNavigatedFrom();
    });

    [Fact]
    public void BackOnSizes_TheRowsShareTheWindowAgain() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(WithStatistics(Large()));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            var (tables, details) = (page.TablesRow.Height, page.DetailsRow.Height);
            OpenStatistics(page, model);
            Assert.True(page.DetailsRow.Height.IsAuto);

            page.DetailTabs.SelectedItem = page.TabSizes;
            UiThread.Wait(Task.Delay(50));
            page.UpdateLayout();

            Assert.Equal(tables, page.TablesRow.Height);
            Assert.Equal(details, page.DetailsRow.Height);
            Assert.True(page.DetailSplitter.IsVisible);
            Assert.True(page.IndexesGrid.ActualHeight > 60);
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void TheChoiceOfStepsAndTheBox_FilterTogether_TheChartAndCopyAsTheyShould() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(WithStatistics(TableStatisticsDetailsTests.Sample()));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            OpenStatistics(page, model);
            var panel = page.StatisticsPanel;
            var whole = panel.Chart.Layout().Bars.Count;
            Assert.Equal(6, panel.HistogramGrid.Items.Count);

            panel.CmbSteps.SelectedItem = TableStatisticsDetailsViewModel.FarSteps;
            Assert.Equal([3, 6], panel.HistogramGrid.Items.Cast<HistogramStepRow>().Select(r => r.Number));
            Assert.Equal("2 of 6 rows", Count(panel.HistogramFilter));

            panel.HistogramFilter.Text = "void";
            Assert.Equal([6], panel.HistogramGrid.Items.Cast<HistogramStepRow>().Select(r => r.Number));
            Assert.Equal("1 of 6 rows", Count(panel.HistogramFilter));

            // Copy and export take the grid's Items, and the columns' text.
            var row = Assert.Single(panel.HistogramGrid.Items.Cast<HistogramStepRow>());
            Assert.Contains("Void", panel.HistogramGrid.Columns.Select(c => c.OnCopyingCellClipboardContent(row)?.ToString()));

            panel.HistogramFilter.Text = "nothing like it";
            Assert.Equal("No rows match the filters.", model.Statistics.NoStepsText);
            page.UpdateLayout();
            Assert.True(panel.TxtNoSteps.IsVisible);

            Assert.Equal(whole, panel.Chart.Layout().Bars.Count);
            Assert.Equal(6, model.Statistics.ChartSteps.Count);

            panel.HistogramFilter.Text = "";
            panel.CmbSteps.SelectedItem = TableStatisticsDetailsViewModel.AllSteps;
            Assert.Equal(6, panel.HistogramGrid.Items.Count);
            Assert.Equal("", Count(panel.HistogramFilter));

            panel.DensityFilter.Text = "orderid";
            Assert.Equal("1 of 2 rows", Count(panel.DensityFilter));
            panel.DensityFilter.Text = "nothing like it";
            Assert.Equal("No rows match the filter.", model.Statistics.NoDensityText);

            // Refresh keeps what's filtered, the object and the choice of steps.
            panel.DensityFilter.Text = "orderid";
            panel.CmbSteps.SelectedItem = TableStatisticsDetailsViewModel.FarSteps;
            UiThread.Wait(model.RefreshAsync());
            UiThread.WaitUntil(() => model.Statistics.ChartSteps.Count == 6);
            UiThread.Wait(Task.Delay(50));
            Assert.Equal("2 of 6 rows", Count(panel.HistogramFilter));
            Assert.Equal("1 of 2 rows", Count(panel.DensityFilter));
            Assert.Equal(TableStatisticsDetailsViewModel.FarSteps, panel.CmbSteps.SelectedItem);
        });
        model.OnNavigatedFrom();
    });

    [Theory, MemberData(nameof(Themes))]
    public void NoHistogramYet_ShowsTheMessage_NotAChart(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var (page, model) = Build(WithStatistics(new StatisticsDetails(null, [], [], 160, false, [])));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            OpenStatistics(page, model);
            var panel = page.StatisticsPanel;

            Assert.True(panel.TxtNoHistogram.IsVisible);
            Assert.Contains("No histogram yet", panel.TxtNoHistogram.Text);
            Assert.False(panel.Chart.IsVisible);
            Assert.False(panel.HistogramGrid.IsVisible);
            TemplateRolloutTests.Render(page, $"table-statistics-no-histogram-{theme}.png");
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    [Theory, MemberData(nameof(Themes))]
    public void APermissionMissing_ShowsTheGrant_AndTheSizesStay(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var repo = WithStatistics(Large());
        var failure = new TableReadFailure("the table's statistics", "The login lacks VIEW DEFINITION in SalesDb.", "USE [SalesDb]; GRANT VIEW DEFINITION TO [reader];");
        repo.GetTableStatisticsAsync = (_, _) => Task.FromResult(new TableStatisticsList([], false, [failure]));
        repo.GetTableStatisticsHealthAsync = (_, ids) => Task.FromResult(new TableHealthBatch(
            ids.Select(id => new TableStatisticsHealth(id, StatisticsHealthState.NotReadable, null, null, null)).ToList(), [failure]));
        var (page, model) = Build(repo);

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            OpenStatistics(page, model);

            Assert.True(page.StatisticsPanel.TxtProblem.IsVisible);
            Assert.Contains("GRANT VIEW DEFINITION", page.StatisticsPanel.TxtProblem.Text);
            Assert.True(page.TxtHealthProblem.IsVisible);
            Assert.Equal(5, page.TablesGrid.Items.Count);
            Assert.Equal(TableSpace.Unknown, ((TableSizeRow)page.TablesGrid.Items[0]).HealthText);
            UiThread.WaitUntil(() => model.Indexes.Count == 3);
            TemplateRolloutTests.Render(page, $"table-statistics-permission-{theme}.png");
        });
        model.OnNavigatedFrom();
    });

    [Fact]
    public void AHeaderTip_SaysItRatesFromTheHeader_AndOpeningCanRateLower() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var (page, model) = Build(Rated());

        var column = page.TablesGrid.Columns.Single(c => (string)c.Header == "Statistics health");
        var tip = column.HeaderStyle.Setters.OfType<Setter>().Single(s => s.Property == ToolTipService.ToolTipProperty).Value.ToString();

        Assert.Contains("header", tip);
        Assert.Contains("rate it lower", tip);
        model.OnNavigatedFrom();
    });

    [Fact]
    public void ShareBar_GivesEachPartItsShare_AndATinyOneASliver() => UiThread.Run(() =>
    {
        var panel = new ShareBarPanel();
        var parts = new (double Share, string Name)[] { (0.75, "a"), (0.2499, "b"), (0.0001, "c") };
        foreach (var (share, name) in parts)
            panel.Children.Add(new Border { DataContext = new SpaceBarPart(share, name, SpaceBarPart.Kinds.Index) });

        panel.Measure(new Size(1000, 12));
        panel.Arrange(new Rect(0, 0, 1000, 12));

        var widths = panel.Children.Cast<Border>().Select(b => b.RenderSize.Width).ToList();
        Assert.InRange(widths[0], 740, 751);
        Assert.InRange(widths[2], 2.9, 3.1);
        Assert.Equal(1000, widths.Sum(), 1);
    });
}
