using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The Ctrl+K quick switcher (#144): a search box over the dimmed window, and what it finds in
/// groups, from <see cref="QuickSwitcher"/>. The keyboard stays in the box: ↑ and ↓ move the
/// selection, Enter opens it, Esc closes and gives the focus back to where it was. A result that
/// can't be opened says why, and the selection steps over it.
/// </summary>
public partial class QuickSwitcherOverlay : UserControl
{
    private Func<string, IReadOnlyList<SwitcherSection>> _search = _ => [];
    private Func<Guid, ConnectionSelectorItem?> _server = _ => null;

    // Where the keyboard was when it opened.
    private IInputElement? _returnFocus;

    // The results that can be opened, in order, with their rows; and the one selected.
    private readonly List<(SwitcherItem Item, Border Row, FrameworkElement EnterKey)> _rows = [];
    private int _selected = -1;

    public QuickSwitcherOverlay()
    {
        InitializeComponent();
        Backdrop.PreviewMouseDown += Backdrop_PreviewMouseDown;
        // In a short window the list gives up room before the box and the keys do.
        SizeChanged += (_, e) => Scroll.MaxHeight = Math.Clamp(e.NewSize.Height - 240, 120, 440);
    }

    /// <summary>
    /// A result was picked: open its route. The flag is true when the keyboard was in the page,
    /// which the route replaces, so it should go into the new page instead.
    /// </summary>
    public event Action<Route, bool>? OpenRequested;

    /// <summary>What holds the page: focus in it doesn't survive another page being opened.</summary>
    public UIElement? PageHost { get; set; }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Where the keyboard is; off screen, in tests, there is no keyboard focus to read.</summary>
    internal Func<IInputElement?> FocusedElement { get; set; } = () => Keyboard.FocusedElement;

    /// <summary>The element the switcher last moved the keyboard focus to; for tests, where nothing is on screen.</summary>
    internal UIElement? FocusMovedTo { get; private set; }

    /// <summary>The results on show.</summary>
    internal IReadOnlyList<SwitcherSection> Sections { get; private set; } = [];

    /// <summary>The results that can be opened, in the order ↓ goes.</summary>
    internal IReadOnlyList<SwitcherItem> Openable => _rows.Select(r => r.Item).ToList();

    internal SwitcherItem? Selected => _selected >= 0 ? _rows[_selected].Item : null;

    internal TextBox SearchBox => Query;

    /// <summary>Opens it, empty, with the keyboard in the box.</summary>
    /// <param name="search">The results for what is typed (see <see cref="QuickSwitcher.Search"/>).</param>
    /// <param name="server">A saved server's row, for its health dot and score.</param>
    /// <param name="keepsPage">The page on screen is a server's: typing another server's name keeps it.</param>
    public void Open(Func<string, IReadOnlyList<SwitcherSection>> search, Func<Guid, ConnectionSelectorItem?> server, bool keepsPage)
    {
        _search = search;
        _server = server;
        if (!IsOpen)
            _returnFocus = FocusedElement();
        TxtTip.Visibility = keepsPage ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        Query.Text = string.Empty;
        Show();
        // Laid out first, so the box can take the focus.
        UpdateLayout();
        MoveFocus(Query);
    }

    /// <summary>Closes it, if it is open.</summary>
    /// <param name="restoreFocus">Give the keyboard back to where it was when it opened.</param>
    public void Close(bool restoreFocus)
    {
        if (!IsOpen)
            return;
        Visibility = Visibility.Collapsed;
        var target = _returnFocus;
        _returnFocus = null;
        if (!restoreFocus || target is null)
            return;
        // A link in a grid's cell is a content element, not an element of its own.
        if (target is UIElement element)
            MoveFocus(element);
        else
            Keyboard.Focus(target);
    }

    // ── Results ──────────────────────────────────────────────────────────────

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsOpen)
            Show();
    }

    // The results for the text in the box, the first that can be opened selected.
    private void Show()
    {
        var text = Query.Text;
        TxtPrompt.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        Sections = _search(text);
        Results.Children.Clear();
        _rows.Clear();
        _selected = -1;
        foreach (var section in Sections)
        {
            var heading = new TextBlock
            {
                Text = section.Title.ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, Results.Children.Count == 0 ? 6 : 12, 10, 4),
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            AutomationProperties.SetName(heading, section.Title);
            Results.Children.Add(heading);

            foreach (var item in section.Items)
            {
                var (row, enterKey) = Row(item);
                Results.Children.Add(row);
                if (item.CanOpen)
                    _rows.Add((item, row, enterKey));
            }
        }

        var none = Sections.Count == 0;
        TxtEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        TxtEmpty.Text = text.Trim().Length == 0
            ? "The pages you open show here. Type to find a server, a page or a setting."
            : $"No server, page or setting matches “{text.Trim()}”.";
        Scroll.ScrollToTop();
        Select(_rows.Count > 0 ? 0 : -1);
        if (none)
            Announce(TxtEmpty.Text);
    }

    private (Border Row, FrameworkElement EnterKey) Row(SwitcherItem item)
    {
        var dock = new DockPanel();

        // One slot on the left, so the names line up: its page's icon, or a server's health dot. A
        // server's page has both: its dot goes beside the score.
        var server = item.Server is { } id ? _server(id) : null;
        var slot = new Grid { Width = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        if (item.Icon.Length > 0)
        {
            var icon = new TextBlock { Text = item.Icon, FontFamily = new FontFamily(NavMenu.IconFont), FontSize = 14 };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            slot.Children.Add(icon);
        }
        else if (server is not null)
        {
            slot.Children.Add(Dot(server, new Thickness(3, 0, 0, 0)));
        }
        DockPanel.SetDock(slot, Dock.Left);
        dock.Children.Add(slot);

        // Then, from the right: Enter while it is selected, the server's score, where it is.
        var enterKey = Key("Enter");
        enterKey.Margin = new Thickness(10, 0, 0, 0);
        enterKey.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(enterKey, Dock.Right);
        dock.Children.Add(enterKey);
        if (server?.Score is not null)
        {
            var score = new TextBlock { Text = server.ScoreText, Foreground = server.ScoreBrush, FontWeight = FontWeights.Bold, Margin = new Thickness(item.Icon.Length > 0 ? 6 : 10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(score, Dock.Right);
            dock.Children.Add(score);
        }
        if (server is not null && item.Icon.Length > 0)
        {
            var dot = Dot(server, new Thickness(12, 0, 0, 0));
            DockPanel.SetDock(dot, Dock.Right);
            dock.Children.Add(dot);
        }
        if (item.Detail.Length > 0)
        {
            var detail = new TextBlock { Text = item.Detail, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            DockPanel.SetDock(detail, Dock.Right);
            dock.Children.Add(detail);
        }

        // Its name, the part typed in bold; one that can't be opened, dimmed, with the reason under it.
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, item.CanOpen ? "TextPrimary" : "TextDisabled");
        if (item.MatchStart >= 0 && item.MatchStart + item.MatchLength <= item.Text.Length)
        {
            name.Inlines.Add(new Run(item.Text[..item.MatchStart]));
            name.Inlines.Add(new Run(item.Text.Substring(item.MatchStart, item.MatchLength)) { FontWeight = FontWeights.Bold });
            name.Inlines.Add(new Run(item.Text[(item.MatchStart + item.MatchLength)..]));
        }
        else
        {
            name.Text = item.Text;
        }
        if (item.Unavailable is { } reason)
        {
            var why = new TextBlock { Text = reason, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            why.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(name);
            lines.Children.Add(why);
            dock.Children.Add(lines);
        }
        else
        {
            dock.Children.Add(name);
        }

        var row = new Border
        {
            Child = dock,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Background = Brushes.Transparent,
            Cursor = item.CanOpen ? Cursors.Hand : Cursors.Arrow
        };
        AutomationProperties.SetName(row, Spoken(item, server));
        if (item.CanOpen)
        {
            row.MouseEnter += (_, _) => { if (Selected != item) row.Background = Tint("TextPrimaryColor", 0x14); };
            row.MouseLeave += (_, _) => { if (Selected != item) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Pick(item);
            };
        }
        return (row, enterKey);
    }

    private static Ellipse Dot(ConnectionSelectorItem server, Thickness margin) => new()
    {
        Width = 8, Height = 8, StrokeThickness = 1.5, Fill = server.HealthFill, Stroke = server.HealthStroke, Margin = margin,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Key(string text) =>
        new() { Style = (Style)FindResource("Key"), Child = new TextBlock { Text = text, Style = (Style)FindResource("KeyText") } };

    // A theme colour, see-through: the selection is the accent showing through.
    private SolidColorBrush Tint(string colorKey, byte alpha) =>
        TryFindResource(colorKey) is Color c ? new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)) : new SolidColorBrush(Color.FromArgb(alpha, 0x80, 0x80, 0x80));

    // "TempDB, Resources", "PROD-SQL02, health score 96", "Agent Jobs, not available: …"
    private static string Spoken(SwitcherItem item, ConnectionSelectorItem? server)
    {
        var parts = new List<string> { item.Text };
        if (item.Detail.Length > 0)
            parts.Add(item.Detail);
        if (server?.Score is { } score)
            parts.Add($"health score {score.Value}");
        if (item.Unavailable is { } reason)
            parts.Add($"can't be opened: {reason}");
        return string.Join(", ", parts);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private void Select(int index)
    {
        if (_selected >= 0 && _selected < _rows.Count)
        {
            _rows[_selected].Row.Background = Brushes.Transparent;
            _rows[_selected].EnterKey.Visibility = Visibility.Collapsed;
        }

        _selected = index;
        if (index < 0)
            return;
        var (_, row, enterKey) = _rows[index];
        row.Background = Tint("AccentColor", 0x2E);
        enterKey.Visibility = Visibility.Visible;
        row.BringIntoView();
        Announce($"{AutomationProperties.GetName(row)}, {index + 1} of {_rows.Count}");
    }

    // ↑ and ↓ go round.
    private void Move(int by)
    {
        if (_rows.Count == 0)
            return;
        Select(((_selected + by) % _rows.Count + _rows.Count) % _rows.Count);
    }

    private void Announce(string text)
    {
        TxtAnnounce.Text = text;
        AutomationProperties.SetName(TxtAnnounce, text);
        if (UIElementAutomationPeer.FromElement(TxtAnnounce) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ── Opening and closing ──────────────────────────────────────────────────

    private void Pick(SwitcherItem item)
    {
        if (!item.CanOpen)
            return;
        // The keyboard was in the page, which is about to go: into the new one instead.
        var intoPage = _returnFocus is not DependencyObject focused || PageHost is null || IsWithin(focused, PageHost);
        Close(restoreFocus: !intoPage);
        OpenRequested?.Invoke(item.Route, intoPage);
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                Move(1);
                break;
            case System.Windows.Input.Key.Up:
                Move(-1);
                break;
            case System.Windows.Input.Key.Enter:
                if (Selected is { } item)
                    Pick(item);
                break;
            case System.Windows.Input.Key.Escape:
                Close(restoreFocus: true);
                break;
            // The keyboard stays in the box until it closes.
            case System.Windows.Input.Key.Tab:
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // A click outside the box closes it, and goes no further.
    private void Backdrop_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource as DependencyObject, Card))
            return;
        e.Handled = true;
        Close(restoreFocus: true);
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var e = element; e is not null; e = (e is Visual ? VisualTreeHelper.GetParent(e) : null) ?? LogicalTreeHelper.GetParent(e))
            if (ReferenceEquals(e, ancestor))
                return true;
        return false;
    }

    private void MoveFocus(UIElement element)
    {
        FocusMovedTo = element;
        element.Focus();
    }
}
