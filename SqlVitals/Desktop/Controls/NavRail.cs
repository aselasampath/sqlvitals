using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The collapsed sidebar (#142), 56 px wide: an icon per global page, the open server's health dot
/// and score, an icon per group of its pages, then Settings and the pin at the foot. Each icon's
/// name is its tooltip. A group's pages open in a fly-out, which <see cref="WorkspaceShell"/> shows.
/// </summary>
internal sealed class NavRail
{
    private readonly Dictionary<string, Button> _pages = new();
    private readonly Dictionary<string, Button> _groups = new();
    private readonly Dictionary<string, (Border Badge, TextBlock Text)> _counts = new();

    public NavRail(Panel panel, Panel footer)
    {
        foreach (var page in NavMenu.Global)
            panel.Children.Add(PageButton(page));

        Separator = new Border { Height = 1, Width = 32, Margin = new Thickness(0, 8, 0, 8) };
        Separator.SetResourceReference(Border.BackgroundProperty, "Border");
        panel.Children.Add(Separator);

        ServerMark = BuildServerMark();
        panel.Children.Add(ServerMark);

        foreach (var group in NavMenu.Groups)
        {
            var button = new Button { Content = group.Icon, Tag = group.Id, ToolTip = $"{group.Name}: {group.Question}" };
            button.SetResourceReference(FrameworkElement.StyleProperty, "NavRailButton");
            AutomationProperties.SetName(button, group.Name);
            AutomationProperties.SetHelpText(button, group.Question);
            // Enter or Space is a click; the keyboard then moves into the fly-out.
            button.Click   += (_, _) => GroupRequested?.Invoke(group.Id, InputManager.Current.MostRecentInputDevice is KeyboardDevice);
            button.KeyDown += (_, e) =>
            {
                // → opens the fly-out, as Enter does, and moves into it.
                if (e.Key != Key.Right)
                    return;
                e.Handled = true;
                GroupRequested?.Invoke(group.Id, true);
            };
            _groups[group.Id] = button;
            panel.Children.Add(button);
        }

        foreach (var page in NavMenu.Footer)
            footer.Children.Add(PageButton(page));

        PinButton = new Button { Content = "\uE718", ToolTip = "Pin the menu open, with its labels" };
        PinButton.SetResourceReference(FrameworkElement.StyleProperty, "NavRailButton");
        AutomationProperties.SetName(PinButton, "Pin the menu open");
        PinButton.Click += (_, _) => PinRequested?.Invoke();
        footer.Children.Add(PinButton);

        ShowServer(null);
    }

    /// <summary>A global page's icon, or Settings, was clicked.</summary>
    public event Action<string>? PageRequested;

    /// <summary>A group's icon was clicked, or → pressed on it: show its pages; true from the keyboard.</summary>
    public event Action<string, bool>? GroupRequested;

    /// <summary>The server's score was clicked: show what lowered it, beside <see cref="ServerMark"/>.</summary>
    public event Action? ScoreRequested;

    /// <summary>The pin at the foot was clicked.</summary>
    public event Action? PinRequested;

    /// <summary>A global page's or Settings' icon.</summary>
    public Button Page(string tag) => _pages[tag];

    /// <summary>A group's icon.</summary>
    public Button Group(string id) => _groups[id];

    public IEnumerable<Button> Groups => _groups.Values;

    internal Button PinButton { get; }

    /// <summary>Between the global pages and the server's.</summary>
    internal Border Separator { get; }

    /// <summary>The open server's health dot and score; hidden with no server open.</summary>
    internal Button ServerMark { get; }

    internal Ellipse   ServerDot   { get; private set; } = null!;
    internal TextBlock ServerScore { get; private set; } = null!;

    // The open server has a score, which a click on its mark explains.
    private bool _hasScore;

    /// <summary>The server whose groups are shown, as its selector row; null hides them.</summary>
    public void ShowServer(ConnectionSelectorItem? server)
    {
        var visibility = server is null ? Visibility.Collapsed : Visibility.Visible;
        ServerMark.Visibility = visibility;
        Separator.Visibility  = visibility;
        foreach (var group in _groups.Values)
            group.Visibility = visibility;
        if (server is null)
            return;

        ServerDot.Fill   = server.HealthFill;
        ServerDot.Stroke = server.HealthStroke;
        ServerScore.Text       = server.ScoreText;
        ServerScore.Foreground = server.ScoreBrush;
        ServerScore.Opacity    = server.ScoreOpacity;
        ServerScore.Visibility = server.ScoreVisibility;
        _hasScore = server.Score is not null;

        ServerMark.ToolTip = _hasScore ? $"{server.ToolTipText}\nClick to see what lowered the score." : server.ToolTipText;
        ServerMark.Cursor  = _hasScore ? Cursors.Hand : Cursors.Arrow;
        AutomationProperties.SetName(ServerMark, server.Score is { } s
            ? $"Server {server.DisplayName}, health score {s.Value}: show what lowered it"
            : server.HealthText.Length > 0 ? $"Server {server.DisplayName}, {server.HealthText}" : $"Server {server.DisplayName}");
    }

    /// <summary>
    /// The page on screen: its icon, or its group's, is highlighted. Null, or a page in no group
    /// and not on the rail, highlights none.
    /// </summary>
    public void Highlight(string? tag)
    {
        var groupId = tag is null ? null : NavMenu.GroupOf(tag)?.Id;
        foreach (var (pageTag, button) in _pages)
            Style(button, pageTag == tag);
        foreach (var (id, button) in _groups)
            Style(button, id == groupId);
    }

    /// <summary>A count on a page's icon, e.g. Alerts' active alerts; 0 hides it.</summary>
    public void SetCount(string tag, int count, string toolTip)
    {
        var (badge, text) = _counts[tag];
        text.Text = count > 99 ? "99+" : count.ToString();
        badge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var page = NavMenu.AllPages.First(p => p.Tag == tag);
        AutomationProperties.SetName(Page(tag), count == 0 ? page.Label : $"{page.Label} ({count} active)");
        Page(tag).ToolTip = count == 0 ? page.Label : $"{page.Label}: {toolTip}";
    }

    // By resource reference, so switching theme restyles the icons at once.
    private static void Style(Button button, bool active) =>
        button.SetResourceReference(FrameworkElement.StyleProperty, active ? "NavRailButtonActive" : "NavRailButton");

    private Button PageButton(NavPage page)
    {
        // The icon, and a count at its top right, hidden until there is one (Alerts).
        var content = new Grid { Width = 38, Height = 38 };
        content.Children.Add(new TextBlock { Text = page.Icon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

        var countText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        var badge = new Border
        {
            Child = countText, CornerRadius = new CornerRadius(7), MinWidth = 15, Height = 15, Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 1, 0), Visibility = Visibility.Collapsed,
        };
        countText.HorizontalAlignment = HorizontalAlignment.Center;
        countText.VerticalAlignment   = VerticalAlignment.Center;
        badge.SetResourceReference(Border.BackgroundProperty, "Danger");
        content.Children.Add(badge);
        _counts[page.Tag] = (badge, countText);

        var button = new Button { Content = content, Tag = page.Tag, ToolTip = page.Label };
        Style(button, active: false);
        AutomationProperties.SetName(button, page.Label);
        AutomationProperties.SetHelpText(button, page.ToolTip ?? string.Empty);
        button.Click += (_, _) => PageRequested?.Invoke(page.Tag);
        _pages[page.Tag] = button;
        return button;
    }

    // The server's health dot, and its score under it.
    private Button BuildServerMark()
    {
        ServerDot   = new Ellipse { Width = 10, Height = 10, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center };
        ServerScore = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(ServerDot);
        body.Children.Add(ServerScore);

        var mark = new Button { Content = body, Height = 46, Margin = new Thickness(0, 0, 0, 6) };
        mark.SetResourceReference(FrameworkElement.StyleProperty, "NavRailButton");
        mark.Click += (_, _) =>
        {
            if (_hasScore)
                ScoreRequested?.Invoke();
        };
        return mark;
    }
}
