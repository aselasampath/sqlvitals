using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Shell;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The Ctrl+K quick switcher (#144): a search box over the dimmed window, and what its
/// <see cref="QuickSwitcherViewModel"/> finds, in groups (#191). The keyboard stays in the box: ↑
/// and ↓ move the selection, Enter opens it, Esc closes and gives the focus back to where it was. A
/// result that can't be opened says why, and the selection steps over it.
/// </summary>
public partial class QuickSwitcherOverlay : UserControl
{
    private QuickSwitcherViewModel _model = new();

    // Where the keyboard was when it opened, and whether closing gives it back.
    private IInputElement? _returnFocus;
    private bool _restoreFocus;

    // The rows of the results that can be opened, in order, and the one drawn selected.
    private readonly List<(Border Row, FrameworkElement EnterKey)> _rows = [];
    private int _selected = -1;

    public QuickSwitcherOverlay()
    {
        DataContext = _model;
        InitializeComponent();
        Follow(_model);
        Backdrop.PreviewMouseDown += Backdrop_PreviewMouseDown;
        // In a short window the list gives up room before the box and the keys do.
        SizeChanged += (_, e) => Scroll.MaxHeight = Math.Clamp(e.NewSize.Height - 240, 120, 440);
    }

    /// <summary>What it searches and shows: the shell's, or its own until then.</summary>
    public QuickSwitcherViewModel ViewModel
    {
        get => _model;
        set
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.Opened          -= OnOpened;
            _model = value;
            DataContext = value;
            Follow(value);
            Visibility = value.IsOpen ? Visibility.Visible : Visibility.Collapsed;
            ShowTip();
            ShowResults();
        }
    }

    private void Follow(QuickSwitcherViewModel model)
    {
        model.PropertyChanged += OnModelChanged;
        model.Opened          += OnOpened;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QuickSwitcherViewModel.IsOpen):
                if (_model.IsOpen) ShowOpen();
                else               ShowClosed();
                break;
            case nameof(QuickSwitcherViewModel.KeepsPage):
                ShowTip();
                break;
            case nameof(QuickSwitcherViewModel.Sections):
                ShowResults();
                break;
            case nameof(QuickSwitcherViewModel.SelectedIndex):
                Select(_model.SelectedIndex);
                break;
        }
    }

    /// <inheritdoc cref="QuickSwitcherViewModel.OpenRequested"/>
    public event Action<Route, bool>? OpenRequested
    {
        add    => _model.OpenRequested += value;
        remove => _model.OpenRequested -= value;
    }

    /// <summary>What holds the page: focus in it doesn't survive another page being opened.</summary>
    public UIElement? PageHost { get; set; }

    public bool IsOpen => _model.IsOpen;

    /// <summary>Where the keyboard is; off screen, in tests, there is no keyboard focus to read.</summary>
    internal Func<IInputElement?> FocusedElement { get; set; } = () => Keyboard.FocusedElement;

    /// <summary>The element the switcher last moved the keyboard focus to; for tests, where nothing is on screen.</summary>
    internal UIElement? FocusMovedTo { get; private set; }

    /// <summary>The results on show.</summary>
    internal IReadOnlyList<SwitcherSection> Sections => _model.Sections;

    /// <summary>The results that can be opened, in the order ↓ goes.</summary>
    internal IReadOnlyList<SwitcherItem> Openable => _model.Openable;

    internal SwitcherItem? Selected => _model.Selected;

    internal TextBox SearchBox => Query;

    /// <inheritdoc cref="QuickSwitcherViewModel.Open"/>
    public void Open(Func<string, IReadOnlyList<SwitcherSection>> search, Func<Guid, ConnectionSelectorItem?> server, bool keepsPage) =>
        _model.Open(search, server, keepsPage);

    /// <summary>Closes it, if it is open.</summary>
    /// <param name="restoreFocus">Give the keyboard back to where it was when it opened.</param>
    public void Close(bool restoreFocus)
    {
        _restoreFocus = restoreFocus;
        try { _model.Close(); }
        finally { _restoreFocus = false; }
    }

    // Opened: over the window, remembering where the keyboard was.
    private void ShowOpen()
    {
        _returnFocus = FocusedElement();
        Visibility = Visibility.Visible;
    }

    // Opened, or opened again: the tip as this page has it, and the keyboard into the box, once it is laid out.
    private void OnOpened()
    {
        ShowTip();
        UpdateLayout();
        MoveFocus(Query);
    }

    // Closed: the keyboard back to where it was, when asked; the shell closing it leaves it be.
    private void ShowClosed()
    {
        Visibility = Visibility.Collapsed;
        var target = _returnFocus;
        _returnFocus = null;
        if (!_restoreFocus || target is null)
            return;
        // A link in a grid's cell is a content element, not an element of its own.
        if (target is UIElement element)
            MoveFocus(element);
        else
            Keyboard.Focus(target);
    }

    // Inside a server's workspace: how to keep the page on another server.
    private void ShowTip() => TxtTip.Visibility = _model.KeepsPage ? Visibility.Visible : Visibility.Collapsed;

    // ── Results ──────────────────────────────────────────────────────────────

    // The results, the first that can be opened selected (the view model says which).
    private void ShowResults()
    {
        TxtPrompt.Visibility = _model.Query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        Results.Children.Clear();
        _rows.Clear();
        _selected = -1;
        foreach (var section in _model.Sections)
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
                    _rows.Add((row, enterKey));
            }
        }

        var none = _model.Sections.Count == 0;
        TxtEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        TxtEmpty.Text = _model.EmptyText;
        Scroll.ScrollToTop();
        if (none)
            Announce(TxtEmpty.Text);
    }

    private (Border Row, FrameworkElement EnterKey) Row(SwitcherItem item)
    {
        var dock = new DockPanel();

        // One slot on the left, so the names line up: its page's icon, or a server's health dot. A
        // server's page has both: its dot goes beside the score.
        var server = item.Server is { } id ? _model.ServerOf(id) : null;
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
        ThemeResources.Wash(TryFindResource(colorKey) is Color c ? c : Colors.Gray, alpha);

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

    // The selected row in the accent, with Enter on it, in view and read out.
    private void Select(int index)
    {
        if (_selected >= 0 && _selected < _rows.Count)
        {
            _rows[_selected].Row.Background = Brushes.Transparent;
            _rows[_selected].EnterKey.Visibility = Visibility.Collapsed;
        }

        _selected = index;
        if (index < 0 || index >= _rows.Count)
            return;
        var (row, enterKey) = _rows[index];
        row.Background = Tint("AccentColor", 0x2E);
        enterKey.Visibility = Visibility.Visible;
        row.BringIntoView();
        Announce($"{AutomationProperties.GetName(row)}, {index + 1} of {_rows.Count}");
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
        _restoreFocus = !intoPage;
        try { _model.Pick(item, intoPage); }
        finally { _restoreFocus = false; }
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                _model.Move(1);
                break;
            case System.Windows.Input.Key.Up:
                _model.Move(-1);
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
