using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The sidebar (#124, #141): the global pages (All servers, Alerts, Export / AI), then the open
/// server's part: its name, health dot, score and Start/Stop, and its pages in the groups of
/// <see cref="NavMenu"/>, one group open at a time. Settings is at the foot. With no server open,
/// the server part says how to open one. Which page is open is up to MainWindow.
/// </summary>
internal sealed class NavSidebar
{
    // Every page's button by its tag, every group's expander by its id, and the counts shown on buttons.
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, Expander> _groups = new();
    private readonly Dictionary<string, (Border Badge, TextBlock Text)> _counts = new();

    /// <param name="panel">The scrolling part: the global pages and the server part.</param>
    /// <param name="footer">Below it, never scrolled away: Settings.</param>
    public NavSidebar(Panel panel, Panel footer)
    {
        foreach (var page in NavMenu.Global)
            panel.Children.Add(CreateButton(page));

        ServerHeader = BuildServerHeader();
        panel.Children.Add(ServerHeader);
        NoServer = BuildNoServer();
        panel.Children.Add(NoServer);

        foreach (var group in NavMenu.Groups)
        {
            var pages = new StackPanel();
            foreach (var page in group.Pages)
            {
                var button = CreateButton(page, showIcon: false);
                // Under the group's name, past its icon. Local, so it stays when the style changes.
                button.Padding = new Thickness(42, 0, 10, 0);
                pages.Children.Add(button);
            }

            var expander = new Expander { Header = group.Name, Content = pages, IsExpanded = false, Tag = group.Id };
            expander.SetResourceReference(FrameworkElement.StyleProperty, "NavGroupExpander");
            NavGroupState.SetIcon(expander, group.Icon);
            AutomationProperties.SetName(expander, group.Name);
            // Also the header's tooltip (see NavGroupExpander).
            AutomationProperties.SetHelpText(expander, group.Question);

            expander.Expanded += Group_Expanded;
            expander.KeyDown  += Group_KeyDown;

            _groups[group.Id] = expander;
            panel.Children.Add(expander);
        }

        foreach (var page in NavMenu.Footer)
            footer.Children.Add(CreateButton(page));

        ShowServer(null);
    }

    /// <summary>A page's button was clicked, with the page's tag.</summary>
    public event Action<string>? PageRequested;

    /// <summary>The open server's score was clicked: show what lowered it, beside <see cref="ScoreButton"/>.</summary>
    public event Action? ScoreRequested;

    /// <summary>The open server's Start or Stop monitoring was clicked.</summary>
    public event Action? MonitorToggleRequested;

    /// <summary>A page's button, e.g. to disable it.</summary>
    public Button Button(string tag) => _buttons[tag];

    public Expander Group(string id) => _groups[id];

    public bool HasPage(string tag) => _buttons.ContainsKey(tag);

    // ── The server part ──────────────────────────────────────────────────────

    /// <summary>The open server's name, health, score and Start/Stop; hidden with no server open.</summary>
    internal Border ServerHeader { get; }

    /// <summary>"Open a server from All servers", while no server is open.</summary>
    internal TextBlock NoServer { get; }

    internal Ellipse   ServerDot    { get; private set; } = null!;
    internal TextBlock ServerName   { get; private set; } = null!;
    internal TextBlock ServerLine   { get; private set; } = null!;
    internal Button    ScoreButton  { get; private set; } = null!;
    internal TextBlock ScoreText    { get; private set; } = null!;
    internal Button    MonitorButton { get; private set; } = null!;

    /// <summary>
    /// The server whose pages are shown, as its selector row (health, score, Start/Stop); null for
    /// none, which hides the groups and says how to open one.
    /// </summary>
    public void ShowServer(ConnectionSelectorItem? server)
    {
        var open = server is not null;
        ServerHeader.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        NoServer.Visibility     = open ? Visibility.Collapsed : Visibility.Visible;
        foreach (var group in _groups.Values)
            group.Visibility = ServerHeader.Visibility;
        if (server is null)
            return;

        ServerDot.Fill   = server.HealthFill;
        ServerDot.Stroke = server.HealthStroke;
        ServerName.Text  = server.DisplayName;
        ServerLine.Text  = $"{server.Server} · {server.DatabaseLabel}";
        ServerHeader.ToolTip = server.ToolTipText;
        AutomationProperties.SetName(ServerHeader, $"Server {server.DisplayName}");

        ScoreText.Text       = server.ScoreText;
        ScoreText.Foreground = server.ScoreBrush;
        ScoreButton.Opacity  = server.ScoreOpacity;
        ScoreButton.Visibility = server.ScoreVisibility;
        AutomationProperties.SetName(ScoreButton, $"Health score {server.ScoreText}: show what lowered it");

        MonitorButton.Content   = server.IsMonitoring ? "\uE71A" : "\uE768";
        MonitorButton.IsEnabled = server.CanToggle;
        MonitorButton.ToolTip   = server.ToggleToolTip;
        AutomationProperties.SetName(MonitorButton, server.ToggleName);
    }

    private Border BuildServerHeader()
    {
        ServerDot = new Ellipse { Width = 9, Height = 9, StrokeThickness = 1.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

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

        ServerName = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ServerName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        ServerLine = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(17, 2, 0, 0) };
        ServerLine.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var top = new DockPanel();
        DockPanel.SetDock(MonitorButton, Dock.Right);
        DockPanel.SetDock(ScoreButton, Dock.Right);
        DockPanel.SetDock(ServerDot, Dock.Left);
        top.Children.Add(MonitorButton);
        top.Children.Add(ScoreButton);
        top.Children.Add(ServerDot);
        top.Children.Add(ServerName);

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(ServerLine);

        var header = new Border { Child = body, Margin = new Thickness(6, 12, 6, 4), Padding = new Thickness(10, 8, 8, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
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

    // ── Pages ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Highlights the page's button and shows it: its group opens, and the others close. Null, or a
    /// page not in the sidebar, highlights none.
    /// </summary>
    public void Highlight(string? tag)
    {
        // By resource reference, so switching theme restyles the buttons at once.
        foreach (var button in _buttons.Values)
            button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
        if (tag is null || !_buttons.TryGetValue(tag, out var active))
            return;
        active.SetResourceReference(FrameworkElement.StyleProperty, "NavButtonActive");

        var groupId = NavMenu.GroupOf(tag)?.Id;
        foreach (var (id, expander) in _groups)
            NavGroupState.SetHoldsCurrentPage(expander, id == groupId);
        if (groupId is not null)
            _groups[groupId].IsExpanded = true;
        active.BringIntoView();
    }

    /// <summary>A count on a page's button, e.g. Alerts' active alerts; 0 hides it.</summary>
    public void SetCount(string tag, int count, string toolTip)
    {
        var (badge, text) = _counts[tag];
        text.Text = count.ToString();
        badge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var page = NavMenu.AllPages.First(p => p.Tag == tag);
        AutomationProperties.SetName(Button(tag), count == 0 ? page.Label : $"{page.Label} ({count} active)");
        Button(tag).ToolTip = toolTip;
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
        content.Children.Add(new TextBlock { Text = page.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button { Content = content, Tag = page.Tag, ToolTip = page.ToolTip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
        // The name without the icon, which a screen reader would otherwise read out.
        AutomationProperties.SetName(button, page.Label);
        button.Click += (_, _) => PageRequested?.Invoke(page.Tag);
        _buttons[page.Tag] = button;
        return button;
    }

    // One group open at a time: opening one closes the others.
    private void Group_Expanded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
            return;
        foreach (var group in _groups.Values)
            if (!ReferenceEquals(group, sender))
                group.IsExpanded = false;
    }

    // The expander has the keyboard focus, not its header (see NavGroupExpander): Enter or Space toggles it.
    private void Group_KeyDown(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not Expander expander || e.Key is not (Key.Enter or Key.Space))
            return;

        expander.IsExpanded = !expander.IsExpanded;
        e.Handled = true;
    }
}
