using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>
/// The one bar across the top of the content area (#140, #191): Back and Forward, the breadcrumb,
/// the page's context (its database, whether it is live, a snapshot or history and how fresh) with
/// Pause or Refresh, the message, focus mode (#148), the details drawer (#147) and the sidebar's ☰
/// (#142). The shell fills it in; its buttons ask the shell through the events. <c>TopBar</c> draws
/// it, with the page's own controls and its layout in a narrow window.
/// </summary>
public sealed partial class TopBarViewModel : ObservableObject
{
    public event Action? BackRequested;
    public event Action? ForwardRequested;
    public event Action? RefreshRequested;
    public event Action? DatabaseRequested;

    /// <summary>Pause (true) or resume (false) the live page.</summary>
    public event Action<bool>? PauseRequested;

    /// <summary>A breadcrumb part was clicked: open its route.</summary>
    public event Action<Route>? CrumbRequested;

    /// <summary>Another server was picked from the breadcrumb's server menu (#141).</summary>
    public event Action<Guid>? ServerRequested;

    /// <summary>☰ was clicked: collapse or hide the sidebar, or show the hidden one over the page (#142).</summary>
    public event Action? SidebarRequested;

    /// <summary>The search button was clicked: open the quick switcher (#144).</summary>
    public event Action? JumpRequested;

    /// <summary>Focus, or Exit focus: focus mode on or off (#148), as F11 does.</summary>
    public event Action? FocusRequested;

    /// <summary>The details button: opens or closes the details drawer (#147), as Ctrl+J does.</summary>
    public event Action? DetailsRequested;

    // ── Back and Forward ─────────────────────────────────────────────────────

    /// <summary>Where Back goes, as "Processes · PROD-SQL01"; null when it can't.</summary>
    public string? BackTo { get; private set; }

    /// <summary>Where Forward goes; null when it can't.</summary>
    public string? ForwardTo { get; private set; }

    public string BackToolTip    => BackTo    is null ? "Back (Alt+Left)"     : $"Back to {BackTo} (Alt+Left)";
    public string ForwardToolTip => ForwardTo is null ? "Forward (Alt+Right)" : $"Forward to {ForwardTo} (Alt+Right)";

    /// <summary>The buttons say where they go: "Back to Processes · PROD-SQL01".</summary>
    public void ShowHistory(string? back, string? forward)
    {
        BackTo    = back;
        ForwardTo = forward;
        OnPropertyChanged(nameof(BackTo));
        OnPropertyChanged(nameof(ForwardTo));
        OnPropertyChanged(nameof(BackToolTip));
        OnPropertyChanged(nameof(ForwardToolTip));
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => BackRequested?.Invoke();

    private bool CanGoBack() => BackTo is not null;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => ForwardRequested?.Invoke();

    private bool CanGoForward() => ForwardTo is not null;

    // ── Breadcrumb ───────────────────────────────────────────────────────────

    /// <summary>All servers › PROD-SQL01 › Activity now › Processes.</summary>
    public IReadOnlyList<Crumb> Crumbs { get; private set; } = [];

    /// <summary>A server's selector row, for the server part's health dot and score.</summary>
    public Func<Guid, ConnectionSelectorItem?> ServerOf { get; private set; } = _ => null;

    /// <summary>Every saved server, in the order the server menu lists them.</summary>
    public IReadOnlyList<ConnectionSelectorItem> Servers { get; private set; } = [];

    /// <summary>The breadcrumb's text: "All servers › PROD-SQL01 › Processes".</summary>
    public string BreadcrumbText => string.Join(" › ", Crumbs.Select(c => c.Text));

    /// <summary>
    /// Where you are; again with the same parts when the server's health changed, which the bar
    /// shows in place.
    /// </summary>
    public void ShowBreadcrumb(IReadOnlyList<Crumb> crumbs, Func<Guid, ConnectionSelectorItem?> server,
                               IReadOnlyList<ConnectionSelectorItem>? servers = null)
    {
        Crumbs   = crumbs;
        ServerOf = server;
        Servers  = servers ?? [];
        OnPropertyChanged(nameof(Crumbs));
        OnPropertyChanged(nameof(BreadcrumbText));
    }

    /// <summary>A breadcrumb part that opens a page, or All servers in the server menu.</summary>
    public void OpenCrumb(Route route) => CrumbRequested?.Invoke(route);

    /// <summary>Another server from the server menu; the open one does nothing.</summary>
    public void SwitchServer(Guid server, Guid current)
    {
        if (server != current)
            ServerRequested?.Invoke(server);
    }

    // ── Page context ─────────────────────────────────────────────────────────

    /// <summary>The page's kind and freshness; null before the first page.</summary>
    public PageFreshness? Freshness { get; private set; }

    /// <summary>A live page whose updates can be paused from the bar.</summary>
    public bool CanPause { get; private set; }

    /// <summary>The database the page reads; null for none.</summary>
    public string? Database { get; private set; }

    /// <summary>Pause or Resume shows: a live page that can pause.</summary>
    public bool ShowsPause => CanPause && Freshness is { Kind: PageKind.Live };

    /// <summary>Refresh shows: beside a paused live page, and for every other page.</summary>
    public bool ShowsRefresh => !ShowsPause || Freshness!.Paused;

    public bool IsPaused => Freshness is { Paused: true };

    /// <summary>
    /// A new page: its context as <see cref="ShowContext"/>, and the message cleared. Its own
    /// controls go to the bar's view.
    /// </summary>
    public void ShowPage(PageFreshness? freshness, bool canPause, string? database)
    {
        ShowMessage(null);
        ShowContext(freshness, canPause, database);
    }

    /// <summary>
    /// The page on screen updated, paused, or changed kind: its kind and freshness (null for no
    /// page yet), whether it can pause, and the database it reads (null for none).
    /// </summary>
    public void ShowContext(PageFreshness? freshness, bool canPause, string? database)
    {
        Freshness = freshness;
        CanPause  = canPause;
        Database  = database;
        OnPropertyChanged(nameof(Database));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(ShowsPause));
        OnPropertyChanged(nameof(ShowsRefresh));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(Freshness));
    }

    [RelayCommand]
    private void Pause() => PauseRequested?.Invoke(!IsPaused);

    /// <summary>Refresh can be pressed: false while the page loads, and for a page with nothing to read.</summary>
    public bool CanRefresh
    {
        get => _canRefresh;
        set
        {
            if (!SetProperty(ref _canRefresh, value))
                return;
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    private bool _canRefresh;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh() => RefreshRequested?.Invoke();

    [RelayCommand]
    private void OpenDatabase() => DatabaseRequested?.Invoke();

    // ── Messages ─────────────────────────────────────────────────────────────

    /// <summary>"Loading…", "Switched to PROD-SQL02.", or an error the page ran into; empty for none.</summary>
    public string Message { get; private set; } = string.Empty;

    public bool MessageIsError { get; private set; }

    /// <summary>Shows a message on the bar; null clears it.</summary>
    public void ShowMessage(string? text, bool isError = false)
    {
        Message        = text ?? string.Empty;
        MessageIsError = isError && Message.Length > 0;
        OnPropertyChanged(nameof(MessageIsError));
        // Raised every time: a screen reader hears the same message again.
        OnPropertyChanged(nameof(Message));
    }

    // ── The sidebar's ☰ (#142), focus mode (#148), the drawer (#147), the switcher (#144) ──

    /// <summary>The sidebar's state on screen, for what ☰ does next.</summary>
    public SidebarMode Sidebar { get; private set; }

    /// <summary>The hidden sidebar is slid in over the page.</summary>
    public bool SidebarSlidOver { get; private set; }

    /// <summary>The active alerts, counted on ☰ while the sidebar is hidden, since its own count is out of sight.</summary>
    public int Alerts { get; private set; }

    /// <summary>What ☰ does next, spoken: "Collapse the menu".</summary>
    public string SidebarName => SidebarWords().Name;

    /// <summary>What ☰ does next, as its tooltip, with the alerts while it carries their count.</summary>
    public string SidebarToolTip => SidebarCounts ? $"{SidebarWords().Tip}\n{AlertsText}" : SidebarWords().Tip;

    /// <summary>☰'s spoken name, with the alerts while it carries their count.</summary>
    public string SidebarAutomationName => SidebarCounts ? $"{SidebarName} ({AlertsText})" : SidebarName;

    /// <summary>☰ carries the alerts' count: the sidebar is hidden and some are active.</summary>
    public bool SidebarCounts => Sidebar == SidebarMode.Hidden && Alerts > 0;

    /// <summary>The count on ☰: "99+" past 99.</summary>
    public string SidebarBadge => Alerts > 99 ? "99+" : Alerts.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private string AlertsText => Alerts == 1 ? "1 active alert" : $"{Alerts} active alerts";

    private (string Name, string Tip) SidebarWords() => Sidebar switch
    {
        SidebarMode.Pinned    => ("Collapse the menu", "Collapse the menu to icons (Ctrl+B)"),
        SidebarMode.Collapsed => ("Hide the menu", "Hide the menu (Ctrl+B)"),
        _ when SidebarSlidOver => ("Close the menu", "Close the menu (Esc). Ctrl+B pins it open."),
        _                      => ("Show the menu", "Show the menu over the page. Ctrl+B pins it open."),
    };

    /// <summary>What ☰ does next, and, while the sidebar is hidden, the active alerts' count on it.</summary>
    public void ShowSidebar(SidebarMode shown, bool slidOver, int alerts)
    {
        Sidebar         = shown;
        SidebarSlidOver = slidOver;
        Alerts          = alerts;
        foreach (var name in new[] { nameof(Sidebar), nameof(SidebarSlidOver), nameof(Alerts), nameof(SidebarName),
                                     nameof(SidebarToolTip), nameof(SidebarAutomationName), nameof(SidebarCounts), nameof(SidebarBadge) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarRequested?.Invoke();

    [RelayCommand]
    private void Jump() => JumpRequested?.Invoke();

    /// <summary>In focus mode the Focus button gives way to Exit focus (Esc).</summary>
    [ObservableProperty]
    private bool _inFocus;

    [RelayCommand]
    private void ToggleFocus() => FocusRequested?.Invoke();

    /// <summary>The details drawer is open: the details button closes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsToolTip), nameof(DetailsName))]
    private bool _detailsOpen;

    public string DetailsToolTip => DetailsOpen ? "Hide the details (Ctrl+J)" : "Show the selected row's details (Ctrl+J)";
    public string DetailsName    => DetailsOpen ? "Hide details" : "Show details";

    [RelayCommand]
    private void ToggleDetails() => DetailsRequested?.Invoke();
}
