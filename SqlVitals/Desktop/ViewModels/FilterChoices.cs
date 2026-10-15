using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One of SP Trace's filter boxes (#188): "(All)" and the values seen, and the one picked. Its
/// choices follow what was captured, and keep the one picked even after its calls are dropped.
/// </summary>
public sealed partial class FilterChoices : ObservableObject
{
    /// <summary>The first choice: picked, the box doesn't filter.</summary>
    public const string All = "(All)";

    public ObservableCollection<string> Items { get; } = [All];

    /// <summary>The choice picked; <see cref="All"/>, or null while the box is being refilled.</summary>
    [ObservableProperty]
    private string? _selected = All;

    /// <summary>The value to filter on; null for <see cref="All"/>.</summary>
    public string? Value => Selected is { } s && s != All ? s : null;

    /// <summary>
    /// The values seen now, in place of the last ones, keeping the one picked. Nothing changes when
    /// they are the same: refilling the list closes a drop-down the user has open.
    /// </summary>
    public void Replace(IEnumerable<string?> values)
    {
        var selected = Selected ?? All;
        var choices  = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Append(selected)
            .Where(v => v != All)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Prepend(All)
            .ToList();
        if (Items.SequenceEqual(choices))
            return;

        Items.Clear();
        foreach (var choice in choices)
            Items.Add(choice);
        Selected = selected;
    }

    /// <summary>Back to <see cref="All"/>.</summary>
    public void Clear() => Selected = All;
}
