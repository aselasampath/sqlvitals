using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Query text in the grids (#232): a stored procedure's whole batch used to make its row taller than
/// the screen. Now each cell is one line without its comments, the whole text is a View away and
/// still what the filter, Copy All and Export see; and Resource Queries groups its tabs by object,
/// each group closed until opened.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class QueryTextGridsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // A module's whole batch, as Highest Logical Reads reads it: a banner, the header, a change history.
    private static readonly string Procedure = string.Join("\r\n",
        "-- =============================================",
        "-- Author:\t\tunicornsts",
        "-- =============================================",
        "CREATE PROCEDURE [wf].[GetEntitiesToWakeupWorkflowsByCaseno]",
        "\t@EntityId INT = -1",
        "AS",
        "BEGIN",
        "\t/* ---- Change History ----",
        "\t\tunicornadi\t2022-08-16\tchecked stateId",
        "\t---------------------------- */",
        "\tDECLARE @CaseList TABLE ( CaseNo INT )",
        string.Concat(Enumerable.Repeat("\tSELECT CaseNo FROM @CaseList\r\n", 60)),
        "END");

    private static ResourceIntensiveQuery Query(string? obj, long cpuUs, long reads, string text) =>
        new(text, "SalesDb", obj, 100, reads, reads / 100, cpuUs, cpuUs / 100, cpuUs * 2, cpuUs / 50, 0, 0,
            DateTime.Now.AddMinutes(-5), DateTime.Now.AddHours(-5), DateTime.Now, "<plan/>", reads / 100, obj is null ? "Ad-Hoc" : "Stored Proc");

    // Three statements of one chatty procedure, one large ad hoc query and the long procedure.
    private static readonly ResourceIntensiveQuery[] Queries =
    [
        Query(null,                       900_000, 5_000_000, "SELECT * FROM dbo.Stock WHERE Sku LIKE @p0"),
        Query("usp_Chatty",               100_000, 2_000_000, "SELECT 1 FROM dbo.A"),
        Query("usp_Chatty",               100_000, 2_000_000, "SELECT 2 FROM dbo.B"),
        Query("usp_Chatty",               100_000, 2_000_000, "SELECT 3 FROM dbo.C"),
        Query("GetEntitiesToWakeupWorkflowsByCaseno", 50_000, 1_000_000, Procedure),
    ];

    private static IWaitStatsRepository Repo() => new FakeRepository
    {
        GetResourceIntensiveQueriesAsync = _ => Task.FromResult<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
            (Queries.OrderByDescending(q => q.TotalLogicalReads), Queries.OrderByDescending(q => q.TotalCPUTime), Queries.OrderByDescending(q => q.TotalLogicalReads))),
        GetObjectQueryPlanAsync = _ => Task.FromResult<string?>("<object plan/>"),
    };

    private static DataGridTextColumn QueryColumn(DataGrid grid) =>
        grid.Columns.OfType<DataGridTextColumn>().Single(c => (string)c.Header == "Query Text");

    // ── The cell, its tooltip and the window ──────────────────────────────

    [Fact]
    public void TheCell_IsOneLine_FromTheCreate_AndTheTooltipKeepsTheLineBreaks()
    {
        var line = new QueryLine();

        var cell = (string)line.Convert(Procedure, typeof(string), null, CultureInfo.InvariantCulture);
        var tip  = (string)line.Convert(Procedure, typeof(object), "Tip", CultureInfo.InvariantCulture);

        Assert.StartsWith("CREATE PROCEDURE [wf].[GetEntitiesToWakeupWorkflowsByCaseno] @EntityId INT = -1 AS BEGIN DECLARE", cell);
        Assert.DoesNotContain('\n', cell);
        Assert.StartsWith("-- ====", tip);
        Assert.Contains("\r\nCREATE PROCEDURE", tip);
        Assert.Null(line.Convert(null, typeof(object), "Tip", CultureInfo.InvariantCulture));
        Assert.Equal("", line.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheWindow_ShowsTheWholeText_NamedByItsFirstWords_WithNoWarning()
    {
        var script = QueryTextWindow.Script(Procedure);

        Assert.Equal(Procedure, script.Text);
        Assert.StartsWith("Query text · CREATE PROCEDURE [wf].", script.Heading);
        Assert.EndsWith("…", script.Heading);
        Assert.Equal("", script.Notice);
        Assert.Equal("query.sql", script.FileName);
        Assert.Equal("To fix it · EXEC x", QueryTextWindow.Script("EXEC x", "To fix it").Heading);
    }

    [Fact]
    public void TheViewCommand_NeedsText() => UiThread.Run(() =>
    {
        Assert.False(QueryTextWindow.Show.CanExecute(null));
        Assert.False(QueryTextWindow.Show.CanExecute("  "));
        Assert.True(QueryTextWindow.Show.CanExecute("SELECT 1"));
    });

    // ── Resource Queries, every tab ───────────────────────────────────────

    [Fact]
    public void ResourceQueries_EachTab_KeepsTheProcedureOnOneLine_AndTheWholeTextForCopyAndFilter() => UiThread.Run(() =>
    {
        var page = Wave2Pages.ResourceQueries(Repo(), folds: new ConnectionSettingsService(_dir));
        TemplateRolloutTests.Hosted(page, page.ViewModel.OnNavigatedToAsync, "query-text-232.png", () =>
        {
            foreach (var (tab, grid) in new (int, DataGrid)[] { (0, page.ReadsGrid), (1, page.CpuGrid), (2, page.HighestReadsGrid) })
            {
                page.Tabs.SelectedIndex = tab;
                UiThread.Wait(Task.Delay(50));
                page.UpdateLayout();

                var procedure = Queries[^1];
                grid.ScrollIntoView(procedure);
                page.UpdateLayout();
                var row  = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(procedure);
                var cell = (TextBlock)QueryColumn(grid).GetCellContent(row)!;

                // One line, from the CREATE: the row is as high as the others.
                Assert.StartsWith("CREATE PROCEDURE", cell.Text);
                Assert.DoesNotContain('\n', cell.Text);
                var other = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(Queries[0]);
                Assert.True(row.ActualHeight <= other.ActualHeight + 1, $"tab {tab}: the row is {row.ActualHeight} high, the others {other.ActualHeight}");

                // Copy All and Export take the clipboard content: the whole text, line breaks and all.
                Assert.Equal(Procedure, QueryColumn(grid).OnCopyingCellClipboardContent(procedure));

                // The filter finds it by words only its comments hold.
                var filter = UiThread.Find<GridFilterBox>(page).Single(f => f.Target == grid);
                filter.Text = "unicornadi";
                Assert.Equal([procedure], grid.Items.Cast<object>());
                filter.Clear();

                // Its View button opens the whole text.
                var view = UiThread.Find<Button>(row).Single(b => (string)b.Content == "View");
                Assert.Same(QueryTextWindow.Show, view.Command);
                Assert.Equal(Procedure, view.CommandParameter);
                Assert.True(view.IsEnabled);
            }
        });
    });

    [Fact]
    public void ResourceQueries_GroupedByObject_StartsClosed_CostliestFirst_AndOpensOnAsk() => UiThread.Run(() =>
    {
        var page = Wave2Pages.ResourceQueries(Repo(), folds: new ConnectionSettingsService(_dir));
        TemplateRolloutTests.Hosted(page, page.ViewModel.OnNavigatedToAsync, null, () =>
        {
            var model = page.ViewModel;
            Assert.Null(page.ReadsGrid.Items.Groups);
            Assert.False(model.ExpandAllCommand.CanExecute(null));

            model.GroupByObject = true;
            UiThread.Wait(Task.Delay(50));
            page.UpdateLayout();

            // By reads, the chatty procedure's three statements (6 M) outrank the one large ad hoc query (5 M).
            Assert.Equal(["usp_Chatty", QueryObjectGroups.AdHoc, "GetEntitiesToWakeupWorkflowsByCaseno"], GroupNames(page.ReadsGrid));
            // By CPU, the ad hoc query (0.9 s) outranks the procedure (0.3 s).
            Assert.Equal([QueryObjectGroups.AdHoc, "usp_Chatty", "GetEntitiesToWakeupWorkflowsByCaseno"], GroupNames(page.CpuGrid));

            var chatty = Header(page.ReadsGrid, "usp_Chatty");
            Assert.False(chatty.IsExpanded);
            Assert.Equal("3 queries · " + 6_000_000.ToString("N0", CultureInfo.CurrentCulture) + " reads · CPU 300 ms", chatty.Summary);
            Assert.Equal("Stored Proc", chatty.Type);
            Assert.DoesNotContain(UiThread.Find<DataGridRow>(page.ReadsGrid), r => r.IsVisible);

            TemplateRolloutTests.Render(page, "query-text-232-grouped-closed.png");

            // Expand all opens every group on every tab; Collapse all closes them again.
            Assert.True(model.ExpandAllCommand.CanExecute(null));
            model.ExpandAllCommand.Execute(null);
            Assert.All(new[] { page.ReadsGrid, page.CpuGrid, page.HighestReadsGrid }, grid =>
                Assert.All(page.GroupingOf(grid).Headers.Values, h => Assert.True(h.IsExpanded)));
            page.UpdateLayout();
            TemplateRolloutTests.Render(page, "query-text-232-grouped-open.png");
            model.CollapseAllCommand.Execute(null);
            Assert.False(chatty.IsExpanded);

            // A refresh keeps a group open, and the filter's rows are what the header sums up.
            chatty.IsExpanded = true;
            UiThread.Wait(model.RefreshAsync());
            UiThread.Wait(Task.Delay(50));
            Assert.Same(chatty, Header(page.ReadsGrid, "usp_Chatty"));
            Assert.True(chatty.IsExpanded);
            var filter = UiThread.Find<GridFilterBox>(page).Single(f => f.Target == page.ReadsGrid);
            filter.Text = "dbo.A";
            Assert.StartsWith("1 query · ", chatty.Summary);
            filter.Clear();

            // Off again: one flat list.
            model.GroupByObject = false;
            Assert.Null(page.ReadsGrid.Items.Groups);
            Assert.Equal(Queries.Length, page.ReadsGrid.Items.Count);
        });
    });

    [Fact]
    public void ResourceQueries_ShowIt_OpensTheGroupOfTheRowItShows() => UiThread.Run(() =>
    {
        var page = Wave2Pages.ResourceQueries(Repo(), folds: new ConnectionSettingsService(_dir));
        TemplateRolloutTests.Hosted(page, page.ViewModel.OnNavigatedToAsync, null, () =>
        {
            page.ViewModel.GroupByObject = true;
            UiThread.Wait(Task.Delay(50));

            // The verdict's first action: the query that uses the most CPU, the ad hoc one.
            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);

            Assert.True(Header(page.CpuGrid, QueryObjectGroups.AdHoc).IsExpanded);
            Assert.False(Header(page.CpuGrid, "usp_Chatty").IsExpanded);
        });
    });

    private static List<string> GroupNames(DataGrid grid) =>
        grid.Items.Groups!.Cast<CollectionViewGroup>().Select(g => ((QueryGroupHeader)g.Name).Name).ToList();

    private static QueryGroupHeader Header(DataGrid grid, string name) =>
        grid.Items.Groups!.Cast<CollectionViewGroup>().Select(g => (QueryGroupHeader)g.Name).Single(h => h.Name == name);

    // ── The view model's part ─────────────────────────────────────────────

    [Fact]
    public void ExpandAndCollapseAll_AskOnlyWhileGrouped()
    {
        var model = new ResourceQueriesViewModel(Repo(), new FakeDialogService());
        var asked = new List<bool>();
        model.ExpandGroups += asked.Add;

        Assert.False(model.CollapseAllCommand.CanExecute(null));
        model.GroupByObject = true;
        model.ExpandAllCommand.Execute(null);
        model.CollapseAllCommand.Execute(null);

        Assert.Equal([true, false], asked);
    }

    // ── The other grids with query text ───────────────────────────────────

    [Fact]
    public void PlanCacheHealth_AndQueryRegressions_ShowOneLine_CopyTheWholeText_AndHaveView() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var settings    = new ConnectionSettingsService(_dir);
        var repo        = new FakeRepository();
        var planCache   = new Pages.PlanCacheHealthPage(new PlanCacheHealthViewModel(repo, repo), settings);
        var regressions = Wave3Pages.QueryRegressions(TemplateRolloutTests.RegressionsRepo(), null, settings);
        var grids = new[] { planCache.MultiPlanGrid, planCache.KeyLookupGrid, planCache.CardinalityGrid, planCache.SkewGrid,
                            planCache.PlanGuidesGrid, planCache.ConvGrid, regressions.RegressionsGrid };

        Assert.All(grids, grid =>
        {
            var column = QueryColumn(grid);
            Assert.IsType<QueryLine>(((Binding)column.Binding).Converter);
            Assert.Equal("QueryText", ((Binding)column.ClipboardContentBinding).Path.Path);
            var view = grid.Columns[grid.Columns.IndexOf(column) - 1];
            Assert.Equal("Text", view.Header);
            Assert.Null(view.ClipboardContentBinding);
        });
    });
}
