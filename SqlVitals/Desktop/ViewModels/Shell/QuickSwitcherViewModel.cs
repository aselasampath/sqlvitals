using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>
/// The Ctrl+K quick switcher (#144, #191): what is typed, what it finds in groups
/// (<see cref="QuickSwitcher"/>), the result selected, and opening it. ↑ and ↓ go round the
/// results that can be opened; one that can't says why and is stepped over.
/// <c>QuickSwitcherOverlay</c> draws it and keeps the keyboard in its box.
/// </summary>
public sealed partial class QuickSwitcherViewModel : ObservableObject
{
    private Func<string, IReadOnlyList<SwitcherSection>> _search = _ => [];

    /// <summary>
    /// A result was picked: open its route. The flag is true when the keyboard was in the page,
    /// which the route replaces, so it should go into the new page instead.
    /// </summary>
    public event Action<Route, bool>? OpenRequested;

    /// <summary>It was opened, or opened again while open: the keyboard goes into its box.</summary>
    public event Action? Opened;

    /// <summary>It is open.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>The page on screen is a server's: typing another server's name keeps it.</summary>
    [ObservableProperty]
    private bool _keepsPage;

    /// <summary>A saved server's selector row, for its health dot and score.</summary>
    public Func<Guid, ConnectionSelectorItem?> ServerOf { get; private set; } = _ => null;

    /// <summary>Opens it, empty.</summary>
    /// <param name="search">The results for what is typed (see <see cref="QuickSwitcher.Search"/>).</param>
    /// <param name="server">A saved server's row, for its health dot and score.</param>
    /// <param name="keepsPage">The page on screen is a server's: typing another server's name keeps it.</param>
    public void Open(Func<string, IReadOnlyList<SwitcherSection>> search, Func<Guid, ConnectionSelectorItem?> server, bool keepsPage)
    {
        _search   = search;
        ServerOf  = server;
        KeepsPage = keepsPage;
        IsOpen    = true;
        if (Query.Length == 0)
            Search();
        else
            Query = string.Empty;
        Opened?.Invoke();
    }

    /// <summary>Closes it, if it is open.</summary>
    public void Close() => IsOpen = false;

    // ── Results ──────────────────────────────────────────────────────────────

    /// <summary>What is typed.</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    partial void OnQueryChanged(string value)
    {
        if (IsOpen)
            Search();
    }

    /// <summary>The results on show, in groups.</summary>
    public IReadOnlyList<SwitcherSection> Sections { get; private set; } = [];

    /// <summary>The results that can be opened, in the order ↓ goes.</summary>
    public IReadOnlyList<SwitcherItem> Openable { get; private set; } = [];

    /// <summary>What it says with nothing to show.</summary>
    public string EmptyText => Query.Trim().Length == 0
        ? "The pages you open show here. Type to find a server, a page or a setting."
        : $"No server, page or setting matches “{Query.Trim()}”.";

    // The results for the text in the box, the first that can be opened selected.
    private void Search()
    {
        Sections = _search(Query);
        Openable = Sections.SelectMany(s => s.Items).Where(i => i.CanOpen).ToList();
        _selectedIndex = -1;
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Sections));
        SelectedIndex = Openable.Count > 0 ? 0 : -1;
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private int _selectedIndex = -1;

    /// <summary>Where the selection is in <see cref="Openable"/>; -1 for none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set
        {
            _selectedIndex = value;
            // Raised every time: a new search selects its first result, even at the same place.
            OnPropertyChanged();
            OnPropertyChanged(nameof(Selected));
        }
    }

    /// <summary>The result selected; null for none.</summary>
    public SwitcherItem? Selected => SelectedIndex >= 0 ? Openable[SelectedIndex] : null;

    /// <summary>↑ and ↓: the selection goes round.</summary>
    public void Move(int by)
    {
        if (Openable.Count == 0)
            return;
        SelectedIndex = ((SelectedIndex + by) % Openable.Count + Openable.Count) % Openable.Count;
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    /// <summary>A result clicked, or Enter on the one selected: it closes, and its route opens.</summary>
    /// <param name="intoPage">The keyboard was in the page, which the route replaces.</param>
    /// <returns>False for one that can't be opened, which leaves it open.</returns>
    public bool Pick(SwitcherItem item, bool intoPage)
    {
        if (!item.CanOpen)
            return false;
        Close();
        OpenRequested?.Invoke(item.Route, intoPage);
        return true;
    }
}
