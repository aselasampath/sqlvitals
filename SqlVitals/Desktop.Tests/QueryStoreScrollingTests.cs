using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Query Store's lists that share a tab (#245): each must scroll inside the tab, at the app's usual
/// size and at its smallest, or its last rows can't be reached; and a window too short for the tabs
/// scrolls the page. Rendered to %TEMP%\SqlVitalsTests\query-store-scrolling.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class QueryStoreScrollingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static readonly string RenderDir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "query-store-scrolling");

    // About the page's size in the app at its default window and at its smallest; the narrow ones,
    // with the drawer pinned, wrap the Health cards to a third row above the settings checks.
    public static TheoryData<int, int> Sizes => new() { { 1060, 700 }, { 1060, 480 }, { 640, 700 }, { 640, 480 } };

    private static QueryStorePage Page(ConnectionSettingsService settings, int categories, int rows, bool settingsChecks = true, string waitCapture = "ON")
    {
        // Size-based cleanup OFF and capture mode ALL: two lines of settings checks.
        var health = new QueryStoreHealth("SalesDb", "READ_WRITE", "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, settingsChecks ? "OFF" : "AUTO", settingsChecks ? "ALL" : "AUTO", waitCapture,
                                          200, 1482, 1630, 1, DateTime.UtcNow.AddDays(-20), DateTime.Now);
        return Wave5Pages.QueryStore(new FakeRepository
        {
            GetQueryStoreStatusAsync          = () => Task.FromResult(new QueryStoreStatus("SalesDb", health, QueryStoreFeatures.None with { WaitStats = true, Hints = true })),
            GetQueryStoreRegressionStatsAsync = _ => Task.FromResult(new QueryStoreRegressionStats("SalesDb", "READ_WRITE", true, [])),
            GetQueryStoreTopQueriesAsync      = (_, _, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreQueryRow>([])),
            GetQueryStoreWaitCategoriesAsync  = _ => Task.FromResult(new QueryStoreRows<QueryStoreWaitCategory>(
                Enumerable.Range(1, categories).Select(i => new QueryStoreWaitCategory(i, $"Category {i}", 1000 * (categories - i + 1), 100.0 / categories)).ToList())),
            GetQueryStoreWaitQueriesAsync     = (_, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreWaitQuery>(
                Enumerable.Range(1, rows).Select(i => new QueryStoreWaitQuery(i, 0, null, $"SELECT {i}", 1000, 9_000, 10, 902)).ToList())),
            GetQueryStoreForcedPlansAsync     = _ => Task.FromResult(new QueryStoreForcedPlans(
                new QueryStoreRows<QueryStoreForcedPlan>(Enumerable.Range(1, rows).Select(i => new QueryStoreForcedPlan(i, 902, 0, null, $"SELECT {i}", "Manual", 0, null, DateTime.UtcNow, 10, 4.2)).ToList()),
                new QueryStoreRows<QueryStoreHint>(Enumerable.Range(1, rows).Select(i => new QueryStoreHint(i, "OPTION (RECOMPILE)", "User", 0, null, $"SELECT {i}")).ToList()))),
        }, settings);
    }

    private static void Shown(FrameworkElement page, int width, int height, Action<Window> test)
    {
        // In a window, as in the app, placed off the screen.
        var window = new Window { Content = page, Width = width, Height = height, Left = -20_000, Top = -20_000,
                                  WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try { test(window); }
        finally { window.Close(); }
    }

    private static void Settle(Window window)
    {
        UiThread.Wait(Task.Delay(100));
        window.UpdateLayout();
    }

    private static void Render(FrameworkElement page, string name)
    {
        Directory.CreateDirectory(RenderDir);
        var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(RenderDir, name));
        encoder.Save(file);
    }

    private static ScrollViewer ScrollerOf(Control list) => UiThread.Find<ScrollViewer>(list).First();

    // The list ends inside its tab's card, the card inside the page, and the list scrolls: what's
    // below is reached with its scrollbar.
    private static void AssertScrollsInside(QueryStorePage page, Control list, Border card, string what)
    {
        var bottom = list.TransformToAncestor(card).Transform(new Point(0, list.RenderSize.Height)).Y;
        Assert.True(bottom <= card.ActualHeight + 0.5, $"{what} ends at {bottom:N0}, below its card of {card.ActualHeight:N0}.");
        var cardBottom = card.TransformToAncestor(page.Shell).Transform(new Point(0, card.ActualHeight)).Y;
        Assert.True(cardBottom <= page.Shell.ActualHeight + 0.5, $"The card ends at {cardBottom:N0}, below the page of {page.Shell.ActualHeight:N0}.");

        var scroller = ScrollerOf(list);
        Assert.True(scroller.ScrollableHeight > 0, $"{what} doesn't scroll: extent {scroller.ExtentHeight:N0}, viewport {scroller.ViewportHeight:N0}.");
        Assert.Equal(Visibility.Visible, scroller.ComputedVerticalScrollBarVisibility);

        // Scrolled by rows: at least a whole row is readable at once, past the header and any scrollbar.
        Assert.True(scroller.ViewportHeight >= 1, $"{what} shows {scroller.ViewportHeight} rows.");

        scroller.ScrollToEnd();
        list.UpdateLayout();
        Assert.Equal(scroller.ScrollableHeight, scroller.VerticalOffset);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Waits_ReachesTheLastCategoryAndTheLastQuery(int width, int height) => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 23, rows: 40);
        Shown(page, width, height, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.Tabs.SelectedItem = page.TabWaits;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Settle(window);
            page.ViewModel.SelectedWaitBar = page.ViewModel.WaitBars[0];
            UiThread.Wait(page.ViewModel.WaitQueriesLoaded);
            Settle(window);
            Render(page, $"waits-{width}x{height}.png");

            var card = (Border)page.TabWaits.Content;
            AssertScrollsInside(page, page.WaitCategoriesList, card, "The category list");
            AssertScrollsInside(page, page.WaitQueriesGrid, card, "The query grid");
        });
    });

    // Capture off says so above the bars, with the statement to copy: the page makes room for it.
    [Theory]
    [MemberData(nameof(Sizes))]
    public void Waits_WithItsNotice_StillReachesTheLastQuery(int width, int height) => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 23, rows: 40, waitCapture: "OFF");
        Shown(page, width, height, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.Tabs.SelectedItem = page.TabWaits;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Settle(window);
            page.ViewModel.SelectedWaitBar = page.ViewModel.WaitBars[0];
            UiThread.Wait(page.ViewModel.WaitQueriesLoaded);
            Settle(window);
            Render(page, $"waits-notice-{width}x{height}.png");

            Assert.True(page.WaitsNotice.IsVisible);
            var card = (Border)page.TabWaits.Content;
            AssertScrollsInside(page, page.WaitQueriesGrid, card, "The query grid");

            // Another tab has no such notice: the room goes.
            page.Tabs.SelectedItem = page.TabTop;
            Settle(window);
            Assert.Equal(300, page.TabsRow.MinHeight);
        });
    });

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ForcedPlans_ReachesTheLastPlanAndTheLastHint(int width, int height) => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 2, rows: 40);
        Shown(page, width, height, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.Tabs.SelectedItem = page.TabForced;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Settle(window);
            Render(page, $"forced-{width}x{height}.png");

            var card = (Border)page.TabForced.Content;
            AssertScrollsInside(page, page.ForcedGrid, card, "The forced plans grid");
            AssertScrollsInside(page, page.HintsGrid, card, "The hints grid");
        });
    });

    [Fact]
    public void AFewCategories_TakeOnlyTheirHeight_AndTheGridTheRest() => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 2, rows: 40);
        Shown(page, 1060, 700, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.Tabs.SelectedItem = page.TabWaits;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Settle(window);

            Assert.Equal(0, ScrollerOf(page.WaitCategoriesList).ScrollableHeight);
            Assert.True(page.WaitCategoriesList.ActualHeight < page.WaitsLayout.ActualHeight * 0.3);
            Assert.True(page.WaitQueriesGrid.ActualHeight > page.WaitsLayout.ActualHeight * 0.3);
        });
    });

    [Fact]
    public void TheMouseWheel_ScrollsBothLists() => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 23, rows: 40);
        Shown(page, 1060, 700, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.Tabs.SelectedItem = page.TabWaits;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Settle(window);
            page.ViewModel.SelectedWaitBar = page.ViewModel.WaitBars[0];
            UiThread.Wait(page.ViewModel.WaitQueriesLoaded);
            Settle(window);

            AssertWheelScrolls(page.WaitCategoriesList, UiThread.Find<ListBoxItem>(page.WaitCategoriesList).First());
            AssertWheelScrolls(page.WaitQueriesGrid, UiThread.Find<DataGridRow>(page.WaitQueriesGrid).First());
        });
    });

    // From a row under the pointer, as the wheel does: the list's scroller is below the list itself.
    private static void Wheel(UIElement row, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        row.RaiseEvent(preview);
        if (!preview.Handled)
            row.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent });
        row.UpdateLayout();
    }

    private static void AssertWheelScrolls(Control list, UIElement row)
    {
        var scroller = ScrollerOf(list);
        Assert.Equal(0, scroller.VerticalOffset);
        Wheel(row, -120);
        Assert.True(scroller.VerticalOffset > 0);
    }

    [Fact]
    public void TheWheel_MovesTheGridThenThePage_AndBackTheSameWay() => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 23, rows: 40);
        Shown(page, 1060, 480, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            page.Tabs.SelectedItem = page.TabWaits;
            UiThread.Wait(page.ViewModel.TabLoaded);
            Settle(window);
            page.ViewModel.SelectedWaitBar = page.ViewModel.WaitBars[0];
            UiThread.Wait(page.ViewModel.WaitQueriesLoaded);
            Settle(window);

            var grid = ScrollerOf(page.WaitQueriesGrid);
            // A row on screen each time: the grid reuses a row scrolled away.
            DataGridRow Row() => UiThread.Find<DataGridRow>(page.WaitQueriesGrid).First(r => r.IsVisible);

            // The grid first, the page still.
            Wheel(Row(), -120);
            Assert.True(grid.VerticalOffset > 0);
            Assert.Equal(0, page.PageScroller.VerticalOffset);

            // At the grid's end, the page.
            grid.ScrollToEnd();
            page.WaitQueriesGrid.UpdateLayout();
            var end = grid.VerticalOffset;
            Wheel(Row(), -120);
            Assert.Equal(end, grid.VerticalOffset);
            Assert.True(page.PageScroller.VerticalOffset > 0);

            // Back up: the grid first again.
            var moved = page.PageScroller.VerticalOffset;
            Wheel(Row(), 120);
            Assert.True(grid.VerticalOffset < end);
            Assert.Equal(moved, page.PageScroller.VerticalOffset);
        });
    });

    // The page leaves the wheel to a combo box, which changes its value with it if it has the focus.
    [Fact]
    public void TheWheel_OverAComboBox_LeavesThePage() => UiThread.Run(() =>
    {
        var combo = new ComboBox { ItemsSource = new[] { "One", "Two" } };
        var text  = new TextBlock { Text = "Not a combo box" };
        var page  = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { combo, text, new Border { Height = 800 } } },
        };
        NestedScrolling.Attach(page);
        Shown(page, 400, 300, window =>
        {
            Settle(window);

            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            combo.RaiseEvent(preview);
            Assert.False(preview.Handled);
            Assert.Equal(0, page.VerticalOffset);

            // A notch moves the page as far as a list goes, not 120 pixels.
            Wheel(text, -120);
            window.UpdateLayout();
            Assert.Equal(SystemParameters.WheelScrollLines * 16.0, page.VerticalOffset);
        });
    });

    [Theory]
    [InlineData(true, false)]    // a click: the page stays under the pointer
    [InlineData(false, true)]    // the keyboard: the page follows the row
    public void BringingARowIntoView_MovesThePage_OnlyWithoutTheMouse(bool mouseDown, bool moves) => UiThread.Run(() =>
    {
        var list = new ListBox { Height = 120, ItemsSource = Enumerable.Range(1, 30).Select(i => $"Row {i}").ToList() };
        var page = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { new Border { Height = 400 }, list } },
        };
        NestedScrolling.Attach(page, () => mouseDown);
        Shown(page, 400, 300, window =>
        {
            Settle(window);
            UiThread.Find<ListBoxItem>(list).First().BringIntoView();
            Settle(window);

            Assert.Equal(moves, page.VerticalOffset > 0);
        });
    });

    [Theory]
    [InlineData(700, false)]
    [InlineData(480, true)]
    public void ThePage_ScrollsOnlyWhenTheWindowIsTooShort(int height, bool scrolls) => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 2, rows: 2, settingsChecks: false);
        Shown(page, 1060, height, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            Settle(window);

            Assert.Equal(scrolls, page.PageScroller.ScrollableHeight > 0);
            Assert.Equal(scrolls ? Visibility.Visible : Visibility.Collapsed, page.PageScroller.ComputedVerticalScrollBarVisibility);
        });
    });

    // The page's least height follows what's above the tabs, as it wraps and folds.
    [Fact]
    public void ThePagesLeastHeight_FollowsTheCardsAndTheFold() => UiThread.Run(() =>
    {
        var page = Page(new ConnectionSettingsService(_dir), categories: 2, rows: 2);
        Shown(page, 1060, 700, window =>
        {
            UiThread.Wait(page.ViewModel.RefreshAsync());
            Settle(window);
            var wide = page.Shell.MinHeight;
            Assert.True(wide > 300);

            window.Width = 640;
            Settle(window);
            var narrow = page.Shell.MinHeight;
            Assert.True(narrow > wide, $"Wrapped: {narrow:N0}, wide: {wide:N0}.");

            page.HealthSection.IsFolded = true;
            Settle(window);
            Assert.True(page.Shell.MinHeight < narrow, $"Folded: {page.Shell.MinHeight:N0}, unfolded: {narrow:N0}.");
        });
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
