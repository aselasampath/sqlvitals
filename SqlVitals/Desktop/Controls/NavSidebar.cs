using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The pinned sidebar (#124, #141, #254): the global pages (All servers, Alerts, Export / AI), then
/// the open server's part: its card (name, a menu of the saved servers, health dot, score and
/// Start/Stop, the worst state and its reason, #173), the Recent row, and every one of its screens
/// under the captions of <see cref="NavMenu.Groups"/>, each one click away, with a dot on the ones a
/// health check past a threshold points at. Settings is at the foot, and the filter box (<see
/// cref="FilterBar"/>) goes at the top. With no server open, the server part says how to open one.
/// Which page is open is up to MainWindow.
/// </summary>
internal sealed class NavSidebar
{
    public const double RowHeight     = 24;
    public const double CaptionHeight = 22;
    public const double GlobalHeight  = 26;

    // Every page's button by its tag, every group's caption by its id, the counts and finding marks on buttons.
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, TextBlock> _captions = new();
    private readonly Dictionary<string, (Border Badge, TextBlock Text)> _counts = new();
    private readonly Dictionary<string, FindingMark> _marks = new();

    // What sets a page's tooltip and spoken name: the server lacking it, or a finding on it.
    private IReadOnlyDictionary<string, string> _unavailable = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, PageFinding> _findings = SidebarFindings.None;

    private bool _serverOpen;

    /// <param name="panel">The scrolling part: the global pages and the server part.</param>
    /// <param name="footer">Below it, never scrolled away: Settings.</param>
    public NavSidebar(Panel panel, Panel footer)
    {
        FilterBar = BuildFilter();

        foreach (var page in NavMenu.Global)
            panel.Children.Add(CreateButton(page));

        ServerHeader = BuildServerHeader();
        panel.Children.Add(ServerHeader);
        NoServer = BuildNoServer();
        panel.Children.Add(NoServer);
        // A clock, then the chips on one line, a third of it each: 23 screens and their
        // captions leave no room for a caption of its own in a 1000 px window.
        RecentRow = new DockPanel { Margin = new Thickness(16, 2, 8, 0), ToolTip = "The screens of this server you opened last" };
        AutomationProperties.SetName(RecentRow, "Recent");
        var clock = new TextBlock { Text = "\uE81C", FontFamily = new FontFamily(NavMenu.IconFont), FontSize = 11, Width = 16,
                                    VerticalAlignment = VerticalAlignment.Center };
        clock.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        AutomationProperties.SetName(clock, "Recent");
        DockPanel.SetDock(clock, Dock.Left);
        RecentRow.Children.Add(clock);
        _recentChips = new UniformGrid { Rows = 1 };
        RecentRow.Children.Add(_recentChips);
        panel.Children.Add(RecentRow);

        foreach (var group in NavMenu.Groups)
        {
            panel.Children.Add(_captions[group.Id] = BuildCaption(group.Name, group.Question, group.Id));
            foreach (var page in group.Pages)
            {
                var button = CreateButton(page, showIcon: false);
                button.Height  = RowHeight;
                button.Padding = new Thickness(16, 0, 8, 0);
                panel.Children.Add(button);
            }
        }

        foreach (var page in NavMenu.Footer)
            footer.Children.Add(CreateButton(page));

        ShowServer(null);
    }

    /// <summary>A page's button was clicked, or Enter pressed in the filter box, with the page's tag.</summary>
    public event Action<string>? PageRequested;

    /// <summary>The open server's score was clicked: show what lowered it, beside <see cref="ScoreButton"/>.</summary>
    public event Action? ScoreRequested;

    /// <summary>The open server's Start or Stop monitoring was clicked.</summary>
    public event Action? MonitorToggleRequested;

    /// <summary>The open server's name was clicked: show the saved servers, beside <see cref="ServerNameButton"/>.</summary>
    public event Action? ServerMenuRequested;

    /// <summary>A page's button, e.g. to disable it.</summary>
    public Button Button(string tag) => _buttons[tag];

    /// <summary>A group's caption over its screens.</summary>
    public TextBlock Caption(string id) => _captions[id];

    /// <summary>The dot and short name at the right of a screen's row.</summary>
    internal FindingMark Mark(string tag) => _marks[tag];

    public bool HasPage(string tag) => _buttons.ContainsKey(tag);

    // ── The filter box ───────────────────────────────────────────────────────

    /// <summary>The filter box with its Ctrl K reminder, for the top of the sidebar, under the app's name.</summary>
    internal FrameworkElement FilterBar { get; }

    internal TextBox Filter { get; private set; } = null!;

    private TextBlock _placeholder = null!;

    private FrameworkElement BuildFilter()
    {
        Filter = new TextBox
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12, BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 0, 44, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Show only the screens whose name or description matches. Enter opens the first, Esc clears.",
        };
        Filter.SetResourceReference(Control.BackgroundProperty, "BgCard");
        Filter.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        Filter.SetResourceReference(Control.BorderBrushProperty, "Border");
        Filter.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimary");
        AutomationProperties.SetName(Filter, "Find a screen");
        Filter.TextChanged    += (_, _) => ApplyFilter();
        Filter.PreviewKeyDown += Filter_PreviewKeyDown;

        var glass = new TextBlock { Text = "\uE721", FontFamily = new FontFamily(NavMenu.IconFont), FontSize = 12, IsHitTestVisible = false,
                                    Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        glass.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _placeholder = new TextBlock { Text = "Find a screen", FontFamily = new FontFamily("Segoe UI"), FontSize = 12, IsHitTestVisible = false,
                                       Margin = new Thickness(27, 0, 44, 0), VerticalAlignment = VerticalAlignment.Center };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        // Ctrl+K finds servers, tables and settings too.
        var keysText = new TextBlock { Text = "Ctrl K", FontFamily = new FontFamily("Segoe UI"), FontSize = 10 };
        keysText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var keys = new Border
        {
            Child = keysText, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Ctrl+K finds servers, tables and settings too",
        };
        keys.SetResourceReference(Border.BorderBrushProperty, "Border");
        AutomationProperties.SetName(keys, "Ctrl+K finds servers, tables and settings too");
        KeysHint = keys;

        var bar = new Grid { Height = 28, Margin = new Thickness(10, 0, 10, 4) };
        bar.Children.Add(Filter);
        bar.Children.Add(glass);
        bar.Children.Add(_placeholder);
        bar.Children.Add(keys);
        return bar;
    }

    /// <summary>The Ctrl K reminder at the filter box's right.</summary>
    internal Border KeysHint { get; private set; } = null!;

    /// <summary>The screens the filter keeps, in the order shown; every one while it is empty.</summary>
    internal IEnumerable<Button> Shown => NavMenu.Global.Concat(NavMenu.Groups.SelectMany(g => g.Pages))
                                                 .Select(p => _buttons[p.Tag])
                                                 .Where(b => b.Visibility == Visibility.Visible);

    // Enter opens the best match that can open, ↓ goes to the first one kept, Esc clears the box.
    private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when Filter.Text.Length > 0:
                Filter.Clear();
                e.Handled = true;
                break;
            case Key.Enter when SidebarFindings.Best(Filter.Text, Shown.Where(b => b.IsEnabled).Select(b => Page((string)b.Tag))) is { } best:
                e.Handled = true;
                Pick(best.Tag);
                break;
            case Key.Down when Shown.FirstOrDefault(b => b.IsEnabled) is { } first:
                e.Handled = true;
                first.Focus();
                break;
        }
    }

    // The global pages and the open server's screens matching the filter, their captions while
    // one of theirs is; the Recent row only while nothing is typed.
    private void ApplyFilter()
    {
        var query = Filter.Text;
        _placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var page in NavMenu.Global)
            _buttons[page.Tag].Visibility = Show(SidebarFindings.Matches(query, page));
        foreach (var group in NavMenu.Groups)
        {
            var any = false;
            foreach (var page in group.Pages)
            {
                var kept = _serverOpen && SidebarFindings.Matches(query, page);
                _buttons[page.Tag].Visibility = Show(kept);
                any |= kept;
            }
            _captions[group.Id].Visibility = Show(any);
        }
        RecentRow.Visibility = Show(_serverOpen && query.Length == 0 && _recentChips.Children.Count > 0);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static NavPage Page(string tag) => NavMenu.AllPages.First(p => p.Tag == tag);

    // A screen picked in the sidebar: the filter has done its job, so the whole list is back next time.
    private void Pick(string tag)
    {
        Filter.Clear();
        PageRequested?.Invoke(tag);
    }

    // ── The server part ──────────────────────────────────────────────────────

    /// <summary>The open server's name, health, score and Start/Stop; hidden with no server open.</summary>
    internal Border ServerHeader { get; }

    /// <summary>"Open a server from All servers", while no server is open.</summary>
    internal TextBlock NoServer { get; }

    /// <summary>The open server's last few screens visited (#254), as chips; hidden until there is one.</summary>
    internal DockPanel RecentRow { get; }

    private readonly UniformGrid _recentChips;

    internal Ellipse   ServerDot    { get; private set; } = null!;
    internal Button    ServerNameButton { get; private set; } = null!;
    internal TextBlock ServerName   { get; private set; } = null!;
    internal TextBlock ServerLine   { get; private set; } = null!;
    internal TextBlock ServerVerdict { get; private set; } = null!;
    internal Button    ScoreButton  { get; private set; } = null!;
    internal TextBlock ScoreText    { get; private set; } = null!;
    internal Button    MonitorButton { get; private set; } = null!;

    /// <summary>
    /// The server whose pages are shown, as its selector row (health, score, Start/Stop); null for
    /// none, which hides its screens and says how to open one.
    /// </summary>
    public void ShowServer(ConnectionSelectorItem? server)
    {
        _serverOpen = server is not null;
        ServerHeader.Visibility = Show(_serverOpen);
        NoServer.Visibility     = Show(!_serverOpen);
        ApplyFilter();
        if (server is null)
            return;

        ServerDot.Fill   = server.HealthFill;
        ServerDot.Stroke = server.HealthStroke;
        ServerName.Text  = server.DisplayName;
        ServerLine.Text  = $"{server.Server} · {server.DatabaseLabel}";
        ServerHeader.ToolTip = server.ToolTipText;
        AutomationProperties.SetName(ServerHeader, $"Server {server.DisplayName}");
        AutomationProperties.SetName(ServerNameButton, $"Server {server.DisplayName}. Switch server");
        ServerNameButton.ToolTip = $"{server.ToolTipText}\nClick to switch to another server.";

        // "Critical: blocking for 4 min" under the server, so the score beside the dot is never unexplained.
        ServerVerdict.Text       = server.Verdict;
        ServerVerdict.Foreground = server.ScoreBrush;
        ServerVerdict.Opacity    = server.ScoreOpacity;
        ServerVerdict.ToolTip    = server.Verdict;
        ServerVerdict.Visibility = server.Verdict.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        ScoreText.Text       = server.ScoreText;
        ScoreText.Foreground = server.ScoreBrush;
        ScoreButton.Opacity  = server.ScoreOpacity;
        ScoreButton.Visibility = server.ScoreVisibility;
        AutomationProperties.SetName(ScoreButton, server.Verdict.Length == 0
            ? $"Health score {server.ScoreText}: show what lowered it"
            : $"{server.Verdict}, health score {server.ScoreText}: show what lowered it");

        MonitorButton.Content   = server.IsMonitoring ? "\uE71A" : "\uE768";
        MonitorButton.IsEnabled = server.CanToggle;
        MonitorButton.ToolTip   = server.ToggleToolTip;
        AutomationProperties.SetName(MonitorButton, server.ToggleName);
    }

    /// <summary>The Recent row's screens, most recent first; none hides it.</summary>
    public void ShowRecent(IReadOnlyList<string> tags)
    {
        _recentChips.Children.Clear();
        _recentChips.Columns = Math.Max(SidebarFindings.RecentShown, tags.Count);
        foreach (var tag in tags)
        {
            var page = NavMenu.AllPages.First(p => p.Tag == tag);
            var chip = new Button
            {
                Content = new TextBlock { Text = page.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = tag, ToolTip = $"Open {page.Label} again",
            };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "RecentChip");
            AutomationProperties.SetName(chip, $"Recent: {page.Label}");
            chip.Click += (_, _) => Pick(tag);
            _recentChips.Children.Add(chip);
        }
        ApplyFilter();
    }

    /// <summary>The Recent row's chips, in order.</summary>
    internal IEnumerable<Button> RecentChips => _recentChips.Children.OfType<Button>();

    private Border BuildServerHeader()
    {
        ServerDot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        ScoreText   = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 13 };
        ScoreButton = new Button { Content = ScoreText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        ScoreButton.SetResourceReference(FrameworkElement.StyleProperty, "ScoreButton");
        ScoreButton.Click += (_, _) => ScoreRequested?.Invoke();

        MonitorButton = new Button
        {
            FontFamily = new FontFamily(NavMenu.IconFont), FontSize = 10, MinWidth = 22, Height = 22,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
        };
        MonitorButton.SetResourceReference(FrameworkElement.StyleProperty, "MonitorToggleButton");
        ToolTipService.SetShowOnDisabled(MonitorButton, true);
        MonitorButton.Click += (_, _) => MonitorToggleRequested?.Invoke();

        // The name opens the saved servers' menu, as the breadcrumb's server part does.
        ServerName = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ServerName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var chevron = new TextBlock { Text = "\uE70D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 8, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var nameRow = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        nameRow.Children.Add(chevron);
        nameRow.Children.Add(ServerName);
        ServerNameButton = new Button { Content = nameRow, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        ServerNameButton.SetResourceReference(FrameworkElement.StyleProperty, "ServerNameButton");
        ServerNameButton.Click += (_, _) => ServerMenuRequested?.Invoke();

        ServerLine = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(17, 1, 0, 0) };
        ServerLine.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        // One line, its tooltip the whole of it (#254): every screen has to fit under it.
        ServerVerdict = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                                        Margin = new Thickness(17, 2, 0, 0) };

        var top = new DockPanel();
        DockPanel.SetDock(MonitorButton, Dock.Right);
        DockPanel.SetDock(ScoreButton, Dock.Right);
        DockPanel.SetDock(ServerDot, Dock.Left);
        top.Children.Add(MonitorButton);
        top.Children.Add(ScoreButton);
        top.Children.Add(ServerDot);
        top.Children.Add(ServerNameButton);

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(ServerLine);
        body.Children.Add(ServerVerdict);

        var header = new Border { Child = body, Margin = new Thickness(6, 4, 6, 2), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        header.SetResourceReference(Border.BackgroundProperty, "BgCard");
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        return header;
    }

    // "Open a server from All servers", the last two words a link to Home.
    private TextBlock BuildNoServer()
    {
        var link = new Hyperlink(new Run(Breadcrumb.AllServers));
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentLight");
        link.Click += (_, _) => PageRequested?.Invoke("Home");

        var text = new TextBlock { Margin = new Thickness(18, 14, 12, 6), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        text.Inlines.Add(new Run("Open a server from "));
        text.Inlines.Add(link);
        AutomationProperties.SetName(text, "No server open. Open a server from All servers.");
        return text;
    }

    // A group's name over its screens: small, upper case and muted, announced as their heading. Not
    // a stop for Tab or the arrows; its question is its tooltip.
    private static TextBlock BuildCaption(string name, string question, string? id = null)
    {
        var caption = new TextBlock
        {
            Text = name.ToUpper(CultureInfo.CurrentCulture), Height = CaptionHeight, Padding = new Thickness(17, 8, 10, 0),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.SemiBold,
            ToolTip = question, Tag = id,
            // Painted, so the tooltip shows over the whole row and not just its letters.
            Background = Brushes.Transparent,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        AutomationProperties.SetName(caption, name);
        AutomationProperties.SetHelpText(caption, question);
        AutomationProperties.SetHeadingLevel(caption, AutomationHeadingLevel.Level2);
        return caption;
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Highlights the page's button and scrolls it into view; its caption stays as it is. Null, or
    /// a page not in the sidebar, highlights none.
    /// </summary>
    public void Highlight(string? tag)
    {
        // By resource reference, so switching theme restyles the buttons at once.
        foreach (var button in _buttons.Values)
            button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
        if (tag is null || !_buttons.TryGetValue(tag, out var active))
            return;
        active.SetResourceReference(FrameworkElement.StyleProperty, "NavButtonActive");
        active.BringIntoView();
    }

    /// <summary>A count on a page's button, e.g. Alerts' active alerts; 0 hides it.</summary>
    public void SetCount(string tag, int count, string toolTip)
    {
        var (badge, text) = _counts[tag];
        text.Text = count.ToString(CultureInfo.CurrentCulture);
        badge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var page = NavMenu.AllPages.First(p => p.Tag == tag);
        AutomationProperties.SetName(Button(tag), count == 0 ? page.Label : $"{page.Label} ({count} active)");
        Button(tag).ToolTip = toolTip;
    }

    /// <summary>
    /// The pages the open server hasn't got, with why (#141): disabled, the reason their tooltip;
    /// the others as they were.
    /// </summary>
    public void SetUnavailable(IReadOnlyDictionary<string, string> unavailable)
    {
        var changed = _unavailable.Keys.Union(unavailable.Keys).ToList();
        _unavailable = unavailable;
        foreach (var tag in changed)
        {
            Button(tag).IsEnabled = !unavailable.ContainsKey(tag);
            ToolTipService.SetShowOnDisabled(Button(tag), true);
            Describe(tag);
        }
    }

    /// <summary>
    /// The finding dots (#254): a screen a check past a threshold points at shows the worst one's
    /// short name and dot, its tooltip lists them all, and it is announced with them.
    /// </summary>
    public void ShowFindings(IReadOnlyDictionary<string, PageFinding> findings)
    {
        var changed = _findings.Keys.Union(findings.Keys).ToList();
        _findings = findings;
        foreach (var tag in changed.Where(_marks.ContainsKey))
        {
            _marks[tag].Show(findings.GetValueOrDefault(tag));
            Describe(tag);
        }
    }

    // A server screen's tooltip and spoken name, from its own, the reason the server lacks it and
    // what it has found.
    private void Describe(string tag)
    {
        var page   = NavMenu.AllPages.First(p => p.Tag == tag);
        var button = Button(tag);
        if (_unavailable.TryGetValue(tag, out var reason))
        {
            button.ToolTip = reason;
            AutomationProperties.SetName(button, page.Label);
        }
        else if (_findings.TryGetValue(tag, out var finding))
        {
            var lines = string.Join("\n", finding.Lines);
            button.ToolTip = page.ToolTip is null ? lines : $"{lines}\n\n{page.ToolTip}";
            AutomationProperties.SetName(button, $"{page.Label}, {finding.Spoken}");
        }
        else
        {
            button.ToolTip = page.ToolTip;
            AutomationProperties.SetName(button, page.Label);
        }
    }

    private Button CreateButton(NavPage page, bool showIcon = true)
    {
        var content = new DockPanel { LastChildFill = true };

        // A count, hidden until there is one (Alerts).
        var countText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        var badge = new Border { Child = countText, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Danger");
        DockPanel.SetDock(badge, Dock.Right);
        content.Children.Add(badge);
        _counts[page.Tag] = (badge, countText);

        if (showIcon)
        {
            var icon = new TextBlock { Text = page.Icon, FontFamily = new FontFamily(NavMenu.IconFont), FontSize = 16, Width = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(icon);
        }
        else
        {
            // A server screen: what a health check found on it, at the right (#254).
            var mark = new FindingMark();
            DockPanel.SetDock(mark, Dock.Right);
            content.Children.Add(mark);
            _marks[page.Tag] = mark;
        }
        content.Children.Add(new TextBlock { Text = page.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button { Content = content, Tag = page.Tag, ToolTip = page.ToolTip };
        if (showIcon)
            button.Height = GlobalHeight;
        button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
        // The name without the icon, which a screen reader would otherwise read out.
        AutomationProperties.SetName(button, page.Label);
        button.Click += (_, _) => Pick(page.Tag);
        _buttons[page.Tag] = button;
        return button;
    }
}
