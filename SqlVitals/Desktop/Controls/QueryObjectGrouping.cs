using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Groups a Resource Queries grid by the object each statement belongs to (#232), when switched on:
/// one collapsible group per procedure, function or trigger, and one for the ad hoc queries, the
/// costliest first by the tab's measure. Each header sums up the rows the grid shows now, after the
/// filter. The grid's sort still orders the rows inside a group.
/// </summary>
internal sealed class QueryObjectGrouping
{
    private readonly DataGrid _grid;
    private readonly QueryGroupRank _rank;
    private readonly Dictionary<string, QueryGroupHeader> _headers = [];
    private readonly PropertyGroupDescription _byObject;
    private bool _refreshQueued;

    public QueryObjectGrouping(DataGrid grid, QueryGroupRank rank)
    {
        _grid     = grid;
        _rank     = rank;
        _byObject = new PropertyGroupDescription(null, new HeaderOf(this)) { CustomSort = QueryGroupHeader.GroupOrder.Instance };
        // Rows read again, or filtered: the headers sum up what is shown now.
        ((INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Update();
    }

    /// <summary>The headers, by key; for tests.</summary>
    internal IReadOnlyDictionary<string, QueryGroupHeader> Headers => _headers;

    public bool IsGrouped
    {
        get => _grid.Items.GroupDescriptions.Contains(_byObject);
        set
        {
            if (value == IsGrouped)
                return;
            if (value)
                _grid.Items.GroupDescriptions.Add(_byObject);
            else
                _grid.Items.GroupDescriptions.Remove(_byObject);
        }
    }

    public void SetAllExpanded(bool expanded)
    {
        foreach (var header in _headers.Values)
            header.IsExpanded = expanded;
    }

    /// <summary>Opens the group a row is in, so it can be scrolled to: the verdict's Show it.</summary>
    public void Reveal(ResourceIntensiveQuery row) => HeaderFor(row).IsExpanded = true;

    private QueryGroupHeader HeaderFor(ResourceIntensiveQuery row)
    {
        var key = QueryObjectGroups.KeyOf(row);
        if (!_headers.TryGetValue(key, out var header))
            _headers[key] = header = new QueryGroupHeader(key, string.IsNullOrWhiteSpace(row.ObjectName) ? QueryObjectGroups.AdHoc : row.ObjectName.Trim());
        return header;
    }

    // Each header sums up its rows as shown, and the groups are ordered on those totals. The view
    // has grouped already when this runs, so a group that moved needs one more pass; it is queued,
    // as the view can't be refreshed while it reports a change.
    private void Update()
    {
        var shown = _grid.Items.OfType<ResourceIntensiveQuery>().ToList();
        var moved = false;
        var order = 0;
        foreach (var group in QueryObjectGroups.ByObject(shown, _rank))
        {
            var header = _headers.TryGetValue(group.Key, out var known) ? known : _headers[group.Key] = new QueryGroupHeader(group.Key, group.Name);
            header.Update(group);
            moved |= header.Order != order;
            header.Order = order++;
        }
        if (!moved || !IsGrouped || _refreshQueued)
            return;
        _refreshQueued = true;
        _grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            if (IsGrouped)
                _grid.Items.Refresh();
        });
    }

    // The group a row goes in: its object's header, the same one across refreshes.
    private sealed class HeaderOf(QueryObjectGrouping owner) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is ResourceIntensiveQuery row ? owner.HeaderFor(row) : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
