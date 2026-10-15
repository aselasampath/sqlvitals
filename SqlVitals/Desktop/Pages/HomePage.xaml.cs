using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Alerts;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Home (#130), bound to <see cref="HomeViewModel"/> (#189): the code-behind sizes the tiles to
/// fill the row, holds their order while the pointer is over them, and opens a tile's menu and its
/// score's breakdown beside it.
/// </summary>
public partial class HomePage : Page, IPageContext
{
    // Every monitored server's health as its samples arrive (#140); each has its own interval.
    public PageKind Kind => PageKind.Live;

    internal HomeViewModel ViewModel { get; }

    // The narrowest a tile gets before the grid drops a column.
    private const double MinTileWidth = 300;

    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(HomePage), new PropertyMetadata(MinTileWidth + 14));

    // The ⋯ last pressed: the breakdown opens beside it.
    private FrameworkElement? _menuButton;

    public HomePage(HomeViewModel viewModel)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // The tiles, then "+ Add a connection": an item of its own, not a Button among the items,
        // which WPF would show without a template (binding error 26).
        Tiles.ItemTemplateSelector = new TileTemplates((DataTemplate)Tiles.Resources["TileTemplate"], (DataTemplate)Tiles.Resources["AddTemplate"]);
        Tiles.ItemsSource = new CompositeCollection
        {
            new CollectionContainer { Collection = viewModel.Tiles },
            AddConnection,
        };

        SizeChanged += (_, _) => UpdateTileWidth();
        viewModel.MenuRequested += OpenMenu;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.Breakdown))
                ShowBreakdown();
        };
    }

    /// <inheritdoc cref="HomeViewModel.OpenRequested"/>
    public event Action<Guid>? OpenRequested
    {
        add    => ViewModel.OpenRequested += value;
        remove => ViewModel.OpenRequested -= value;
    }

    /// <inheritdoc cref="HomeViewModel.InvestigateRequested"/>
    public event Action<Alert>? InvestigateRequested
    {
        add    => ViewModel.InvestigateRequested += value;
        remove => ViewModel.InvestigateRequested -= value;
    }

    /// <inheritdoc cref="HomeViewModel.AddConnectionRequested"/>
    public event Action? AddConnectionRequested
    {
        add    => ViewModel.AddConnectionRequested += value;
        remove => ViewModel.AddConnectionRequested -= value;
    }

    /// <inheritdoc cref="HomeViewModel.EditRequested"/>
    public event Action<Guid>? EditRequested
    {
        add    => ViewModel.EditRequested += value;
        remove => ViewModel.EditRequested -= value;
    }

    /// <summary>The width of a tile's slot, gap included, so the tiles fill the row.</summary>
    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    /// <summary>The tiles in the order shown, without "+ Add a connection".</summary>
    internal IReadOnlyList<HomeTile> TilesShown => ViewModel.Tiles;

    /// <summary>The item after the tiles that "+ Add a connection" is drawn for.</summary>
    internal static readonly object AddConnection = new();

    // A tile's template for a tile, "+ Add a connection"'s for the item after them.
    private sealed class TileTemplates(DataTemplate tile, DataTemplate add) : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => item is HomeTile ? tile : add;
    }

    private void UpdateTileWidth()
    {
        // The tiles sit inside the page's margins and scrollbar.
        var width   = Math.Max(0, ActualWidth - 20 - 6 - SystemParameters.VerticalScrollBarWidth);
        var columns = Math.Max(1, (int)((width + 0.5) / (MinTileWidth + 14)));
        TileWidth   = Math.Floor(width / columns);
    }

    private void Tiles_MouseEnter(object sender, MouseEventArgs e) => ViewModel.HoldOrder = true;

    private void Tiles_MouseLeave(object sender, MouseEventArgs e) => ViewModel.HoldOrder = false;

    // ── The tile's menu (#141) and its score's breakdown ──────────────────────

    /// <summary>The menu last opened on a tile; for tests.</summary>
    internal ContextMenu? TileMenu { get; private set; }

    // What the view model offers for the tile, under its ⋯.
    private void OpenMenu(HomeTile tile)
    {
        if (MoreButtonOf(tile) is not { } button)
            return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var choice in ViewModel.MenuFor(tile))
        {
            if (choice is null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = choice.Header, IsEnabled = choice.IsEnabled, ToolTip = choice.ToolTip };
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += (_, _) =>
            {
                _menuButton = button;
                choice.Choose();
            };
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => TileMenu = null;
        TileMenu = menu;
        menu.IsOpen = true;
    }

    // The ⋯ on a tile's element.
    private Button? MoreButtonOf(HomeTile tile) =>
        Tiles.ItemContainerGenerator.ContainerFromItem(tile) is DependencyObject container
            ? Descendants(container).OfType<Button>().FirstOrDefault(b => b.Name == "MoreButton")
            : null;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
    }

    // After the menu has closed and let go of the mouse, or the popup closes at once.
    private void ShowBreakdown()
    {
        if (ViewModel.Breakdown is not { } breakdown)
        {
            BreakdownPopup.IsOpen = false;
            return;
        }
        Breakdown.Show(breakdown.Name, breakdown.Score, breakdown.Text, breakdown.Paused);
        if (BreakdownPopup.IsOpen)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ViewModel.Breakdown is null)
                return;
            BreakdownPopup.PlacementTarget = _menuButton;
            BreakdownPopup.IsOpen = true;
        });
    }

    private void BreakdownPopup_Closed(object? sender, EventArgs e) => ViewModel.BreakdownFor = null;
}
