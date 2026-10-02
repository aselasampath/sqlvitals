using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Pages.Settings;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Settings (#131): a side menu of categories in groups, and the category beside it. Each category
/// is built the first time it's opened. Connections holds a card per saved connection and a page
/// per connection; the categories for the whole app save as they change.
/// </summary>
public partial class SettingsPage : Page
{
    private readonly SettingsContext _context;
    private readonly Dictionary<SettingsCategory, FrameworkElement> _built = new();
    private readonly Dictionary<SettingsCategory, RadioButton> _items = new();
    private TextBlock? _connectionsCount;

    // Set while a menu item is checked in code, so it isn't a click.
    private bool _isSelecting;
    private SettingsCategory _category;

    /// <param name="applyConnections">
    /// Tells the running app the saved connections changed; true asks it to open Live Metrics afterwards.
    /// </param>
    /// <param name="location">Where it opens; Connections when null.</param>
    /// <param name="message">Shown above the category, e.g. "Switched to UAT." or a password prompt.</param>
    public SettingsPage(
        ConnectionSettingsService service,
        MonitoringManager monitoring,
        ChatNotificationService chat,
        Func<bool, Task> applyConnections,
        SettingsLocation? location = null,
        string? message = null)
    {
        InitializeComponent();
        _context = new SettingsContext(service, monitoring, chat, applyConnections,
                                       (text, caption, buttons, icon) => Prompt(text, caption, buttons, icon),
                                       target => _ = OpenAsync(target),
                                       id => ServerRequested?.Invoke(id));
        BuildMenu();
        UpdateCount();

        // Nothing is open yet, so there's nothing to leave: this completes straight away.
        _ = OpenAsync(location ?? new SettingsLocation(SettingsCategory.Connections));
        ShowMessage(message);

        Loaded   += (_, _) => _context.Service.Changed += OnSettingsChanged;
        Unloaded += (_, _) => _context.Service.Changed -= OnSettingsChanged;
    }

    /// <summary>"View on Home" on a connection's page: open that server's detail page (#130).</summary>
    public event Action<Guid>? ServerRequested;

    /// <summary>The theme was switched: open Settings again on Appearance, in the new theme.</summary>
    public event Action? ThemeChanged;

    /// <summary>A category was opened, so Settings can open on it again this session.</summary>
    public event Action<SettingsCategory>? CategoryShown;

    /// <summary>Asks the user; tests answer instead of a message box.</summary>
    internal Ask Prompt { get; set; } = (text, caption, buttons, icon) =>
        MessageBox.Show(text, caption, buttons, icon,
                        buttons == MessageBoxButton.YesNoCancel ? MessageBoxResult.Cancel : MessageBoxResult.No);

    /// <summary>The category on screen.</summary>
    public SettingsCategory Category => _category;

    /// <summary>The category's control on screen.</summary>
    internal FrameworkElement? Shown => CategoryHost.Content as FrameworkElement;

    /// <summary>Connections, once it has been opened.</summary>
    internal ConnectionsCategory? Connections => _built.GetValueOrDefault(SettingsCategory.Connections) as ConnectionsCategory;

    internal RadioButton MenuItemFor(SettingsCategory category) => _items[category];

    /// <summary>
    /// Whether Settings can be left for another page. With unsaved changes on a connection's page
    /// or the default thresholds, it asks to save, discard or stay; false when the user stays.
    /// </summary>
    public Task<bool> TryLeaveAsync() =>
        Shown is ISettingsCategory category ? category.TryLeaveAsync() : Task.FromResult(true);

    /// <summary>Opens a category, or a connection's tab, leaving the one on screen first.</summary>
    public async Task OpenAsync(SettingsLocation location)
    {
        if (Shown is not null && location.Category != _category && !await TryLeaveAsync())
        {
            Select(_category);
            return;
        }

        if (!_built.TryGetValue(location.Category, out var control))
            _built[location.Category] = control = Build(location.Category);
        else if (!ReferenceEquals(control, Shown) && control is ISettingsCategory again)
            again.OnShownAgain();

        _category = location.Category;
        Select(_category);
        CategoryHost.Content = control;
        AutomationProperties.SetName(CategoryHost, SettingsNav.Info(_category).Title);
        CategoryShown?.Invoke(_category);

        if (control is ConnectionsCategory connections)
            await connections.ShowAsync(location);
    }

    /// <summary>A message above the category; null or empty hides it.</summary>
    public void ShowMessage(string? message)
    {
        TxtBanner.Text    = message ?? string.Empty;
        Banner.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseBanner_Click(object sender, RoutedEventArgs e) => ShowMessage(null);

    // ── Side menu ─────────────────────────────────────────────────────────────

    private void BuildMenu()
    {
        foreach (var group in SettingsNav.Groups)
        {
            var heading = new TextBlock
            {
                Text       = group.Name.ToUpperInvariant(),
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(12, Menu.Children.Count == 0 ? 6 : 16, 0, 4),
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            Menu.Children.Add(heading);

            foreach (var info in group.Categories)
            {
                var title = new TextBlock { Text = info.Title, VerticalAlignment = VerticalAlignment.Center };
                var row   = new DockPanel();
                if (info.Id == SettingsCategory.Connections)
                {
                    _connectionsCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    DockPanel.SetDock(_connectionsCount, Dock.Right);
                    row.Children.Add(_connectionsCount);
                }
                row.Children.Add(title);

                var item = new RadioButton { Content = row, Tag = info.Id, Style = (Style)Resources["MenuItem"] };
                AutomationProperties.SetName(item, info.Title);
                item.Click   += MenuItem_Click;
                item.KeyDown += MenuItem_KeyDown;
                _items[info.Id] = item;
                Menu.Children.Add(item);
            }
        }
    }

    // A click or Space; on the category already open it goes back to its start, e.g. from a
    // connection's page to the cards.
    private async void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelecting || sender is not RadioButton { Tag: SettingsCategory category })
            return;
        await OpenAsync(new SettingsLocation(category));
    }

    private void MenuItem_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not RadioButton item)
            return;
        item.IsChecked = true;
        MenuItem_Click(item, e);
        e.Handled = true;
    }

    private void Select(SettingsCategory category)
    {
        _isSelecting = true;
        try { _items[category].IsChecked = true; }
        finally { _isSelecting = false; }
    }

    // "Connections  5".
    private void UpdateCount()
    {
        var count = _context.Service.Load().Connections.Count;
        if (_connectionsCount is not null)
            _connectionsCount.Text = count.ToString();
        AutomationProperties.SetName(_items[SettingsCategory.Connections],
            count == 1 ? "Connections, 1 saved" : $"Connections, {count} saved");
    }

    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(UpdateCount);

    private FrameworkElement Build(SettingsCategory category)
    {
        switch (category)
        {
            case SettingsCategory.Connections:      return new ConnectionsCategory(_context);
            case SettingsCategory.Monitoring:       return new MonitoringCategory(_context);
            case SettingsCategory.Thresholds:       return new ThresholdsCategory(_context);
            case SettingsCategory.Alerts:           return new AlertsCategory(_context);
            case SettingsCategory.NotificationArea: return new NotificationAreaCategory(_context);
            case SettingsCategory.Activity:         return new ActivityCategory(_context);
            default:
                var appearance = new AppearanceCategory(_context);
                appearance.ThemeChanged += () => ThemeChanged?.Invoke();
                return appearance;
        }
    }
}
