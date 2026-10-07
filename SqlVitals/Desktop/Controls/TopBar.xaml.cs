using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The one bar across the top of the content area (#140): Back and Forward, the breadcrumb, and
/// the page's context: its database, its own controls (a time range picker), whether it is live,
/// a snapshot or history and how fresh, then Pause or Refresh. MainWindow fills it in.
/// </summary>
public partial class TopBar : UserControl
{
    // A snapshot's age moves on while it is on screen: "2 min ago", then amber at 15 minutes.
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    private PageFreshness? _freshness;
    private bool _canPause;

    public TopBar()
    {
        InitializeComponent();
        _ageTimer.Tick += (_, _) => ShowFreshness();
        Unloaded += (_, _) => _ageTimer.Stop();
    }

    /// <summary>The clock the snapshot's age is measured on; a fake one in tests.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    private DateTime Now => Clock.GetLocalNow().DateTime;

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

    // ── The sidebar's ☰ (#142) ───────────────────────────────────────────────

    /// <summary>
    /// What ☰ does next, as its tooltip and spoken name, and, while the sidebar is hidden, the
    /// active alerts' count on it, since the sidebar's own count is out of sight.
    /// </summary>
    public void ShowSidebar(SidebarMode shown, bool slidOver, int alerts)
    {
        var (name, tip) = shown switch
        {
            SidebarMode.Pinned    => ("Collapse the menu", "Collapse the menu to icons (Ctrl+B)"),
            SidebarMode.Collapsed => ("Hide the menu", "Hide the menu (Ctrl+B)"),
            _ when slidOver       => ("Close the menu", "Close the menu (Esc). Ctrl+B pins it open."),
            _                     => ("Show the menu", "Show the menu over the page. Ctrl+B pins it open."),
        };

        var counted = shown == SidebarMode.Hidden && alerts > 0;
        SidebarBadge.Visibility = counted ? Visibility.Visible : Visibility.Collapsed;
        TxtSidebarBadge.Text    = alerts > 99 ? "99+" : alerts.ToString(CultureInfo.CurrentCulture);
        var count = alerts == 1 ? "1 active alert" : $"{alerts} active alerts";
        BtnSidebar.ToolTip = counted ? $"{tip}\n{count}" : tip;
        AutomationProperties.SetName(BtnSidebar, counted ? $"{name} ({count})" : name);
    }

    private void BtnSidebar_Click(object sender, RoutedEventArgs e) => SidebarRequested?.Invoke();

    private void BtnJump_Click(object sender, RoutedEventArgs e) => JumpRequested?.Invoke();

    /// <summary>Focus, or Exit focus: focus mode on or off (#148), as F11 does.</summary>
    public event Action? FocusRequested;

    private void BtnFocus_Click(object sender, RoutedEventArgs e) => FocusRequested?.Invoke();

    /// <summary>In focus mode the Focus button gives way to Exit focus (Esc).</summary>
    public void ShowFocus(bool on)
    {
        BtnFocus.Visibility     = on ? Visibility.Collapsed : Visibility.Visible;
        BtnExitFocus.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The details button: opens or closes the details drawer (#147), as Ctrl+J does.</summary>
    public event Action? DetailsRequested;

    private void BtnDetails_Click(object sender, RoutedEventArgs e) => DetailsRequested?.Invoke();

    /// <summary>Says whether the details button opens or closes the drawer.</summary>
    public void ShowDetailsOpen(bool open)
    {
        BtnDetails.ToolTip = open ? "Hide the details (Ctrl+J)" : "Show the selected row's details (Ctrl+J)";
        System.Windows.Automation.AutomationProperties.SetName(BtnDetails, open ? "Hide details" : "Show details");
    }

    // ── Back and Forward ─────────────────────────────────────────────────────

    /// <summary>The buttons say where they go: "Back to Processes · PROD-SQL01".</summary>
    public void ShowHistory(string? back, string? forward)
    {
        BtnBack.IsEnabled    = back is not null;
        BtnForward.IsEnabled = forward is not null;
        BtnBack.ToolTip      = back    is null ? "Back (Alt+Left)"     : $"Back to {back} (Alt+Left)";
        BtnForward.ToolTip   = forward is null ? "Forward (Alt+Right)" : $"Forward to {forward} (Alt+Right)";
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)    => BackRequested?.Invoke();
    private void BtnForward_Click(object sender, RoutedEventArgs e) => ForwardRequested?.Invoke();

    // ── Breadcrumb ───────────────────────────────────────────────────────────

    /// <summary>The breadcrumb's text, for tests and the window's own description of where it is.</summary>
    internal string BreadcrumbText { get; private set; } = string.Empty;

    // The breadcrumb on show, and its server part's health, which a new sample updates in place:
    // rebuilding the parts would take the keyboard focus off one.
    private IReadOnlyList<Crumb> _crumbs = [];
    private (Ellipse Dot, TextBlock Score, FrameworkElement Part, Crumb Crumb)? _serverPart;

    // The saved servers, for the server part's menu: the latest list, read when the menu opens.
    private IReadOnlyList<ConnectionSelectorItem> _servers = [];

    /// <param name="server">A server's selector row, for its health dot and score.</param>
    /// <param name="servers">Every saved server, in the order the server menu lists them.</param>
    public void ShowBreadcrumb(IReadOnlyList<Crumb> crumbs, Func<Guid, ConnectionSelectorItem?> server,
                               IReadOnlyList<ConnectionSelectorItem>? servers = null)
    {
        _servers = servers ?? [];
        if (crumbs.SequenceEqual(_crumbs) && _serverPart is { Crumb.ServerId: { } shownId } shown && server(shownId) is { } item)
        {
            ShowHealth(shown.Dot, shown.Score, shown.Part, shown.Crumb, item);
            return;
        }

        _crumbs = crumbs;
        _serverPart = null;
        Crumbs.Children.Clear();
        foreach (var crumb in crumbs)
        {
            var part = CrumbElement(crumb, crumb.ServerId is { } id ? server(id) : null);
            CrumbPanel.SetCanDrop(part, crumb.CanDrop);
            Crumbs.Children.Add(part);
        }
        BreadcrumbText = string.Join(" › ", crumbs.Select(c => c.Text));
    }

    private FrameworkElement CrumbElement(Crumb crumb, ConnectionSelectorItem? server)
    {
        var text = new TextBlock { Text = crumb.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement content = text;
        (Ellipse Dot, TextBlock Score)? health = null;

        // The server: its health dot and score, as in the selector, and its health in words. Short
        // of room its name trims, rather than the dot or the score going.
        if (server is not null)
        {
            var row = new DockPanel();
            var dot = new Ellipse
            {
                Width = 8, Height = 8, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var score = new TextBlock { FontWeight = FontWeights.Bold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(score, Dock.Right);
            row.Children.Add(dot);
            row.Children.Add(score);
            text.FontWeight = FontWeights.SemiBold;
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            row.Children.Add(text);
            content = row;
            health = (dot, score);
        }

        FrameworkElement part;
        if (server is not null)
        {
            // The server: a menu of the saved servers, to switch to another (#141).
            var chevron = new TextBlock { Text = "\uE70D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(chevron, Dock.Right);
            var row = (DockPanel)content;
            row.Children.Insert(0, chevron);
            var button = new Button { Content = content, Style = (Style)FindResource("CrumbButton") };
            button.Click += (_, _) => OpenServerMenu(button, crumb.ServerId!.Value);
            part = button;
        }
        else if (crumb.Target is not { } target)
        {
            // The page on screen.
            if (server is null)
            {
                text.FontWeight = FontWeights.SemiBold;
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            }
            content.Margin = new Thickness(4, 0, 4, 0);
            part = content;
        }
        else
        {
            var button = new Button { Content = content, Style = (Style)FindResource("CrumbButton"), ToolTip = $"Open {crumb.Text}" };
            button.Click += (_, _) => CrumbRequested?.Invoke(target);
            part = button;
        }

        AutomationProperties.SetName(part, crumb.Text);
        if (health is { } h)
        {
            _serverPart = (h.Dot, h.Score, part, crumb);
            ShowHealth(h.Dot, h.Score, part, crumb, server!);
        }
        return part;
    }

    // The server part's dot, score, spoken name and tooltip, from its selector row.
    private static void ShowHealth(Ellipse dot, TextBlock score, FrameworkElement part, Crumb crumb, ConnectionSelectorItem server)
    {
        dot.Fill   = server.HealthFill;
        dot.Stroke = server.HealthStroke;
        score.Text       = server.ScoreText;
        score.Foreground = server.ScoreBrush;
        score.Visibility = server.Score is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(part, server.Score is { } s ? $"{crumb.Text}, health score {s.Value}"
                                         : server.HealthText.Length > 0 ? $"{crumb.Text}, {server.HealthText}"
                                         : crumb.Text);
        AutomationProperties.SetName(part, AutomationProperties.GetName(part) + ". Switch server");
        part.ToolTip = $"{server.ToolTipText}\nClick to switch to another server.";
    }

    /// <summary>The server menu, while it is open; for tests.</summary>
    internal ContextMenu? ServerMenu { get; private set; }

    // Every saved server with its health, the open one ticked, then All servers.
    private void OpenServerMenu(FrameworkElement anchor, Guid current)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var server in _servers)
        {
            var id   = server.Settings.Id;
            var dot  = new Ellipse { Width = 8, Height = 8, StrokeThickness = 1.5, Fill = server.HealthFill, Stroke = server.HealthStroke, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = server.DisplayName, VerticalAlignment = VerticalAlignment.Center };
            var score = new TextBlock { Text = server.ScoreText, Foreground = server.ScoreBrush, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var header = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(score, Dock.Right);
            header.Children.Add(dot);
            header.Children.Add(score);
            header.Children.Add(name);

            var item = new MenuItem { Header = header, IsCheckable = false, IsChecked = id == current, ToolTip = server.ToolTipText };
            AutomationProperties.SetName(item, server.Score is { } s ? $"{server.DisplayName}, health score {s.Value}" : server.DisplayName);
            item.Click += (_, _) => { if (id != current) ServerRequested?.Invoke(id); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = Breadcrumb.AllServers };
        all.Click += (_, _) => CrumbRequested?.Invoke(new Route("Home"));
        menu.Items.Add(all);

        menu.Closed += (_, _) => ServerMenu = null;
        ServerMenu = menu;
        menu.IsOpen = true;
    }

    // ── Page context ─────────────────────────────────────────────────────────

    /// <summary>
    /// A new page: the controls it hands to the bar (the previous page's go with it), then its
    /// context as <see cref="ShowContext"/>. The message is cleared.
    /// </summary>
    public void ShowPage(FrameworkElement? tools, PageFreshness? freshness, bool canPause, string? database)
    {
        // In the bar to begin with; the layout moves them to the row below if there's no room.
        ToolsRow.Content    = null;
        ToolsRow.Visibility = Visibility.Collapsed;
        ToolsHost.Content    = tools;
        ToolsHost.Visibility = tools is null ? Visibility.Collapsed : Visibility.Visible;
        _tools = tools;
        ShowMessage(null);
        ShowContext(freshness, canPause, database);
    }

    /// <summary>
    /// The page on screen updated, paused, or changed kind: its kind and freshness (null for no
    /// page yet), whether it can pause, and the database it reads (null for none).
    /// </summary>
    public void ShowContext(PageFreshness? freshness, bool canPause, string? database)
    {
        BtnDatabase.Visibility = database is null ? Visibility.Collapsed : Visibility.Visible;
        if (database is not null)
        {
            RunDatabase.Text = database;
            BtnDatabase.ToolTip = $"This page reads the connection's database, {database}. Click to change it in the connection's settings.";
            AutomationProperties.SetName(BtnDatabase, $"Database: {database}");
        }

        _canPause  = canPause;
        _freshness = freshness;
        ShowFreshness();

        var any = freshness is { Kind: not PageKind.None } || _tools is not null || database is not null;
        ContextPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the bar says now, for tests.</summary>
    internal PageFreshness? Freshness => _freshness;

    private void ShowFreshness()
    {
        var f = _freshness;
        var kind = f?.Kind ?? PageKind.None;
        var now = Now;

        Badge.Visibility = kind == PageKind.None ? Visibility.Collapsed : Visibility.Visible;
        _detailWanted    = kind != PageKind.None;
        if (!_detailWanted)
            TxtDetail.Visibility = Visibility.Collapsed;
        InvalidateMeasure();
        if (f is null || kind == PageKind.None)
        {
            _ageTimer.Stop();
            UpdatePauseButton(null);
            return;
        }

        var (text, fill, border) = kind switch
        {
            PageKind.Live     => ("LiveBadgeText",     "LiveBadgeFill",     "LiveBadgeBorder"),
            PageKind.Snapshot => ("SnapshotBadgeText", "SnapshotBadgeFill", "SnapshotBadgeBorder"),
            _                 => ("HistoryBadgeText",  "HistoryBadgeFill",  "HistoryBadgeBorder"),
        };
        // Paused is still a live page, shown muted: its data has stopped moving.
        if (f.Paused)
            (text, fill, border) = ("TextMuted", "BgCard", "Border");

        TxtBadge.SetResourceReference(TextBlock.ForegroundProperty, text);
        Badge.SetResourceReference(Border.BackgroundProperty, fill);
        Badge.SetResourceReference(Border.BorderBrushProperty, border);
        BadgeDot.SetResourceReference(Shape.FillProperty, text);
        BadgeDot.Visibility = kind == PageKind.Live ? Visibility.Visible : Visibility.Collapsed;
        TxtBadge.Text = PageContextText.Badge(f);

        // Read out in words, the age included: the colours only repeat it.
        var spoken = PageContextText.AutomationName(f, now);
        AutomationProperties.SetName(TxtBadge, spoken);
        Badge.ToolTip = spoken;

        TxtDetail.Text = PageContextText.Detail(f, now);
        TxtDetail.ToolTip = TxtDetail.Text.Length > 0 ? spoken : null;
        AutomationProperties.SetName(TxtDetail, TxtDetail.Text);
        var stale = PageContextText.IsStale(f, now);
        TxtDetail.SetResourceReference(TextBlock.ForegroundProperty, stale ? "StaleText" : "TextMuted");
        TxtDetail.FontWeight = stale ? FontWeights.SemiBold : FontWeights.Normal;

        if (kind == PageKind.Snapshot && f.Taken is not null)
            _ageTimer.Start();
        else
            _ageTimer.Stop();

        UpdatePauseButton(f);
    }

    // Short of room, the bar gives things up before the breadcrumb's server's or page's name
    // trims: first the freshness text (the badge's tooltip and spoken name still say it), then Jump
    // (Ctrl+K still opens it) and Ask AI, which does nothing yet; if that isn't enough, the page's
    // own controls move to a row below instead, and the rest comes back if it fits. All of it is worked out from the widths
    // at once, so another layout pass at the same width changes nothing.
    private bool _detailWanted;
    private FrameworkElement? _tools;

    private Button[] Placeholders => [BtnJump, BtnAskAi];

    /// <summary>The page's controls are in the row under the bar, for want of room in it.</summary>
    internal bool ToolsWrapped => ToolsRow.Content is not null;

    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        if (double.IsInfinity(constraint.Width))
            return size;

        // The room the breadcrumb's server and page would have with everything shown: the bar's
        // width less what the rest takes at its own width now, less what is hidden now. Then what
        // to give up, in order, so that they fit.
        var detail       = _detailWanted ? DetailWidth() : 0;
        var placeholders = PlaceholdersWidth();
        var tools        = _tools is null ? 0 : ToolsWidth();
        var room = constraint.Width - Root.Padding.Left - Root.Padding.Right - Root.BorderThickness.Left - Root.BorderThickness.Right;
        var spare = room - Natural(LeftButtons) - Natural(RightButtons) - Natural(ContextPanel) - Crumbs.KeptWidth
                  - (TxtDetail.Visibility == Visibility.Visible ? 0 : detail)
                  - (BtnJump.Visibility == Visibility.Visible ? 0 : placeholders)
                  - (ToolsWrapped ? tools : 0);

        // The controls wrap only if giving up the rest isn't enough; once they have, the rest
        // may well fit beside the breadcrumb again.
        var wrapTools        = spare + detail + placeholders < 0 && _tools is not null;
        if (wrapTools)
            spare += tools;
        var showDetail       = spare >= 0;
        var showPlaceholders = spare + detail >= 0;

        var changed = SetVisible(TxtDetail, showDetail && _detailWanted);
        foreach (var button in Placeholders)
            changed |= SetVisible(button, showPlaceholders);
        if (wrapTools != ToolsWrapped)
        {
            WrapTools(wrapTools);
            changed = true;
        }
        if (!changed)
            return size;

        // A panel asked again for the same size keeps its last answer, so the ones holding what
        // changed, and everything up to this control's own template, are told to measure afresh.
        foreach (var element in new UIElement[] { ToolsHost, ToolsRow, ContextPanel, LeftButtons, RightButtons, Crumbs, BarRow, Rows })
            element.InvalidateMeasure();
        for (DependencyObject? e = Root; e is not null && !ReferenceEquals(e, this); e = VisualTreeHelper.GetParent(e))
            (e as UIElement)?.InvalidateMeasure();
        return base.MeasureOverride(constraint);
    }

    // An element's width with all the room it wants; its parent measures it again as laid out.
    private static double Natural(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private static bool SetVisible(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility == visibility)
            return false;
        element.Visibility = visibility;
        return true;
    }

    // The page's controls at their own width, wherever they are now, with their gap in the bar.
    private double ToolsWidth()
    {
        _tools!.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _tools.DesiredSize.Width + ToolsHost.Margin.Left + ToolsHost.Margin.Right;
    }

    private void WrapTools(bool wrap)
    {
        ToolsHost.Content    = null;
        ToolsRow.Content     = null;
        (wrap ? ToolsRow : ToolsHost).Content = _tools;
        ToolsHost.Visibility = wrap ? Visibility.Collapsed : Visibility.Visible;
        ToolsRow.Visibility  = wrap ? Visibility.Visible : Visibility.Collapsed;
    }

    private double PlaceholdersWidth() => Placeholders.Sum(b => b.Width + b.Margin.Left + b.Margin.Right);

    private double DetailWidth()
    {
        var text = new FormattedText(TxtDetail.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(TxtDetail.FontFamily, TxtDetail.FontStyle, TxtDetail.FontWeight, TxtDetail.FontStretch),
            TxtDetail.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Min(text.Width, TxtDetail.MaxWidth) + TxtDetail.Margin.Left;
    }

    /// <summary>The snapshot's age text again, as the timer does; for tests with a fake clock.</summary>
    internal void Tick() => ShowFreshness();

    // Pause or Resume for a live page that can pause; Refresh beside a paused one, and for every other page.
    private void UpdatePauseButton(PageFreshness? f)
    {
        var pausable = _canPause && f is { Kind: PageKind.Live };
        BtnPause.Visibility = pausable ? Visibility.Visible : Visibility.Collapsed;
        BtnRefresh.Visibility = pausable && !f!.Paused ? Visibility.Collapsed : Visibility.Visible;
        if (!pausable)
            return;

        var paused = f!.Paused;
        BtnPause.Content = paused ? "\uE768" : "\uE769";
        var name = paused ? "Resume live updates" : "Pause live updates";
        BtnPause.ToolTip = paused ? name : $"{name}: the page stops moving so you can read it. F5 still refreshes it once.";
        AutomationProperties.SetName(BtnPause, name);
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) =>
        PauseRequested?.Invoke(_freshness is not { Paused: true });

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void BtnDatabase_Click(object sender, RoutedEventArgs e) => DatabaseRequested?.Invoke();

    /// <summary>Refresh can be pressed: false while the page loads, and for a page with nothing to read.</summary>
    public bool CanRefresh
    {
        get => BtnRefresh.IsEnabled;
        set => BtnRefresh.IsEnabled = value;
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    /// <summary>
    /// "Loading…", "Switched to PROD-SQL02.", or an error the page ran into, which used to be the
    /// sidebar's status text. Null clears it.
    /// </summary>
    public void ShowMessage(string? text, bool isError = false)
    {
        TxtMessage.Text = text ?? string.Empty;
        MessageIsError = isError && !string.IsNullOrEmpty(text);
        TxtMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        TxtMessage.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Danger" : "TextMuted");
        AutomationProperties.SetName(TxtMessage, isError ? $"Error: {text}" : text ?? string.Empty);
        // A screen reader hears an error as it arrives.
        AutomationProperties.SetLiveSetting(TxtMessage, isError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (!string.IsNullOrEmpty(text) && UIElementAutomationPeer.FromElement(TxtMessage) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>The message on show, for tests and for keeping an error when the page reports again.</summary>
    public string Message => TxtMessage.Text;

    public bool MessageIsError { get; private set; }

    // A toolbar to a screen reader, not a plain group.
    protected override AutomationPeer OnCreateAutomationPeer() => new TopBarAutomationPeer(this);

    private sealed class TopBarAutomationPeer(TopBar owner) : UserControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolBar;
        protected override string GetClassNameCore() => nameof(TopBar);
    }
}
