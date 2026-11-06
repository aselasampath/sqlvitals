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
/// on the grids' views, a filter that hides the selected table, and what Copy takes.
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
        var model = new TableStatisticsViewModel(repo, navigator ?? new FakeNavigator());
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
