using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A multi-select filter box (#237), shown by the <c>CheckListFilter</c> control: "(All)" and a
/// check box per value seen. Nothing ticked, or "(All)", means no filter; a row matches when its
/// value is one of those ticked. Its values follow what was captured, and keep those ticked even
/// after their rows are gone.
/// </summary>
public sealed class MultiFilterChoices : ObservableObject
{
    /// <summary>The first entry: ticked, the box doesn't filter.</summary>
    public const string All = "(All)";

    /// <summary>The values seen, in order; each keeps its tick through <see cref="Replace"/>.</summary>
    public ObservableCollection<FilterChoice> Items { get; } = [];

    // Several ticks change at once: one change after, not one per value.
    private bool _held;

    private IReadOnlySet<string>? _values;

    /// <summary>The values ticked, to match rows on (ignoring case); null when the box doesn't filter.</summary>
    public IReadOnlySet<string>? Values => _values;

    /// <summary>
    /// "(All)" ticked: nothing else is. Ticking it clears the ticks; it can't be cleared itself, it
    /// clears when a value is ticked.
    /// </summary>
    public bool IsAll
    {
        get => _values is null;
        set
        {
            if (value)
                Clear();
            else
                OnPropertyChanged();   // still ticked: the check box reads it back
        }
    }

    /// <summary>What the closed box shows: "(All)", the one value ticked, or "3 selected".</summary>
    public string Summary => Ticked() switch
    {
        []        => All,
        [var one] => one,
        var many  => $"{many.Count} selected",
    };

    /// <summary>The values ticked, a line each; "(All)" when none are.</summary>
    public string ToolTipText => Ticked() is { Count: > 0 } ticked ? string.Join(Environment.NewLine, ticked) : All;

    /// <summary>True when the box lets <paramref name="value"/> through.</summary>
    public bool Matches(string? value) => _values is not { } values || value is not null && values.Contains(value);

    /// <summary>
    /// The values seen now, in place of the last ones, keeping those ticked. The list is changed in
    /// place, and not at all when they are the same, so a drop-down the user has open stays as it is.
    /// </summary>
    public void Replace(IEnumerable<string?> values)
    {
        var known = new Dictionary<string, FilterChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
            known.TryAdd(item.Value, item);

        // The ticked first, so a value seen again in another case keeps the spelling ticked.
        var next = Items.Where(i => i.IsChecked).Select(i => i.Value)
            .Concat(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!))
            .Where(v => v != All)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(v => known.TryGetValue(v, out var item) ? item : new FilterChoice(v))
            .ToList();
        if (Items.SequenceEqual(next))
            return;

        // Items[..i] are next[..i]: each next one is moved up or added, and what's left at the end goes.
        for (var i = 0; i < next.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], next[i]))
                continue;
            var at = Items.IndexOf(next[i]);
            if (at > i)
                Items.Move(at, i);
            else
            {
                next[i].PropertyChanged += OnItemChanged;
                Items.Insert(i, next[i]);
            }
        }
        while (Items.Count > next.Count)
        {
            Items[^1].PropertyChanged -= OnItemChanged;
            Items.RemoveAt(Items.Count - 1);
        }
    }

    /// <summary>Every tick cleared: back to "(All)".</summary>
    public void Clear()
    {
        // Nothing ticked: nothing to filter again, but "(All)" a click cleared reads back that it's ticked.
        if (_values is null)
        {
            OnPropertyChanged(nameof(IsAll));
            return;
        }
        _held = true;
        try
        {
            foreach (var item in Items)
                item.IsChecked = false;
        }
        finally
        {
            _held = false;
        }
        Changed();
    }

    private List<string> Ticked() => Items.Where(i => i.IsChecked).Select(i => i.Value).ToList();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_held && e.PropertyName == nameof(FilterChoice.IsChecked))
            Changed();
    }

    // The ticks changed: what the box filters on and shows.
    private void Changed()
    {
        var ticked = Ticked();
        _values = ticked.Count == 0 ? null : ticked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(Values));
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ToolTipText));
    }
}
