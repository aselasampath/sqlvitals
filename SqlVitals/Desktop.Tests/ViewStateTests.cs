using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A page opened again comes back as it was left (#139): tab, grid sort, filter, selected row.
/// Pages are rebuilt, so the state is taken from one instance and put on a new one.
/// </summary>
public sealed class ViewStateTests
{
    public sealed record Row(string Name, int Count);

    [Fact]
    public void ARealPage_OpenedAgain_GetsItsTabFilterSortAndSelectionBack() => UiThread.Run(() =>
    {
        var repo = IndexRepository.Create();

        var first = Wave3Pages.IndexHealth(repo);
        UiThread.Layout(first, 1200, 900);
        UiThread.Wait(first.ViewModel.RefreshAsync());
        Idle();

        var missing = Grid(first, "MissingGrid");
        Filter(first, "MissingFilter").Text = "dbo";
        Sort(missing, nameof(MissingIndex.TableName), ListSortDirection.Ascending);
        missing.SelectedItem = missing.Items.Cast<MissingIndex>().Single(m => m.TableName == "Customers");
        TabsOf(first).SelectedIndex = 1;
        var state = ViewState.Capture(first);

        // A new read: the numbers moved, and the rows came back as Orders, Customers.
        IndexRepository.Bump();
        var again = Wave3Pages.IndexHealth(repo);
        UiThread.Layout(again, 1200, 900);
        UiThread.Wait(again.ViewModel.RefreshAsync());
        state.Restore(again);
        Idle();

        var grid = Grid(again, "MissingGrid");
        Assert.Equal(1, TabsOf(again).SelectedIndex);
        Assert.Equal("dbo", Filter(again, "MissingFilter").Text);
        Assert.Equal(new[] { new SortDescription(nameof(MissingIndex.TableName), ListSortDirection.Ascending) }, grid.Items.SortDescriptions);
        Assert.Equal(ListSortDirection.Ascending, grid.Columns.Single(c => c.SortMemberPath == nameof(MissingIndex.TableName)).SortDirection);
        Assert.Equal(["Customers", "Orders"], grid.Items.Cast<MissingIndex>().Select(m => m.TableName));
        Assert.Equal("Customers", ((MissingIndex)grid.SelectedItem).TableName);
    });

    [Fact]
    public void AGrid_FilledAfterTheRestore_GetsItsSort_AfterTheCodeThatFilledIt() => UiThread.Run(() =>
    {
        var (first, firstGrid, _) = TestPage();
        firstGrid.ItemsSource = Rows(("a", 1), ("b", 2));
        Sort(firstGrid, nameof(Row.Count), ListSortDirection.Descending);
        var state = ViewState.Capture(first);

        // A tab's grid is filled when the tab is opened, through DataGridRefresh, which puts back
        // the (empty) sort it found straight after setting the rows.
        var (again, grid, _) = TestPage();
        state.Restore(again);
        Idle();
        DataGridRefresh.SetItemsSource(grid, Rows(("a", 1), ("b", 2), ("c", 3)));
        Idle();

        Assert.Equal(["c", "b", "a"], grid.Items.Cast<Row>().Select(r => r.Name));
    });

    [Fact]
    public void ARow_WhoseTextChanged_IsNotSelected_RatherThanTheWrongOne() => UiThread.Run(() =>
    {
        var (first, firstGrid, _) = TestPage();
        firstGrid.ItemsSource = Rows(("a", 1), ("b", 2));
        firstGrid.SelectedIndex = 1;
        var state = ViewState.Capture(first);

        var (again, grid, _) = TestPage();
        grid.ItemsSource = Rows(("a", 1), ("x", 2));
        state.Restore(again);
        Idle();

        Assert.Null(grid.SelectedItem);
    });

    [Fact]
    public void AGrid_NotFilledThisTime_KeepsItsEarlierState_AndSoDoesItsFilter() => UiThread.Run(() =>
    {
        var (first, firstGrid, firstFilter) = TestPage();
        firstGrid.ItemsSource = Rows(("a", 1), ("b", 2));
        firstFilter.Text = "b";
        Sort(firstGrid, nameof(Row.Name), ListSortDirection.Descending);
        var earlier = ViewState.Capture(first);

        // Opened again, but left before the grid's tab was opened: nothing to read from it.
        var (again, _, _) = TestPage();
        var kept = ViewState.Capture(again, earlier);

        var (third, grid, filter) = TestPage();
        kept.Restore(third);
        grid.ItemsSource = Rows(("a", 1), ("b", 2), ("bb", 3));
        Idle();

        Assert.Equal("b", filter.Text);
        Assert.Equal(["bb", "b"], grid.Items.Cast<Row>().Select(r => r.Name));
    });

    [Fact]
    public void AFilterCleared_IsNotBroughtBack() => UiThread.Run(() =>
    {
        var (first, firstGrid, firstFilter) = TestPage();
        firstGrid.ItemsSource = Rows(("a", 1), ("b", 2));
        firstFilter.Text = "b";
        var earlier = ViewState.Capture(first);

        var (again, grid, filter) = TestPage();
        grid.ItemsSource = Rows(("a", 1), ("b", 2));
        earlier.Restore(again);
        Idle();
        filter.Text = "";
        var later = ViewState.Capture(again, earlier);

        var (third, thirdGrid, thirdFilter) = TestPage();
        thirdGrid.ItemsSource = Rows(("a", 1), ("b", 2));
        later.Restore(third);
        Idle();

        Assert.Equal("", thirdFilter.Text);
        Assert.Equal(2, thirdGrid.Items.Count);
    });

    [Fact]
    public void WhatTheUserDoes_WhileThePageLoads_IsKept() => UiThread.Run(() =>
    {
        var (first, firstGrid, firstFilter) = TestPage();
        firstGrid.ItemsSource = Rows(("a", 1), ("b", 2), ("ab", 3));
        firstFilter.Text = "b";
        Sort(firstGrid, nameof(Row.Name), ListSortDirection.Descending);
        firstGrid.SelectedIndex = 0;
        var state = ViewState.Capture(first);

        // Restored as the page opens; the user filters, sorts and selects before the rows arrive.
        var (again, grid, filter) = TestPage();
        state.Restore(again);
        filter.Text = "a";
        Sort(grid, nameof(Row.Count), ListSortDirection.Ascending);
        grid.ItemsSource = Rows(("a", 1), ("b", 2), ("ab", 3));
        grid.SelectedIndex = 1;
        Idle();

        Assert.Equal("a", filter.Text);
        Assert.Equal(["a", "ab"], grid.Items.Cast<Row>().Select(r => r.Name));
        Assert.Equal("ab", ((Row)grid.SelectedItem).Name);
    });

    [Fact]
    public void ThePage_IsScrolledBack_OnceItHasLoaded() => UiThread.Run(() =>
    {
        static (Page, ScrollViewer) Tall()
        {
            var scroll = new ScrollViewer { Content = new Border { Height = 3000 } };
            var page = new Page { Content = scroll };
            UiThread.Layout(page, 800, 600);
            return (page, scroll);
        }

        var (first, firstScroll) = Tall();
        firstScroll.ScrollToVerticalOffset(1200);
        UiThread.Layout(first, 800, 600);
        var state = ViewState.Capture(first);

        var (again, scroll) = Tall();
        state.Restore(again);
        Idle();
        Assert.Equal(0, scroll.VerticalOffset);

        state.RestoreScrolling(again);
        Idle();
        UiThread.Layout(again, 800, 600);
        Assert.Equal(1200, scroll.VerticalOffset);
    });

    // ── Helpers ───────────────────────────────────────────────────────────

    // A page with a filter box over a named grid, as the real pages have them.
    private static (Page Page, DataGrid Grid, GridFilterBox Filter) TestPage()
    {
        var grid = new DataGrid { Name = "RowsGrid", AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Name",  Binding = new Binding(nameof(Row.Name)),  SortMemberPath = nameof(Row.Name) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Count", Binding = new Binding(nameof(Row.Count)), SortMemberPath = nameof(Row.Count) });
        var filter = new GridFilterBox { Name = "RowsFilter", Target = grid };
        var page = new Page { Content = new StackPanel { Children = { filter, grid } } };
        UiThread.Layout(page, 800, 600);
        return (page, grid, filter);
    }

    private static List<Row> Rows(params (string Name, int Count)[] rows) => rows.Select(r => new Row(r.Name, r.Count)).ToList();

    private static void Sort(DataGrid grid, string property, ListSortDirection direction)
    {
        grid.Items.SortDescriptions.Clear();
        grid.Items.SortDescriptions.Add(new SortDescription(property, direction));
        foreach (var column in grid.Columns)
            column.SortDirection = column.SortMemberPath == property ? direction : null;
    }

    private static DataGrid Grid(Page page, string name) => (DataGrid)page.FindName(name);
    private static GridFilterBox Filter(Page page, string name) => (GridFilterBox)page.FindName(name);
    private static TabControl TabsOf(Page page) => LogicalDescendants(page).OfType<TabControl>().Single();

    private static IEnumerable<object> LogicalDescendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            yield return child;
            if (child is DependencyObject element)
                foreach (var deeper in LogicalDescendants(element))
                    yield return deeper;
        }
    }

    // Runs everything queued on the dispatcher, down to the idle priorities a restore uses.
    private static void Idle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Two missing indexes and no other indexes; other reads fail, as with no server.</summary>
    internal static class IndexRepository
    {
        private static int _reads;

        public static FakeRepository Create() => new()
        {
            Unreachable = true,
            GetIndexHealthAsync = () =>
            {
                var rows = new[] { Missing("Customers", 100 + _reads), Missing("Orders", 50 + _reads) };
                return Task.FromResult<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>(
                    (_reads % 2 == 0 ? rows : rows.Reverse(), []));
            },
            GetIndexUsagePatternsAsync = () => Task.FromResult<IEnumerable<IndexUsagePattern>>([]),
            GetIndexFragmentationAsync = (_, _) => Task.FromResult<IEnumerable<IndexFragmentation>>([]),
        };

        // The next read returns different numbers, and the rows in another order.
        public static void Bump() => _reads++;

        private static MissingIndex Missing(string table, long seeks) =>
            new("Sales", "dbo", table, "[Id]", null, null, 1, seeks, 0, null, null, 1, 50, seeks * 50,
                "INFO", $"CREATE INDEX IX_{table} ON dbo.{table} ([Id]);", DateTime.Now);
    }
}
