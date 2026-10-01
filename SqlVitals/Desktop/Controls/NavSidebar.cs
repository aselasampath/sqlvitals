using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The sidebar's pages (#124): Home above the groups in <see cref="NavMenu"/> (#130), each group
/// collapsible, then Export / AI and Settings below them. Built into MainWindow's nav panel; which
/// page is open is up to MainWindow.
/// </summary>
internal sealed class NavSidebar
{
    // Every page's button by its tag, and every group's expander by its id.
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, Expander> _groups = new();

    /// <param name="collapsed">The groups the user left collapsed; the others start expanded.</param>
    public NavSidebar(Panel panel, IReadOnlyList<string> collapsed)
    {
        foreach (var page in NavMenu.Header)
            panel.Children.Add(CreateButton(page));

        foreach (var group in NavMenu.Groups)
        {
            var pages = new StackPanel();
            foreach (var page in group.Pages)
                pages.Children.Add(CreateButton(page));

            var expander = new Expander
            {
                Header     = group.Name.ToUpperInvariant(),
                Content    = pages,
                IsExpanded = !collapsed.Contains(group.Id),
                Tag        = group.Id,
            };
            expander.SetResourceReference(FrameworkElement.StyleProperty, "NavGroupExpander");
            AutomationProperties.SetName(expander, group.Name);
            // Also the header's tooltip (see NavGroupExpander).
            AutomationProperties.SetHelpText(expander, group.Question);

            // After IsExpanded is set, so applying the saved state doesn't report it as a change.
            expander.Expanded  += Group_Toggled;
            expander.Collapsed += Group_Toggled;
            expander.KeyDown   += Group_KeyDown;

            _groups[group.Id] = expander;
            panel.Children.Add(expander);
        }

        foreach (var page in NavMenu.Footer)
        {
            var separator = new Separator { Margin = new Thickness(12, 6, 12, 6) };
            separator.SetResourceReference(Control.BackgroundProperty, "NavSeparator");
            panel.Children.Add(separator);
            panel.Children.Add(CreateButton(page));
        }
    }

    /// <summary>A page's button was clicked, with the page's tag.</summary>
    public event Action<string>? PageRequested;

    /// <summary>A group was collapsed (true) or expanded (false): by the user, or by <see cref="Highlight"/>.</summary>
    public event Action<string, bool>? GroupToggled;

    /// <summary>A page's button, e.g. to disable it or show a count on it.</summary>
    public Button Button(string tag) => _buttons[tag];

    public Expander Group(string id) => _groups[id];

    public bool HasPage(string tag) => _buttons.ContainsKey(tag);

    /// <summary>
    /// Highlights the page's button and shows it: its group expands if it was collapsed, as if
    /// clicked, and marks itself as holding the current page if the user collapses it again.
    /// </summary>
    public void Highlight(string tag)
    {
        // By resource reference, so switching theme restyles the buttons at once.
        foreach (var button in _buttons.Values)
            button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
        var active = Button(tag);
        active.SetResourceReference(FrameworkElement.StyleProperty, "NavButtonActive");

        var groupId = NavMenu.GroupOf(tag)?.Id;
        foreach (var (id, expander) in _groups)
        {
            NavGroupState.SetHoldsCurrentPage(expander, id == groupId);
            if (id == groupId)
                expander.IsExpanded = true;
        }
        active.BringIntoView();
    }

    private Button CreateButton(NavPage page)
    {
        var button = new Button { Content = page.Content, Tag = page.Tag, ToolTip = page.ToolTip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
        // The name without the icon, which a screen reader would otherwise read out.
        AutomationProperties.SetName(button, page.Label);
        button.Click += (_, _) => PageRequested?.Invoke(page.Tag);
        _buttons[page.Tag] = button;
        return button;
    }

    private void Group_Toggled(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is Expander { Tag: string id } expander)
            GroupToggled?.Invoke(id, !expander.IsExpanded);
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
