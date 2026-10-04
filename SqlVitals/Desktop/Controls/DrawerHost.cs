using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The page and its details drawer (#147): the page fills the area; the drawer is pinned in a
/// column beside it, or floats over its right side, or is closed, as <see cref="DrawerLayout"/>
/// says. MainWindow puts its Frame here. Built in code, like <see cref="WorkspaceShell"/>.
/// </summary>
[ContentProperty(nameof(Page))]
public sealed class DrawerHost : Grid
{
    private readonly ColumnDefinition _drawerColumn = new() { Width = new GridLength(0) };
    private UIElement? _page;
    private DrawerLayout _layout = new();

    public DrawerHost()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(_drawerColumn);

        Drawer = new DetailsDrawer();
        SetZIndex(Drawer, 20);
        Children.Add(Drawer);

        Drawer.PinRequested   += () => { _layout.TogglePin(); Apply(); Remember(); };
        Drawer.CloseRequested += Close;
        Drawer.Dragged        += delta => { _layout.Resize(_layout.Width + delta); Apply(); };
        Drawer.DragCompleted  += Remember;

        Apply();
    }

    /// <summary>The page area: MainWindow's Frame.</summary>
    public UIElement? Page
    {
        get => _page;
        set
        {
            if (_page is not null)
                Children.Remove(_page);
            _page = value;
            if (value is not null)
            {
                SetColumn(value, 0);
                Children.Insert(0, value);
            }
        }
    }

    public DetailsDrawer Drawer { get; }

    /// <summary>The drawer's state; replaced by the saved one at startup.</summary>
    public DrawerLayout Layout
    {
        get => _layout;
        set { _layout = value; Apply(); }
    }

    public DrawerState State => _layout.State;

    /// <summary>The pin button was pressed or the edge dropped: whether it pins and its width, to save.</summary>
    public event Action<bool, double>? PreferencesChanged;

    /// <summary>It opened or closed, or pinned or floated: the bar's button follows.</summary>
    public event Action? StateChanged;

    /// <summary>Ctrl+J.</summary>
    public void Toggle()
    {
        if (_layout.IsOpen) Close();
        else                Open();
    }

    public void Open()
    {
        if (_layout.IsOpen)
            return;
        _layout.Open();
        Apply();
    }

    public void Close()
    {
        if (!_layout.IsOpen)
            return;
        // The keyboard was in the drawer: back to the page, rather than lost.
        var hadFocus = Drawer.IsKeyboardFocusWithin;
        _layout.Close();
        Apply();
        if (hadFocus)
            _page?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>Esc: closes a floating drawer. True when it did.</summary>
    public bool Escape()
    {
        var hadFocus = Drawer.IsKeyboardFocusWithin;
        if (!_layout.Escape())
            return false;
        Apply();
        if (hadFocus)
            _page?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        return true;
    }

    /// <summary>Shows what the page supplies.</summary>
    public void Show(DrawerContent content) => Drawer.Show(content);

    private void Remember() => PreferencesChanged?.Invoke(_layout.Pinned, _layout.Width);

    private void Apply()
    {
        var state = _layout.State;
        Drawer.Visibility = state == DrawerState.Closed ? Visibility.Collapsed : Visibility.Visible;
        Drawer.SetPinned(state == DrawerState.Pinned || (state == DrawerState.Closed && _layout.Pinned));
        Drawer.Width = _layout.Width;

        if (state == DrawerState.Pinned)
        {
            _drawerColumn.Width = new GridLength(_layout.Width);
            SetColumn(Drawer, 1);
            Drawer.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            // Floating: over the page's right side, which keeps its width.
            _drawerColumn.Width = new GridLength(0);
            SetColumn(Drawer, 0);
            Drawer.HorizontalAlignment = HorizontalAlignment.Right;
        }
        StateChanged?.Invoke();
    }
}
