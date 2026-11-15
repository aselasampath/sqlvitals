using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The saved servers as a menu, to switch to another: the breadcrumb's server part (#141) and the
/// sidebar's server card (#254) open the same one.
/// </summary>
internal static class ServerMenus
{
    /// <summary>
    /// Opens it under <paramref name="anchor"/>: every saved server with its health dot and score,
    /// the open one ticked, then All servers.
    /// </summary>
    /// <param name="pick">A server was picked, the open one included.</param>
    /// <param name="allServers">All servers was picked.</param>
    /// <param name="closed">It closed, hooked before it opens, so a menu that closes at once is still heard.</param>
    public static ContextMenu Open(FrameworkElement anchor, IReadOnlyList<ConnectionSelectorItem> servers, Guid current,
                                  Action<Guid> pick, Action allServers, Action closed)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var server in servers)
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
            item.Click += (_, _) => pick(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = Breadcrumb.AllServers };
        all.Click += (_, _) => allServers();
        menu.Items.Add(all);

        menu.Closed += (_, _) => closed();
        menu.IsOpen = true;
        return menu;
    }
}
