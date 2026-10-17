using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Shell;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The one bar across the top of the content area (#140): Back and Forward, the breadcrumb, and
/// the page's context: its database, its own controls (a time range picker), whether it is live,
/// a snapshot or history and how fresh, then Pause or Refresh. It draws its
/// <see cref="TopBarViewModel"/> (#191), whose commands its buttons are; the page's own controls
/// and the layout in a narrow window are its own.
/// </summary>
public partial class TopBar : UserControl
{
    // A snapshot's age moves on while it is on screen: "2 min ago", then amber at 15 minutes.
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    private TopBarViewModel _model = new();

    public TopBar()
    {
        DataContext = _model;
        InitializeComponent();
        _model.PropertyChanged += OnModelChanged;
        _ageTimer.Tick += (_, _) => ShowFreshness();
        Unloaded += (_, _) => _ageTimer.Stop();
    }

    /// <summary>What it shows, and what its buttons ask for: the shell's, or its own until then.</summary>
    public TopBarViewModel ViewModel
    {
        get => _model;
        set
        {
            _model.PropertyChanged -= OnModelChanged;
            _model = value;
            DataContext = value;
            _model.PropertyChanged += OnModelChanged;
            ShowHistory();
            ShowBreadcrumb();
            ShowContext();
            ShowMessage();
            ShowSidebar();
            ShowFocus();
            ShowDetailsOpen();
        }
    }

    // Each change, drawn; a change that raises several names is drawn on its last.
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TopBarViewModel.ForwardToolTip): ShowHistory(); break;
            case nameof(TopBarViewModel.BreadcrumbText): ShowBreadcrumb(); break;
            case nameof(TopBarViewModel.Freshness):      ShowContext(); break;
            case nameof(TopBarViewModel.Message):        ShowMessage(); break;
            case nameof(TopBarViewModel.SidebarBadge):   ShowSidebar(); break;
            case nameof(TopBarViewModel.InFocus):        ShowFocus(); break;
            case nameof(TopBarViewModel.DetailsOpen):    ShowDetailsOpen(); break;
        }
    }

    /// <summary>The clock the snapshot's age is measured on; a fake one in tests.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    private DateTime Now => Clock.GetLocalNow().DateTime;

    /// <inheritdoc cref="TopBarViewModel.PauseRequested"/>
    public event Action<bool>? PauseRequested
    {
        add    => _model.PauseRequested += value;
        remove => _model.PauseRequested -= value;
    }

    /// <inheritdoc cref="TopBarViewModel.CrumbRequested"/>
    public event Action<Route>? CrumbRequested
    {
        add    => _model.CrumbRequested += value;
        remove => _model.CrumbRequested -= value;
    }

    /// <inheritdoc cref="TopBarViewModel.ServerRequested"/>
    public event Action<Guid>? ServerRequested
    {
        add    => _model.ServerRequested += value;
        remove => _model.ServerRequested -= value;
    }

    /// <inheritdoc cref="TopBarViewModel.SidebarRequested"/>
    public event Action? SidebarRequested
    {
        add    => _model.SidebarRequested += value;
        remove => _model.SidebarRequested -= value;
    }

    /// <inheritdoc cref="TopBarViewModel.JumpRequested"/>
    public event Action? JumpRequested
    {
        add    => _model.JumpRequested += value;
        remove => _model.JumpRequested -= value;
    }

    // ── The sidebar's ☰ (#142) ───────────────────────────────────────────────

    /// <inheritdoc cref="TopBarViewModel.ShowSidebar"/>
    public void ShowSidebar(SidebarMode shown, bool slidOver, int alerts) => _model.ShowSidebar(shown, slidOver, alerts);

    private void ShowSidebar()
    {
        SidebarBadge.Visibility = _model.SidebarCounts ? Visibility.Visible : Visibility.Collapsed;
        TxtSidebarBadge.Text    = _model.SidebarBadge;
        BtnSidebar.ToolTip      = _model.SidebarToolTip;
        AutomationProperties.SetName(BtnSidebar, _model.SidebarAutomationName);
    }

    /// <summary>In focus mode the Focus button gives way to Exit focus (Esc).</summary>
    public void ShowFocus(bool on) => _model.InFocus = on;

    private void ShowFocus()
    {
        BtnFocus.Visibility     = _model.InFocus ? Visibility.Collapsed : Visibility.Visible;
        BtnExitFocus.Visibility = _model.InFocus ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Says whether the details button opens or closes the drawer.</summary>
    public void ShowDetailsOpen(bool open) => _model.DetailsOpen = open;

    private void ShowDetailsOpen()
    {
        BtnDetails.ToolTip = _model.DetailsToolTip;
        AutomationProperties.SetName(BtnDetails, _model.DetailsName);
    }

    // ── Back and Forward ─────────────────────────────────────────────────────

    /// <inheritdoc cref="TopBarViewModel.ShowHistory"/>
    public void ShowHistory(string? back, string? forward) => _model.ShowHistory(back, forward);

    // Enabled by their commands.
    private void ShowHistory()
    {
        BtnBack.ToolTip    = _model.BackToolTip;
        BtnForward.ToolTip = _model.ForwardToolTip;
    }

    // ── Breadcrumb ───────────────────────────────────────────────────────────

    /// <summary>The breadcrumb's text, for tests and the window's own description of where it is.</summary>
    internal string BreadcrumbText => _model.BreadcrumbText;

    // The breadcrumb on show, and its server part's health, which a new sample updates in place:
    // rebuilding the parts would take the keyboard focus off one.
    private IReadOnlyList<Crumb> _crumbs = [];
    private (Ellipse Dot, TextBlock Score, FrameworkElement Part, Crumb Crumb)? _serverPart;

    /// <inheritdoc cref="TopBarViewModel.ShowBreadcrumb"/>
    public void ShowBreadcrumb(IReadOnlyList<Crumb> crumbs, Func<Guid, ConnectionSelectorItem?> server,
                               IReadOnlyList<ConnectionSelectorItem>? servers = null) =>
        _model.ShowBreadcrumb(crumbs, server, servers);

    private void ShowBreadcrumb()
    {
        var crumbs = _model.Crumbs;
        var server = _model.ServerOf;
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
            button.Click += (_, _) => _model.OpenCrumb(target);
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
        foreach (var server in _model.Servers)
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
            item.Click += (_, _) => _model.SwitchServer(id, current);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = Breadcrumb.AllServers };
        all.Click += (_, _) => _model.OpenCrumb(new Route("Home"));
        menu.Items.Add(all);

        menu.Closed += (_, _) => ServerMenu = null;
        ServerMenu = menu;
        menu.IsOpen = true;
    }

    // ── Page context ─────────────────────────────────────────────────────────

    /// <summary>
    /// A new page: the controls it hands to the bar (the previous page's go with it), then its
    /// context as <see cref="ShowContext(PageFreshness?, bool, string?)"/>. The message is cleared.
    /// </summary>
    public void ShowPage(FrameworkElement? tools, PageFreshness? freshness, bool canPause, string? database)
    {
        ShowTools(tools);
        _model.ShowPage(freshness, canPause, database);
    }

    /// <summary>The controls a new page hands to the bar, taken out of its layout; the previous page's go.</summary>
    public void ShowTools(FrameworkElement? tools)
    {
        // In the bar to begin with; the layout moves them to the row below if there's no room.
        ToolsRow.Content    = null;
        ToolsRow.Visibility = Visibility.Collapsed;
        ToolsHost.Content    = tools;
        ToolsHost.Visibility = tools is null ? Visibility.Collapsed : Visibility.Visible;
        _tools = tools;
        ShowContext();
    }

    /// <inheritdoc cref="TopBarViewModel.ShowContext"/>
    public void ShowContext(PageFreshness? freshness, bool canPause, string? database) => _model.ShowContext(freshness, canPause, database);

    private void ShowContext()
    {
        var database = _model.Database;
        BtnDatabase.Visibility = database is null ? Visibility.Collapsed : Visibility.Visible;
        if (database is not null)
        {
            RunDatabase.Text = database;
            BtnDatabase.ToolTip = $"This page reads the connection's database, {database}. Click to change it in the connection's settings.";
            AutomationProperties.SetName(BtnDatabase, $"Database: {database}");
        }

        ShowFreshness();

        var any = _model.Freshness is { Kind: not PageKind.None } || _tools is not null || database is not null;
        ContextPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the bar says now, for tests.</summary>
    internal PageFreshness? Freshness => _model.Freshness;

    private void ShowFreshness()
    {
        var f = _model.Freshness;
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
            UpdatePauseButton();
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

        UpdatePauseButton();
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
    private void UpdatePauseButton()
    {
        BtnPause.Visibility   = _model.ShowsPause ? Visibility.Visible : Visibility.Collapsed;
        BtnRefresh.Visibility = _model.ShowsRefresh ? Visibility.Visible : Visibility.Collapsed;
        if (!_model.ShowsPause)
            return;

        var paused = _model.IsPaused;
        BtnPause.Content = paused ? "\uE768" : "\uE769";
        var name = paused ? "Resume live updates" : "Pause live updates";
        BtnPause.ToolTip = paused ? name : $"{name}: the page stops moving so you can read it. F5 still refreshes it once.";
        AutomationProperties.SetName(BtnPause, name);
    }

    /// <inheritdoc cref="TopBarViewModel.CanRefresh"/>
    public bool CanRefresh
    {
        get => _model.CanRefresh;
        set => _model.CanRefresh = value;
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    /// <summary>
    /// "Loading…", "Switched to PROD-SQL02.", or an error the page ran into, which used to be the
    /// sidebar's status text. Null clears it.
    /// </summary>
    public void ShowMessage(string? text, bool isError = false) => _model.ShowMessage(text, isError);

    private void ShowMessage()
    {
        var text    = _model.Message;
        var isError = _model.MessageIsError;
        TxtMessage.Text = text;
        TxtMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        TxtMessage.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        TxtMessage.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Danger" : "TextMuted");
        AutomationProperties.SetName(TxtMessage, isError ? $"Error: {text}" : text);
        // A screen reader hears an error as it arrives.
        AutomationProperties.SetLiveSetting(TxtMessage, isError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (!string.IsNullOrEmpty(text) && UIElementAutomationPeer.FromElement(TxtMessage) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>The message on show, for tests and for keeping an error when the page reports again.</summary>
    public string Message => _model.Message;

    public bool MessageIsError => _model.MessageIsError;

    // A toolbar to a screen reader, not a plain group.
    protected override AutomationPeer OnCreateAutomationPeer() => new TopBarAutomationPeer(this);

    private sealed class TopBarAutomationPeer(TopBar owner) : UserControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolBar;
        protected override string GetClassNameCore() => nameof(TopBar);
    }
}
