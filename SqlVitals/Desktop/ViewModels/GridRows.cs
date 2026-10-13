using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A grid's rows in its view model (#184): one collection for the page's life, refilled on each
/// read with <see cref="Replace"/>. The grid stays bound to the same collection, so its view
/// keeps the sort and the filter (<c>GridFilterBox</c>) the user chose, which a new list would
/// drop. A refill is one reset, not an event per row.
/// </summary>
public sealed class GridRows<T> : ObservableCollection<T>, IGridRows
{
    public bool IsFilled { get; private set; }

    /// <summary>The rows read now, in place of the last ones.</summary>
    public void Replace(IEnumerable<T> rows)
    {
        IsFilled = true;
        Items.Clear();
        foreach (var row in rows)
            Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
