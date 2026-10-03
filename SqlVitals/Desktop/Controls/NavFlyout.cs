using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A group's pages beside its icon in the collapsed sidebar (#142): the group's name, its
/// question, its pages, and *Pin the menu open*. ↑ and ↓ move between them; Esc or ← closes it.
/// </summary>
internal sealed class NavFlyout : Border
{
    public const double FlyoutWidth = 240;

    private readonly List<Button> _pages = new();
    private readonly string? _current;

    /// <param name="current">The page on screen, highlighted if it is in the group.</param>
    /// <param name="sidebarButton">The pinned sidebar's button for a page: a page this server
    /// lacks is disabled there, with the reason as its tooltip, and is the same here.</param>
    public NavFlyout(NavGroup group, string? current, Func<string, Button> sidebarButton)
    {
        GroupId  = group.Id;
        _current = current;
        Width = FlyoutWidth;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(0, 10, 0, 6);
        SetResourceReference(BackgroundProperty, "BgSidebar");
        SetResourceReference(BorderBrushProperty, "Border");
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };

        var body = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(body, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);

        var name = new TextBlock { Text = group.Name, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 0, 14, 2) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var question = new TextBlock { Text = group.Question, FontFamily = new FontFamily("Segoe UI"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 14, 8) };
        question.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        body.Children.Add(name);
        body.Children.Add(question);

        foreach (var page in group.Pages)
        {
            var source = sidebarButton(page.Tag);
            var button = PageButton(page, page.Tag == current);
            button.IsEnabled = source.IsEnabled;
            button.ToolTip   = source.ToolTip;
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += (_, _) => PageRequested?.Invoke(page.Tag);
            _pages.Add(button);
            body.Children.Add(button);
        }

        var line = new Border { Height = 1, Margin = new Thickness(12, 6, 12, 4) };
        line.SetResourceReference(BackgroundProperty, "Border");
        body.Children.Add(line);

        PinButton = PageButton(new NavPage("Pin", "\uE718", "Pin the menu open"), active: false);
        PinButton.ToolTip = "Show the sidebar with its labels again";
        PinButton.Click += (_, _) => PinRequested?.Invoke();
        body.Children.Add(PinButton);

        Child = body;
        AutomationProperties.SetName(this, $"{group.Name} pages");
        PreviewKeyDown += Flyout_PreviewKeyDown;
    }

    /// <summary>A page was picked.</summary>
    public event Action<string>? PageRequested;

    /// <summary>*Pin the menu open* was picked.</summary>
    public event Action? PinRequested;

    /// <summary>Esc or ← was pressed: close, and go back to the group's icon.</summary>
    public event Action? CloseRequested;

    public string GroupId { get; }

    /// <summary>The group's pages, in order.</summary>
    public IReadOnlyList<Button> Pages => _pages;

    public Button PinButton { get; }

    /// <summary>Where the keyboard goes in: the page on screen if it is here, else the first page that can open.</summary>
    public Button FocusTarget =>
        _pages.FirstOrDefault(p => p.IsEnabled && (string)p.Tag == _current)
        ?? _pages.FirstOrDefault(p => p.IsEnabled) ?? PinButton;

    private void Flyout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Left))
            return;
        e.Handled = true;
        CloseRequested?.Invoke();
    }

    private static Button PageButton(NavPage page, bool active)
    {
        var content = new DockPanel { LastChildFill = true };
        var icon = new TextBlock { Text = page.Icon, FontFamily = new FontFamily(NavMenu.IconFont), FontSize = 16, Width = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = page.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button { Content = content, Tag = page.Tag };
        button.SetResourceReference(StyleProperty, active ? "NavButtonActive" : "NavButton");
        AutomationProperties.SetName(button, page.Label);
        return button;
    }
}
