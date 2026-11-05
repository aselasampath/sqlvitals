using System.Collections.Specialized;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A multi-select filter box on its own (#237): the values seen, what's ticked, what it matches and
/// shows, ticks kept through a refill that changes the list in place, and Clear.
/// </summary>
public sealed class MultiFilterChoicesTests
{
    private static MultiFilterChoices Box(params string?[] values)
    {
        var box = new MultiFilterChoices();
        box.Replace(values);
        return box;
    }

    /// <summary>Ticks each value, named in any case.</summary>
    internal static void Tick(MultiFilterChoices box, params string[] values)
    {
        foreach (var value in values)
            box.Items.Single(i => string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase)).IsChecked = true;
    }

    private static string[] Values(MultiFilterChoices box) => box.Items.Select(i => i.Value).ToArray();

    [Fact]
    public void Replace_ListsTheValuesSeen_Sorted_OnceIgnoringCase_WithoutBlanks()
    {
        var box = Box("SalesDb", null, "BillingDb", "  ", "salesdb", "(All)", "Archive");

        Assert.Equal(["Archive", "BillingDb", "SalesDb"], Values(box));
        Assert.True(box.IsAll);
        Assert.Null(box.Values);
        Assert.Equal("(All)", box.Summary);
        Assert.Equal("(All)", box.ToolTipText);
        Assert.True(box.Matches("anything"));
        Assert.True(box.Matches(null));
    }

    [Fact]
    public void Ticked_MatchesAnyOfThem_IgnoringCase_AndSaysWhatsTicked()
    {
        var box = Box("Archive", "BillingDb", "SalesDb");
        var changes = new List<string?>();
        box.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Tick(box, "SalesDb");

        Assert.False(box.IsAll);
        Assert.Equal("SalesDb", box.Summary);
        Assert.Contains(nameof(MultiFilterChoices.Values), changes);
        Assert.True(box.Matches("salesdb"));
        Assert.False(box.Matches("BillingDb"));
        Assert.False(box.Matches(null));

        Tick(box, "Archive");

        Assert.Equal("2 selected", box.Summary);
        Assert.Equal($"Archive{Environment.NewLine}SalesDb", box.ToolTipText);
        Assert.True(box.Matches("Archive"));
        Assert.True(box.Matches("SalesDb"));
        Assert.False(box.Matches("BillingDb"));
    }

    [Fact]
    public void Replace_KeepsTheTicked_AfterTheirRowsAreGone_AndTheSameValuesChangeNothing()
    {
        var box = Box("Archive", "BillingDb", "SalesDb");
        Tick(box, "BillingDb");
        var billing = box.Items[1];
        var changes = new List<NotifyCollectionChangedAction>();
        box.Items.CollectionChanged += (_, e) => changes.Add(e.Action);

        box.Replace(["SalesDb", "Archive", "BillingDb"]);
        Assert.Empty(changes);   // nothing to change: an open list is left as it is

        box.Replace(["SalesDb", "Reporting"]);

        Assert.Equal(["BillingDb", "Reporting", "SalesDb"], Values(box));
        Assert.Same(billing, box.Items[0]);
        Assert.True(billing.IsChecked);
        Assert.Equal("BillingDb", box.Summary);
        // In place, never a reset: the list's check boxes stay, the one under the mouse too.
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

        box.Replace([]);
        Assert.Equal(["BillingDb"], Values(box));
        Assert.True(box.Matches("BillingDb"));
    }

    [Fact]
    public void Replace_KeepsTheSpellingTicked_WhenTheValueComesBackInAnotherCase()
    {
        var box = Box("SalesDb");
        Tick(box, "SalesDb");
        var sales = box.Items[0];
        var changes = 0;
        box.Items.CollectionChanged += (_, _) => changes++;

        box.Replace(["SALESDB", "Archive"]);

        Assert.Equal(["Archive", "SalesDb"], Values(box));
        Assert.Same(sales, box.Items[1]);
        box.Replace(["SALESDB", "Archive"]);
        Assert.Equal(1, changes);   // Archive added once; the next refill finds nothing to change
    }

    [Fact]
    public void All_TickedClearsTheTicks_AndCantBeClearedItself()
    {
        var box = Box("Archive", "SalesDb");
        Tick(box, "Archive", "SalesDb");
        Assert.False(box.IsAll);

        box.IsAll = true;

        Assert.True(box.IsAll);
        Assert.All(box.Items, i => Assert.False(i.IsChecked));
        Assert.Equal("(All)", box.Summary);

        // Clearing "(All)", or ticking it again, changes nothing: it only says it's still ticked, for
        // its check box to read back, and the rows aren't filtered again.
        var changes = new List<string?>();
        box.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        box.IsAll = false;
        box.IsAll = true;
        Assert.True(box.IsAll);
        Assert.Equal([nameof(MultiFilterChoices.IsAll), nameof(MultiFilterChoices.IsAll)], changes);
    }

    [Fact]
    public void Clear_UnticksEverything_InOneChange()
    {
        var box = Box("Archive", "BillingDb", "SalesDb");
        Tick(box, "Archive", "SalesDb");
        var changes = 0;
        box.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(MultiFilterChoices.Values) ? 1 : 0;

        box.Clear();

        Assert.Equal(1, changes);
        Assert.True(box.IsAll);
        Assert.Null(box.Values);
        Assert.Equal(["Archive", "BillingDb", "SalesDb"], Values(box));
    }
}
