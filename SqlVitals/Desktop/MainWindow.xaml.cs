using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Shell;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop;

/// <summary>
/// The app's window. Its logic is the <see cref="ShellViewModel"/> (#191), its DataContext, with the
/// top bar, the sidebar and the quick switcher; the window keeps what needs a window: the page in
/// its frame (<see cref="IPageHost"/>), the tray icon, its placement and focus mode's full screen,
/// coming back when a second copy starts, and the keys that work anywhere in it.
/// </summary>
[SuppressMessage("Design", "CA1001", Justification = "A Window isn't IDisposable; OnClosing disposes _tray")]
public partial class MainWindow : Window, IPageHost
{
    private readonly ShellViewModel _shell;
    private readonly ConnectionSettingsService _settings;

    // The notification-area icon: Windows notifications for alerts, and the way back to the
    // window while it's hidden (#35).
    private readonly TrayIcon _tray;

    // What restoring from the tray goes back to: normal or maximized.
    private WindowState _restoreState = WindowState.Normal;

    /// <summary>The app's window, made by the container (#166) with the shell it shows.</summary>
    public MainWindow(ShellViewModel shell, ConnectionSettingsService settings)
    {
        _shell    = shell;
        _settings = settings;
        DataContext = shell;
        InitializeComponent();

        Bar.ViewModel      = shell.TopBar;
        Shell.ViewModel    = shell.Sidebar;
        Switcher.ViewModel = shell.Switcher;
        Loading.ViewModel  = shell.LoadingBar;
        shell.Attach(this);
        shell.PropertyChanged += OnShellChanged;

        // Pages are recreated on each navigation, but the Frame journal would otherwise keep
        // the old instances — and the mouse Back button could bring back a page still showing
        // data from the previous database.
        MainFrame.Navigated += (_, _) =>
        {
            while (MainFrame.CanGoBack)
                MainFrame.RemoveBackEntry();
        };

        // The quick switcher (#144): a menu the sidebar has open, and the score, close first; the
        // keyboard goes into the page it opened if it was in the one replaced, unless something
        // else has taken it while the page loaded.
        Switcher.PageHost = MainFrame;
        shell.SwitcherOpening += () =>
        {
            Shell.CloseFlyout(focusIcon: false);
            ScorePopup.IsOpen = false;
        };
        shell.FocusIntoPageRequested += () =>
        {
            if (Keyboard.FocusedElement is not DependencyObject focused || ReferenceEquals(focused, this) || Switcher.IsAncestorOf(focused))
                MainFrame.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };

        // A page opened as an alert's evidence (#145): Show all clears its filter where it is, and the
        // banner's history link opens the page that charts the alert's window.
        FocusBar.ShowAllRequested += shell.ShowAllEvidence;
        FocusBar.HistoryRequested += async () => await shell.OpenEvidenceHistoryAsync();

        // The sidebar (#142): ☰ and Ctrl+B change it, and give the keyboard back to ☰; collapsed in a
        // narrow window. The score in its server header opens the breakdown beside it (#36).
        Shell.ReturnFocusTo   = Bar.BtnSidebar;
        Shell.ScoreRequested += ShowScore;
        SizeChanged += (_, e) => shell.Sidebar.SetWindowWidth(e.NewSize.Width);

        _tray = new TrayIcon(settings);
        _tray.OpenRequested   += ShowFromTray;
        // The tray menu's Alerts opens every server's; a notification opens its server's workspace (#141).
        _tray.AlertsRequested += async connectionId =>
        {
            ShowFromTray();
            await shell.OpenAlertsAsync(connectionId);
        };
        // A notification about one alert opens its evidence (#145).
        _tray.EvidenceRequested += async alert =>
        {
            ShowFromTray();
            await shell.InvestigateAsync(alert, ActivitySources.Notification);
        };
        _tray.ExitRequested     += ExitApp;
        _tray.StartAllRequested += shell.StartAll;
        _tray.StopAllRequested  += shell.StopAll;
        shell.AlertNotified += OnAlertNotified;
        shell.Informed      += OnInformed;

        var store = shell.Start(Width);

        // Every grid's density, as chosen in Settings → Appearance (#148).
        if (Application.Current is { } app)
            GridDensityResources.Apply(app.Resources, store.GridDensity);

        // The details drawer (#147): pinned or floating and its width as last left; closed at start.
        Details.Layout = new DrawerLayout(store.DrawerPinned, store.DrawerWidth);
        Details.PreferencesChanged += SaveDrawer;
        Details.StateChanged       += () => shell.TopBar.DetailsOpen = Details.State != DrawerState.Closed;
        shell.DetailsRequested     += Details.Toggle;

        // An installation from before #175 kept query text in full: its user chooses once, when
        // the window is first on screen (not while it starts in the tray).
        if (shell.AsksQueryTextStorage)
            ContentRendered += AskQueryTextStorageOnce;

        Loaded += async (_, _) => await shell.OpenFirstPageAsync();
    }

    // What the shell says that needs the window: focus mode, the evidence banner, the score, the tray icon.
    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.InFocus):
                if (_shell.InFocus) EnterFullScreen();
                else                LeaveFullScreen();
                break;
            case nameof(ShellViewModel.FocusBanner):
                FocusBar.Show(_shell.FocusBanner.Text, _shell.FocusBanner.ShowAll);
                break;
            case nameof(ShellViewModel.Score) when _shell.Score is { } score:
                ScoreBreakdown.Show(score.Name, score.Score, score.NoScore, score.Paused);
                break;
            case nameof(ShellViewModel.ActiveAlerts):
                _tray.ShowActiveAlerts(_shell.ActiveAlerts);
                break;
            case nameof(ShellViewModel.MonitoredCount):
                _tray.ShowMonitoring(_shell.MonitoredCount);
                break;
        }
    }

    // ── The page (IPageHost) ──────────────────────────────────────────────────

    // The page on screen, and how each recent route's page was being looked at, put back when it's opened again (#139).
    private Page? _page;
    private readonly RouteStates<ViewState> _viewStates = new();

    public ShownPage Show(PageInstance page, PageFocus? focus)
    {
        var view = page.View;
        System.Diagnostics.Debug.Assert(PageCatalog.Types.GetValueOrDefault(page.ViewModel.Tag) == view.GetType(),
                                        $"PageCatalog says \"{page.ViewModel.Tag}\" opens another type than {view.GetType().Name}.");

        // A banner hidden on the page left shows again on the next page opened as evidence.
        FocusBar.NewPage();

        // The page's own controls (a time range picker) go into the top bar before it loads; the
        // previous page's go (#140).
        var context = view as IPageContext;
        FrameworkElement? tools = null;
        try { tools = context?.TakeBarTools(); }
        catch (Exception ex) { AppLog.Error($"{page.ViewModel.Tag}: could not move its controls to the top bar", ex); }

        // Opened as evidence (#145): filtered to the problem, or set to the alert's window, from its
        // first load on.
        var focusPage = view as IFocusPage;
        if (focus is not null && focusPage is not null)
        {
            try { focusPage.ShowFocus(focus); }
            catch (Exception ex) { AppLog.Error($"{page.ViewModel.Tag}: could not apply its filter", ex); }
        }

        MainFrame.Navigate(view);
        _page = view;
        ShowDetailsOf(view);
        Bar.ShowTools(tools);
        return new ShownPage(context, focusPage);
    }

    public void KeepViewState(Route route)
    {
        if (_page is not null)
            _viewStates.Save(route, ViewState.Capture(_page, _viewStates.Get(route)));
    }

    public void RestoreViewState(Route route, bool scrolling)
    {
        if (_page is not { } page || _viewStates.Get(route) is not { } state)
            return;
        try
        {
            if (scrolling) state.RestoreScrolling(page);
            else           state.Restore(page);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{route.Tag}: could not put back the view state", ex);
        }
    }

    // ── The details drawer (#147) ─────────────────────────────────────────────

    // The page filling the drawer, while it is the page on screen.
    private IDetailsSource? _detailsSource;

    // A new page: the drawer shows its details, or says it has none; it stays open or closed.
    private void ShowDetailsOf(object page)
    {
        _detailsSource?.DetailsChanged -= OnDetailsChanged;
        _detailsSource = page as IDetailsSource;
        _detailsSource?.DetailsChanged += OnDetailsChanged;
        Details.Show(_detailsSource?.Details ?? DrawerContent.None);
    }

    // The page's selection changed; a click on a row opens a closed drawer.
    private void OnDetailsChanged(bool open)
    {
        if (_detailsSource is null)
            return;
        Details.Show(_detailsSource.Details);
        if (open)
            Details.Open();
    }

    // A layout preference: saved, but not in the activity log.
    private void SaveDrawer(bool pinned, double width)
    {
        try { _settings.SetDrawer(pinned, width); }
        catch (Exception ex) { AppLog.Error("Details drawer: could not save whether it is pinned, or its width", ex); }
    }

    // ── Health score (#36) ────────────────────────────────────────────────────

    // The score in the server header, or under its dot in the collapsed sidebar, opens its breakdown beside it.
    private void ShowScore(FrameworkElement anchor)
    {
        if (!_shell.ShowWorkspaceScore())
            return;
        ScorePopup.PlacementTarget = anchor;
        ScorePopup.IsOpen          = true;
    }

    private void ScorePopup_Closed(object? sender, EventArgs e) => _shell.CloseScore();

    // ── Focus mode (#148) ─────────────────────────────────────────────────────

    // The window as it was before focus mode; null when not in it.
    private WindowPlacement? _beforeFocus;

    // Full screen; a floating drawer closes, a pinned one stays.
    private void EnterFullScreen()
    {
        _beforeFocus = WindowPlacement.Capture(this);
        if (Details.State == DrawerState.Floating)
            Details.Close();
        _beforeFocus.FullScreen().ApplyTo(this);
    }

    // Back to the window as it was.
    private void LeaveFullScreen()
    {
        if (_beforeFocus is not { } before)
            return;
        _beforeFocus = null;
        before.ApplyTo(this);
    }

    // ── Notification area (#35) ───────────────────────────────────────────────

    private void OnAlertNotified(AlertNotification notification) => _tray.Notify(notification);

    private void OnInformed(string title, string text, bool warning) => _tray.Inform(title, text, warning);

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState != WindowState.Minimized)
            _restoreState = WindowState;
        else if (_shell.KeepRunningInTray)
            HideToTray();
    }

    private void AskQueryTextStorageOnce(object? sender, EventArgs e)
    {
        ContentRendered -= AskQueryTextStorageOnce;
        _shell.AskQueryTextStorage();
    }

    // Monitoring, history and alerts carry on; only the window goes. Windows it owns (a plan or
    // script window) hide and come back with it.
    private void HideToTray()
    {
        Hide();
        if (_shell.TrayHint() is { } hint)
            _tray.Inform("SqlVitals is still running", hint);
    }

    /// <summary>Brings the window back from the tray (or from behind other windows) as it was.</summary>
    internal void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Show();
        Activate();
    }

    // Exit in the tray menu: quit for real, with the usual clean-up in OnClosing.
    private void ExitApp()
    {
        ((App)Application.Current).IsExiting = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window only hides it, unless the user asked to exit or Windows is ending
        // the session.
        if (!((App)Application.Current).IsExiting && _shell.KeepRunningInTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // A notification still to come mustn't reach the disposed icon.
        _shell.AlertNotified -= OnAlertNotified;
        _shell.Informed      -= OnInformed;
        _tray.Dispose();

        // Why, for the chat channels' "stopped watching" (#170).
        _shell.Shutdown(((App)Application.Current).SessionEndingReason switch
        {
            ReasonSessionEnding.Shutdown => WatchStop.WindowsShutdown,
            ReasonSessionEnding.Logoff   => WatchStop.WindowsSignOut,
            _                            => WatchStop.AppExit,
        });

        base.OnClosing(e);
    }

    // ── Keys ──────────────────────────────────────────────────────────────────

    // Alt+← / Alt+→ and the keyboard's own Back and Forward keys, before the Frame's built-in
    // journal commands see them (its journal is kept empty). F5 refreshes the page (#140), rather
    // than the Frame reloading it. Ctrl+B pins, collapses or hides the sidebar (#142). Ctrl+K opens
    // or closes the quick switcher (#144); while it is open, the other keys are its own.
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_shell.Switcher.IsOpen)
                Switcher.Close(restoreFocus: true);
            else
                _shell.OpenSwitcher();
            return;
        }
        if (_shell.Switcher.IsOpen)
            return;

        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await _shell.RefreshAsync();
            return;
        }

        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _shell.Sidebar.Cycle();
            return;
        }

        // F11: focus mode on or off (#148).
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _shell.ToggleFocus();
            return;
        }

        // Ctrl+J opens or closes the details drawer (#147).
        if (e.Key == Key.J && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Details.Toggle();
            return;
        }

        bool? back = e.Key switch
        {
            Key.BrowserBack    => true,
            Key.BrowserForward => false,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left  => true,
            Key.System when Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Right => false,
            _ => null,
        };
        if (back is null)
            return;
        e.Handled = true;
        await _shell.GoBackOrForwardAsync(back.Value);
    }

    // Esc closes a floating details drawer (#147), once nothing in the page has used it: a filter
    // box clearing its text, a grid cancelling an edit, a pop-up closing come first. Then focus mode (#148).
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None || _shell.Switcher.IsOpen)
            return;
        if (Details.Escape() || _shell.ExitFocus())
            e.Handled = true;
    }

    // The mouse's back and forward buttons.
    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2) || _shell.Switcher.IsOpen)
            return;
        e.Handled = true;
        await _shell.GoBackOrForwardAsync(back: e.ChangedButton == MouseButton.XButton1);
    }
}
