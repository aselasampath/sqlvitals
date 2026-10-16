using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Connections (#131), bound to its <see cref="ConnectionsCategoryViewModel"/> (#190): the
/// cards two a row, and over them the page its view model has open. Only view code here: the cards'
/// width, and which view shows what is open.
/// </summary>
internal partial class ConnectionsCategory : UserControl
{
    // The narrowest a card gets before the cards go one a row.
    private const double MinCardWidth = 330;

    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(ConnectionsCategory), new PropertyMetadata(MinCardWidth + 14));

    /// <summary>The item after the cards that "+ Add a connection" is drawn for.</summary>
    internal static readonly object AddConnection = new();

    public ConnectionsCategory(ConnectionsCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        Cards.ItemTemplateSelector = new CardTemplates((DataTemplate)Cards.Resources["CardTemplate"], (DataTemplate)Cards.Resources["AddTemplate"]);
        Cards.ItemsSource = new CompositeCollection
        {
            new CollectionContainer { Collection = model.Cards },
            AddConnection,
        };

        model.PropertyChanged += OnModelChanged;
        ShowOpen();

        SizeChanged += (_, _) => UpdateCardWidth();
        Loaded   += (_, _) => model.Activate();
        Unloaded += (_, _) => model.Deactivate();
    }

    public ConnectionsCategoryViewModel ViewModel { get; }

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>The connection's page on screen; null while the cards are shown.</summary>
    internal ConnectionEditor? Editor => EditorHost.Content as ConnectionEditor;

    /// <summary>A connection's Check permissions on screen; null otherwise.</summary>
    internal ConnectionPermissions? Permissions => EditorHost.Content as ConnectionPermissions;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionsCategoryViewModel.Open))
            ShowOpen();
    }

    // A view for what the view model has open, over the cards.
    private void ShowOpen() => EditorHost.Content = ViewModel.Open switch
    {
        ConnectionEditorViewModel editor          => new ConnectionEditor(editor),
        ConnectionPermissionsViewModel permission => new ConnectionPermissions(permission),
        _                                         => null,
    };

    private void UpdateCardWidth()
    {
        var width = Math.Max(0, ActualWidth - 32 - 18 - SystemParameters.VerticalScrollBarWidth);
        if (!double.IsFinite(width) || width == 0)
            return;
        var columns = width >= 2 * (MinCardWidth + 14) ? 2 : 1;
        CardWidth   = Math.Floor(width / columns);
    }

    // A card's template for a card, "+ Add a connection"'s for the item after them.
    private sealed class CardTemplates(DataTemplate card, DataTemplate add) : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => item is ConnectionCardItem ? card : add;
    }
}
