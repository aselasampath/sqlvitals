using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Shell;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The window's layout around the page (#142): the sidebar pinned (232 px, <see cref="NavSidebar"/>),
/// collapsed (56 px, <see cref="NavRail"/>, a group's pages in a <see cref="NavFlyout"/>) or hidden
/// (0 px, slid in over the page from the ☰ button or the window's left edge), as its
/// <see cref="SidebarViewModel"/> says (#191). The pointer resting, the slide and the keyboard focus
/// are its own. The page itself, the top bar and the frame, is the window's child of this grid, in column 1.
/// </summary>
public sealed class WorkspaceShell : Grid
{
    /// <summary>How long the pointer rests on a group's icon or the left edge before it opens.</summary>
    internal static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the pointer may stray from a menu it opened by resting before it closes.</summary>
    internal static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(SidebarLayout.PinnedWidth) };
    private readonly Border _pinned;
    private readonly Border _rail;
    private readonly Border _edge;
    private readonly Canvas _flyoutLayer = new();
    private readonly TranslateTransform _slide = new();

    // Opening a menu the pointer rests on, and closing one it opened that the pointer has left.
    private readonly DispatcherTimer _hoverTimer = new() { Interval = HoverDelay };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = LeaveDelay };
    private Action? _onHover;
    private Action? _onLeave;

    private SidebarViewModel _model = new();

    // Opened by the pointer resting, not a click or a key: it closes again when the pointer leaves.
    private bool _flyoutByHover;
    private bool _slideByHover;

    // Set while the pointer resting slides the sidebar in, for the slide to know.
    private bool _slidingByHover;

    // The pages' buttons' own tooltips, put back when the open server has them again.
    private readonly Dictionary<string, object?> _ownToolTips = new();

    // The keyboard was sent into the fly-out: Esc takes it back to the group's icon.
    private bool _flyoutHasKeyboard;

    public WorkspaceShell()
    {
        // The server header's score and Start / Stop styles, which go with the sidebar.
        Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
            new Uri("/SqlVitals.Desktop;component/Controls/SidebarStyles.xaml", UriKind.Relative)));

        ColumnDefinitions.Add(_sidebarColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _pinned = BuildPinned();
        _rail   = BuildRail();
        _edge   = new Border
        {
            Width = 6, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
        };
        SetColumnSpan(_edge, 2);
        Panel.SetZIndex(_edge, 15);
        _edge.MouseEnter += (_, _) => Schedule(() => SlideIn(byHover: true, focus: false));
        _edge.MouseLeave += (_, _) => CancelHover();

        SetColumnSpan(_flyoutLayer, 2);
        Panel.SetZIndex(_flyoutLayer, 20);

        Children.Add(_pinned);
        Children.Add(_rail);
        Children.Add(_edge);
        Children.Add(_flyoutLayer);

        _hoverTimer.Tick += (_, _) => Fire(ref _onHover, _hoverTimer);
        _leaveTimer.Tick += (_, _) => Fire(ref _onLeave, _leaveTimer);

        Menu.PageRequested          += PickPage;
        Menu.ScoreRequested         += () => ScoreRequested?.Invoke(Menu.ScoreButton);
        Menu.MonitorToggleRequested += () => _model.ToggleMonitoring();
        Rail.PageRequested  += PickPage;
        Rail.ScoreRequested += () => ScoreRequested?.Invoke(Rail.ServerMark);
        Rail.PinRequested   += () => _model.Choose(SidebarMode.Pinned);
        Rail.GroupRequested += (id, keyboard) => OpenFlyout(id, byHover: false, focus: keyboard);
        foreach (var group in NavMenu.Groups)
        {
            var icon = Rail.Group(group.Id);
            icon.MouseEnter += (_, _) =>
            {
                StopLeave();
                if (Flyout?.GroupId != group.Id)
                    Schedule(() => OpenFlyout(group.Id, byHover: true, focus: false));
            };
            icon.MouseLeave += (_, _) =>
            {
                CancelHover();
                if (Flyout is not null && _flyoutByHover)
                    Leave(() => CloseFlyout(focusIcon: false));
            };

            // One group open at a time, as the view model has it.
            var expander = Menu.Group(group.Id);
            expander.Expanded  += (_, e) => { if (ReferenceEquals(e.OriginalSource, expander)) _model.OpenGroup = group.Id; };
            expander.Collapsed += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, expander) && _model.OpenGroup == group.Id)
                    _model.OpenGroup = null;
            };
        }

        _model.PropertyChanged += OnModelChanged;
        ApplyMode();
    }

    /// <summary>What it shows: the shell's, or its own until then.</summary>
    public SidebarViewModel ViewModel
    {
        get => _model;
        set
        {
            _model.PropertyChanged -= OnModelChanged;
            _model = value;
            _model.PropertyChanged += OnModelChanged;
            VersionText.Text = value.Version;
            ShowServer();
            Menu.Highlight(value.Current);
            Rail.Highlight(value.Current);
            ShowOpenGroup();
            ShowAlertCount();
            ShowUnavailable();
            if (value.IsSlidOver) ShowSlidIn();
            else                  ApplyMode();
        }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SidebarViewModel.Shown):       ApplyMode(); break;
            case nameof(SidebarViewModel.IsSlidOver):
                if (_model.IsSlidOver) ShowSlidIn();
                else                   ShowSlidOut();
                break;
            case nameof(SidebarViewModel.Version):     VersionText.Text = _model.Version; break;
            case nameof(SidebarViewModel.Server):      ShowServer(); break;
            case nameof(SidebarViewModel.Current):
                Menu.Highlight(_model.Current);
                Rail.Highlight(_model.Current);
                break;
            case nameof(SidebarViewModel.OpenGroup):   ShowOpenGroup(); break;
            case nameof(SidebarViewModel.AlertsToolTip): ShowAlertCount(); break;
            case nameof(SidebarViewModel.Unavailable): ShowUnavailable(); break;
        }
    }

    /// <summary>The pinned sidebar, also the one slid in over the page.</summary>
    internal NavSidebar Menu { get; private set; } = null!;

    /// <summary>The collapsed sidebar.</summary>
    internal NavRail Rail { get; private set; } = null!;

    /// <summary>The app's version under its name.</summary>
    internal TextBlock VersionText { get; private set; } = null!;

    /// <summary>Beside the app's name: collapse the pinned sidebar, or pin the slid-in one.</summary>
    internal Button PinButton { get; private set; } = null!;

    /// <summary>Where the keyboard focus goes back to when the slid-in sidebar closes: the top bar's ☰.</summary>
    public UIElement? ReturnFocusTo { get; set; }

    /// <summary>The server's score was clicked: show what lowered it beside the element.</summary>
    public event Action<FrameworkElement>? ScoreRequested;

    /// <summary>A group's fly-out beside the collapsed sidebar, while one is open.</summary>
    internal NavFlyout? Flyout { get; private set; }

    /// <summary>Slides in and out; off in tests, and where Windows has animations turned off.</summary>
    internal bool Animate { get; set; } = SystemParameters.ClientAreaAnimation;

    /// <summary>The element the shell last moved the keyboard focus to; for tests, where nothing is on screen.</summary>
    internal UIElement? FocusMovedTo { get; private set; }

    // ── The state ────────────────────────────────────────────────────────────

    // The state on screen changed; the view model has slid the hidden sidebar out first.
    private void ApplyMode()
    {
        var focusWasIn = _pinned.IsKeyboardFocusWithin || _rail.IsKeyboardFocusWithin || _flyoutLayer.IsKeyboardFocusWithin;
        CancelHover();
        StopLeave();
        CloseFlyout(focusIcon: false);

        var shown = _model.Shown;
        _sidebarColumn.Width = new GridLength(SidebarLayout.WidthOf(shown));
        _pinned.Visibility = shown == SidebarMode.Pinned    ? Visibility.Visible : Visibility.Collapsed;
        _rail.Visibility   = shown == SidebarMode.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _edge.Visibility   = shown == SidebarMode.Hidden    ? Visibility.Visible : Visibility.Collapsed;
        ShowPinButton();

        // The keyboard was in the sidebar as it went: on to ☰, rather than lost.
        if (focusWasIn && ReturnFocusTo is not null)
            MoveFocus(ReturnFocusTo);
    }

    // Collapse when pinned; pin when slid in.
    private void ShowPinButton()
    {
        var pin = _model.IsSlidOver;
        PinButton.Content = pin ? "\uE718" : "\uE77A";
        PinButton.ToolTip = pin ? "Pin the menu open (Ctrl+B)" : "Collapse the menu to icons (Ctrl+B)";
        AutomationProperties.SetName(PinButton, pin ? "Pin the menu open" : "Collapse the menu to icons");
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) =>
        _model.Choose(_model.IsSlidOver ? SidebarMode.Pinned : SidebarMode.Collapsed);

    // ── What the sidebar shows, in each state ────────────────────────────────

    // The open server's header and pages, or none; see NavSidebar.ShowServer.
    private void ShowServer()
    {
        var server = _model.Server;
        Menu.ShowServer(server);
        Rail.ShowServer(server);
        if (server is null)
            CloseFlyout(focusIcon: false);
    }

    // The pinned sidebar's groups: the one the view model has open.
    private void ShowOpenGroup()
    {
        foreach (var group in NavMenu.Groups)
            Menu.Group(group.Id).IsExpanded = group.Id == _model.OpenGroup;
    }

    /// <summary>A count on a page's button and icon, e.g. Alerts'; 0 hides it.</summary>
    public void SetCount(string tag, int count, string toolTip)
    {
        Menu.SetCount(tag, count, toolTip);
        Rail.SetCount(tag, count, toolTip);
    }

    private void ShowAlertCount() => SetCount("Alerts", _model.AlertCount, _model.AlertsToolTip);

    // The pages the open server hasn't got: disabled, saying why; the others as they were.
    private void ShowUnavailable()
    {
        foreach (var tag in _ownToolTips.Keys.Union(_model.Unavailable.Keys).ToList())
        {
            var button = Menu.Button(tag);
            if (!_ownToolTips.ContainsKey(tag))
                _ownToolTips[tag] = button.ToolTip;
            var disabled = _model.Unavailable.TryGetValue(tag, out var reason);
            button.IsEnabled = !disabled;
            button.ToolTip   = disabled ? reason : _ownToolTips[tag];
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }

    // A page picked anywhere closes a fly-out or the slid-in sidebar it was picked in.
    private void PickPage(string tag)
    {
        CloseFlyout(focusIcon: false);
        CloseSlideOver(returnFocus: false);
        _model.OpenPage(tag);
    }

    // ── Collapsed: a group's fly-out ─────────────────────────────────────────

    /// <summary>
    /// Shows a group's pages beside its icon. Clicking the icon of the open one closes it.
    /// </summary>
    /// <param name="byHover">The pointer rested on the icon: it closes again when the pointer leaves.</param>
    /// <param name="focus">Move the keyboard into it (Enter or → on the icon).</param>
    internal void OpenFlyout(string groupId, bool byHover, bool focus)
    {
        CancelHover();
        StopLeave();
        if (_model.Shown != SidebarMode.Collapsed)
            return;

        if (Flyout?.GroupId == groupId)
        {
            if (byHover)
                return;
            // A click on the icon of a fly-out the pointer opened keeps it; another click closes it.
            if (!_flyoutByHover && !focus)
            {
                CloseFlyout(focusIcon: false);
                return;
            }
            _flyoutByHover = false;
            if (focus)
                FocusInto(Flyout);
            return;
        }

        CloseFlyout(focusIcon: false);
        var group  = NavMenu.Groups.First(g => g.Id == groupId);
        var flyout = new NavFlyout(group, _model.Current, Menu.Button);
        flyout.PageRequested  += PickPage;
        flyout.PinRequested   += () => _model.Choose(SidebarMode.Pinned);
        flyout.CloseRequested += () => CloseFlyout(focusIcon: true);
        flyout.MouseEnter     += (_, _) => StopLeave();
        flyout.MouseLeave     += (_, _) =>
        {
            if (_flyoutByHover)
                Leave(() => CloseFlyout(focusIcon: false));
        };
        Flyout = flyout;
        _flyoutByHover = byHover;
        _flyoutLayer.Children.Add(flyout);
        PlaceFlyout(flyout, Rail.Group(groupId));
        AutomationProperties.SetItemStatus(Rail.Group(groupId), "Expanded");

        if (focus)
            FocusInto(flyout);
    }

    // Level with its icon, and inside the window.
    private void PlaceFlyout(NavFlyout flyout, FrameworkElement icon)
    {
        flyout.Measure(new Size(NavFlyout.FlyoutWidth, double.PositiveInfinity));
        var top  = TopOf(icon) - 10;
        var room = ActualHeight > 0 ? ActualHeight : double.PositiveInfinity;
        top = Math.Max(8, Math.Min(top, room - flyout.DesiredSize.Height - 8));
        Canvas.SetLeft(flyout, SidebarLayout.CollapsedWidth + 6);
        Canvas.SetTop(flyout, top);
    }

    private double TopOf(FrameworkElement icon)
    {
        try { return icon.TranslatePoint(new Point(0, 0), this).Y; }
        catch (InvalidOperationException) { return 0; }   // not in the shell's visual tree yet
    }

    /// <summary>Closes the fly-out, if one is open; back to its group's icon with the keyboard.</summary>
    internal void CloseFlyout(bool focusIcon)
    {
        if (Flyout is not { } flyout)
            return;
        var icon = Rail.Group(flyout.GroupId);
        _flyoutLayer.Children.Remove(flyout);
        Flyout = null;
        _flyoutByHover = false;
        _flyoutHasKeyboard = false;
        AutomationProperties.SetItemStatus(icon, string.Empty);
        if (focusIcon)
            MoveFocus(icon);
    }

    private void FocusInto(NavFlyout flyout)
    {
        // Laid out first, so its buttons can take the focus.
        flyout.UpdateLayout();
        _flyoutHasKeyboard = true;
        MoveFocus(flyout.FocusTarget);
    }

    // ── Hidden: slid in over the page ────────────────────────────────────────

    /// <summary>Slides the hidden sidebar in over the page, from ☰ or the window's left edge.</summary>
    /// <param name="focus">Move the keyboard into it, and keep it there until it closes (☰).</param>
    internal void SlideIn(bool byHover, bool focus)
    {
        CancelHover();
        _slidingByHover = byHover;
        try { _model.SlideIn(keyboard: focus); }
        finally { _slidingByHover = false; }
    }

    // Slid in: over the page, with a shadow, and the keyboard in it when ☰ or Ctrl+B slid it.
    private void ShowSlidIn()
    {
        CancelHover();
        _slideByHover = _slidingByHover;
        SetColumnSpan(_pinned, 2);
        _pinned.Width = SidebarLayout.PinnedWidth;
        _pinned.HorizontalAlignment = HorizontalAlignment.Left;
        Panel.SetZIndex(_pinned, 30);
        _pinned.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black, Direction = 0 };
        // Tab goes round the sidebar's buttons until it closes.
        KeyboardNavigation.SetTabNavigation(_pinned, KeyboardNavigationMode.Cycle);
        _pinned.Visibility = Visibility.Visible;
        _edge.Visibility   = Visibility.Collapsed;
        ShowPinButton();

        if (Animate)
            _slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-SidebarLayout.PinnedWidth, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        if (_model.SlideByKeyboard)
        {
            _pinned.UpdateLayout();
            MoveFocus(Menu.Button(NavMenu.Global[0].Tag));
        }
    }

    /// <summary>Slides the hidden sidebar out again, if it is in; the keyboard goes back to ☰.</summary>
    internal void CloseSlideOver(bool returnFocus) => _model.CloseSlideOver(keyboard: returnFocus);

    // Slid out: back in its column, and the keyboard back to ☰ if it was in it, or Esc, ☰ or Ctrl+B slid it.
    private void ShowSlidOut()
    {
        var focusWasIn = _pinned.IsKeyboardFocusWithin;

        _slideByHover = false;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.X = 0;
        SetColumnSpan(_pinned, 1);
        _pinned.Width = double.NaN;
        _pinned.HorizontalAlignment = HorizontalAlignment.Stretch;
        Panel.SetZIndex(_pinned, 0);
        _pinned.Effect = null;
        KeyboardNavigation.SetTabNavigation(_pinned, KeyboardNavigationMode.Continue);
        _pinned.Visibility = _model.Shown == SidebarMode.Pinned ? Visibility.Visible : Visibility.Collapsed;
        _edge.Visibility   = _model.Shown == SidebarMode.Hidden ? Visibility.Visible : Visibility.Collapsed;
        ShowPinButton();

        if ((_model.SlideByKeyboard || focusWasIn) && ReturnFocusTo is not null)
            MoveFocus(ReturnFocusTo);
    }

    // ── Closing: Esc, a click outside, the pointer leaving ───────────────────

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        var source = e.OriginalSource as DependencyObject;

        // A click in a menu keeps it open after the pointer leaves; a click outside closes it,
        // and goes on to whatever was clicked. A group's icon opens or closes its own.
        if (Flyout is { } flyout)
        {
            if (IsWithin(source, flyout))
                _flyoutByHover = false;
            else if (!Rail.Groups.Any(g => IsWithin(source, g)))
                CloseFlyout(focusIcon: false);
        }
        if (_model.IsSlidOver)
        {
            if (IsWithin(source, _pinned))
                _slideByHover = false;
            else
                CloseSlideOver(returnFocus: false);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || e.Handled)
            return;
        if (Flyout is { } flyout)
        {
            CloseFlyout(focusIcon: _flyoutHasKeyboard || flyout.IsKeyboardFocusWithin);
            e.Handled = true;
        }
        else if (_model.IsSlidOver)
        {
            CloseSlideOver(returnFocus: true);
            e.Handled = true;
        }
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

    // ── The pointer resting, and leaving ─────────────────────────────────────

    private void Schedule(Action open)
    {
        _onHover = open;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void CancelHover()
    {
        _onHover = null;
        _hoverTimer.Stop();
    }

    private void Leave(Action close)
    {
        _onLeave = close;
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    // The pointer came back: what leaving would have closed stays.
    private void StopLeave()
    {
        _onLeave = null;
        _leaveTimer.Stop();
    }

    private static void Fire(ref Action? action, DispatcherTimer timer)
    {
        timer.Stop();
        var run = action;
        action = null;
        run?.Invoke();
    }

    /// <summary>Runs what the pointer resting, or leaving, would after its delay; for tests.</summary>
    internal void ElapseTimers()
    {
        Fire(ref _onHover, _hoverTimer);
        Fire(ref _onLeave, _leaveTimer);
    }

    // ── Building ─────────────────────────────────────────────────────────────

    private Border BuildPinned()
    {
        var name = new TextBlock { Text = "SqlVitals", FontFamily = new FontFamily("Segoe UI"), FontSize = 18, FontWeight = FontWeights.Bold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        VersionText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
        VersionText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var logo = new StackPanel();
        logo.Children.Add(name);
        logo.Children.Add(VersionText);

        PinButton = new Button { Width = 32, Height = 32, FontSize = 14, VerticalAlignment = VerticalAlignment.Top };
        PinButton.SetResourceReference(StyleProperty, "NavRailButton");
        PinButton.Click += PinButton_Click;

        var header = new DockPanel { Margin = new Thickness(16, 18, 8, 4) };
        DockPanel.SetDock(PinButton, Dock.Right);
        header.Children.Add(PinButton);
        header.Children.Add(logo);
        DockPanel.SetDock(header, Dock.Top);

        // Settings, at the foot.
        var footer = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
        DockPanel.SetDock(footer, Dock.Bottom);

        // The global pages, then the open server's (#141).
        var panel  = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = panel, Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        Menu = new NavSidebar(panel, footer);

        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 0, 1, 0), RenderTransform = _slide };
        border.SetResourceReference(Border.BackgroundProperty, "BgSidebar");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        AutomationProperties.SetName(border, "Menu");
        border.MouseEnter += (_, _) => StopLeave();
        border.MouseLeave += (_, _) =>
        {
            if (_model.IsSlidOver && _slideByHover)
                Leave(() => CloseSlideOver(returnFocus: false));
        };
        return border;
    }

    private Border BuildRail()
    {
        // The app's initials where its name is when pinned.
        var mark = new TextBlock { Text = "SV", FontFamily = new FontFamily("Segoe UI"), FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 12) };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        DockPanel.SetDock(mark, Dock.Top);

        var footer = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 10) };
        DockPanel.SetDock(footer, Dock.Bottom);

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        // ↑ and ↓ go from icon to icon.
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Contained);
        var scroll = new ScrollViewer
        {
            Content = panel, Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dock = new DockPanel();
        dock.Children.Add(mark);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        Rail = new NavRail(panel, footer);

        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 0, 1, 0), Visibility = Visibility.Collapsed };
        border.SetResourceReference(Border.BackgroundProperty, "BgSidebar");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        AutomationProperties.SetName(border, "Menu, collapsed");
        return border;
    }
}
