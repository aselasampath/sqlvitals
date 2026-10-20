using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// An object's group in a Resource Queries grid (#232). Kept across refreshes under its key, so a
/// refresh or the filter keeps whether the user expanded it. Groups start collapsed: grouped, the
/// grid first reads as one line per procedure.
/// </summary>
internal sealed class QueryGroupHeader(string key, string name) : INotifyPropertyChanged
{
    public string Key { get; } = key;

    private string _name = name;
    public string Name { get => _name; private set => Set(ref _name, value); }

    private string? _type;
    /// <summary>"Stored Proc", "Trigger"…; null for ad hoc queries.</summary>
    public string? Type { get => _type; private set => Set(ref _type, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    /// <summary>Where the group sits: the costliest first (see <see cref="QueryObjectGroups.ByObject"/>).</summary>
    public int Order { get; set; } = int.MaxValue;

    public void Update(QueryObjectGroup group)
    {
        Name    = group.Name;
        Type    = group.Type;
        Summary = group.Summary(CultureInfo.CurrentCulture);
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>Sorts a grid's object groups by <see cref="Order"/>.</summary>
    public sealed class GroupOrder : IComparer
    {
        public static readonly GroupOrder Instance = new();

        public int Compare(object? x, object? y) => OrderOf(x).CompareTo(OrderOf(y));

        private static int OrderOf(object? group) =>
            group is CollectionViewGroup { Name: QueryGroupHeader header } ? header.Order : int.MaxValue;
    }
}
