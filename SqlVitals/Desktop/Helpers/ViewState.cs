using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// How a page was being looked at (#139): the tab chosen, each grid's sort, filter, selected row
/// and scroll position, and where the page was scrolled to. Taken when the page is left and put
/// back on a new instance of it, so returning to a page doesn't start it from scratch.
/// </summary>
/// <remarks>
/// Only what the user looks through is kept, never a choice that does something: a combo box or
/// toggle can start a trace or a timer, so those stay as each page sets them up. A page is rebuilt
/// rather than kept, because pages drop their timers, sessions and traces when they're left.
/// Elements are matched by name, or by their order among unnamed ones of the same type.
/// </remarks>
public sealed class ViewState
{
    private readonly Dictionary<string, int>       _tabs    = [];
    private readonly Dictionary<string, GridState> _grids   = [];
    private readonly Dictionary<string, string>    _filters = [];
    private readonly Dictionary<string, Point>     _scrolls = [];

    /// <param name="SelectedKey">The selected row's text columns, to find it among new rows.</param>
    private sealed record GridState(IReadOnlyList<SortDescription> Sorts, IReadOnlyList<object?>? SelectedKey, Point Offset);

    /// <param name="previous">
    /// The state this instance was opened with. A grid that never got rows here (on a tab not
    /// opened this time) keeps its earlier state, and so does its filter, instead of losing them.
    /// </param>
    public static ViewState Capture(DependencyObject page, ViewState? previous = null)
    {
        var state = new ViewState();
        var seen = new HashSet<string>();
        foreach (var (key, element) in Elements(page))
        {
            if (!seen.Add(key))
                continue;
            switch (element)
            {
                case TabControl tabs when tabs.SelectedIndex >= 0:
                    state._tabs[key] = tabs.SelectedIndex;
                    break;
                case DataGrid grid when grid.ItemsSource is not null:
                    state._grids[key] = new GridState(grid.Items.SortDescriptions.ToList(),
                                                      grid.SelectedItem is { } item ? RowKey(grid, item) : null,
                                                      Offset(ScrollViewerIn(grid)));
                    break;
                case DataGrid when previous?._grids.TryGetValue(key, out var earlier) == true:
                    state._grids[key] = earlier;
                    break;
                case GridFilterBox filter when filter.Text.Length > 0:
                    state._filters[key] = filter.Text;
                    break;
                case GridFilterBox { Target.ItemsSource: null } when previous?._filters.TryGetValue(key, out var text) == true:
                    state._filters[key] = text;
                    break;
                case ScrollViewer scroll when scroll.VerticalOffset > 0 || scroll.HorizontalOffset > 0:
                    state._scrolls[key] = Offset(scroll);
                    break;
            }
        }
        return state;
    }

    /// <summary>
    /// Puts the state back on a new instance of the page, as soon as it's built: the tabs at once,
    /// and each grid's filter, sort, selection and scrolling once the grid gets its rows. Anything
    /// the user changes while the page loads is kept: a grid already sorted, filtered or with a
    /// row selected isn't touched. The page's own scrolling waits for <see cref="RestoreScrolling"/>.
    /// </summary>
    public void Restore(FrameworkElement page)
    {
        var elements = Index(page);
        T? Find<T>(string key) where T : class => elements.TryGetValue(key, out var e) ? e as T : null;

        foreach (var (key, index) in _tabs)
            if (Find<TabControl>(key) is { } tabs && index < tabs.Items.Count && tabs.SelectedIndex != index)
                tabs.SelectedIndex = index;

        // A filter first, so the sort and selection apply to the rows it leaves.
        foreach (var (key, text) in _filters)
            if (Find<GridFilterBox>(key) is { } filter)
            {
                void Apply()
                {
                    if (filter.Text.Length == 0)
                        filter.Text = text;
                }
                if (filter.Target is { } target)
                    WhenFilled(target, Apply);
                else
                    Apply();
            }

        foreach (var (key, state) in _grids)
            if (Find<DataGrid>(key) is { } grid)
                WhenFilled(grid, () => Apply(grid, state));
    }

    /// <summary>
    /// Scrolls the page back to where it was, once it has loaded and been laid out; a page the
    /// user has already scrolled stays where it is.
    /// </summary>
    public void RestoreScrolling(FrameworkElement page)
    {
        if (_scrolls.Count == 0)
            return;

        page.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Safely(() =>
        {
            var elements = Index(page);
            foreach (var (key, offset) in _scrolls)
                if (elements.TryGetValue(key, out var element) && element is ScrollViewer { VerticalOffset: 0, HorizontalOffset: 0 } scroll)
                    ScrollTo(scroll, offset);
        }));
    }

    // A user control's names are its own, so two can repeat one; the first is used.
    private static Dictionary<string, FrameworkElement> Index(DependencyObject page)
    {
        var elements = new Dictionary<string, FrameworkElement>();
        foreach (var (key, element) in Elements(page))
            elements.TryAdd(key, element);
        return elements;
    }

    // Once the grid has rows, and after the code that set them has finished: DataGridRefresh puts
    // its own sort back straight after setting the rows, which would undo one applied in between.
    private static void WhenFilled(DataGrid grid, Action apply)
    {
        if (grid.ItemsSource is not null)
        {
            grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, Safely(apply));
            return;
        }

        // Only the grid's own collection holds the handler, so a grid never filled (a tab never
        // opened) keeps nothing alive.
        var items = (INotifyCollectionChanged)grid.Items;
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (grid.ItemsSource is null)
                return;
            items.CollectionChanged -= OnChanged;
            grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, Safely(apply));
        }
        items.CollectionChanged += OnChanged;
    }

    // Losing a sort or a scroll position isn't worth an error dialog; the log says what happened.
    private static Action Safely(Action apply) => () =>
    {
        try { apply(); }
        catch (Exception ex) { Services.AppLog.Error("View state: could not put back how a page was left", ex); }
    };

    private static void Apply(DataGrid grid, GridState state)
    {
        if (state.Sorts.Count > 0 && grid.Items.SortDescriptions.Count == 0)
        {
            using (grid.Items.DeferRefresh())
            {
                foreach (var sort in state.Sorts)
                    grid.Items.SortDescriptions.Add(sort);
            }

            // The header arrows live on the columns, not the view (as in DataGridRefresh).
            foreach (var column in grid.Columns)
            {
                var match = state.Sorts.FirstOrDefault(s => s.PropertyName == column.SortMemberPath);
                column.SortDirection = match.PropertyName is null ? null : match.Direction;
            }
        }

        if (state.SelectedKey is { } key && grid.SelectedItem is null &&
            grid.Items.Cast<object>().FirstOrDefault(i => i != CollectionView.NewItemPlaceholder && RowKey(grid, i).SequenceEqual(key)) is { } row)
            grid.SelectedItem = row;

        if (state.Offset != default)
            grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, Safely(() =>
            {
                if (ScrollViewerIn(grid) is { VerticalOffset: 0, HorizontalOffset: 0 } scroll)
                    ScrollTo(scroll, state.Offset);
            }));
    }

    // A row's identity across reloads: the text it shows (names, statements, states), not its
    // numbers, which change with every read. A row whose text changed isn't selected again,
    // rather than selecting the wrong one. A grid of numbers only is matched on all of them.
    private static IReadOnlyList<object?> RowKey(DataGrid grid, object item)
    {
        var values = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible && c.ClipboardContentBinding is not null)
            .Select(c => c.OnCopyingCellClipboardContent(item))
            .ToList();
        var text = values.Where(v => v is string).ToList();
        return text.Count > 0 ? text : values;
    }

    private static Point Offset(ScrollViewer? scroll) =>
        scroll is null ? default : new Point(scroll.HorizontalOffset, scroll.VerticalOffset);

    private static void ScrollTo(ScrollViewer scroll, Point offset)
    {
        scroll.ScrollToHorizontalOffset(offset.X);
        scroll.ScrollToVerticalOffset(offset.Y);
    }

    // A grid's own scroll viewer is part of its template, so it's found in the visual tree.
    private static ScrollViewer? ScrollViewerIn(DependencyObject element)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer scroll)
                return scroll;
            if (ScrollViewerIn(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    // The page's tabs, grids, filters and scroll viewers in the logical tree (which, unlike the
    // visual tree, has the grids on tabs not shown), keyed by name or by order among unnamed ones.
    private static IEnumerable<(string Key, FrameworkElement Element)> Elements(DependencyObject root)
    {
        var unnamed = new Dictionary<Type, int>();
        foreach (var element in Walk(root))
        {
            if (element is not (TabControl or DataGrid or GridFilterBox or ScrollViewer))
                continue;

            var type = element.GetType();
            if (!string.IsNullOrEmpty(element.Name))
            {
                yield return ($"{type.Name}:{element.Name}", element);
                continue;
            }
            unnamed[type] = unnamed.GetValueOrDefault(type) + 1;
            yield return ($"{type.Name}#{unnamed[type]}", element);
        }
    }

    private static IEnumerable<FrameworkElement> Walk(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is FrameworkElement element)
                yield return element;
            // A grid's rows and a filter box's parts are nothing to keep.
            if (child is DataGrid or GridFilterBox)
                continue;
            foreach (var deeper in Walk(child))
                yield return deeper;
        }
    }
}
