using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Connections (#131): a card per saved connection, in the sidebar selector's order,
/// and a connection's page over them while one is open. Reads the sessions; contacts a server
/// only when Test is pressed.
/// </summary>
internal partial class ConnectionsCategory : UserControl, ISettingsCategory
{
    // The narrowest a card gets before the cards go one a row.
    private const double MinCardWidth = 330;

    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(ConnectionsCategory), new PropertyMetadata(MinCardWidth + 14));

    private readonly SettingsContext _context;
    private readonly ObservableCollection<ConnectionCardItem> _cards = new();

    // The settings as of the last build; re-read when they change, not per sample.
    private ConnectionStore _store = new();

    public ConnectionsCategory(SettingsContext context)
    {
        _context = context;
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Connections).Subtitle;

        var addCard = new Button
        {
            Content   = "+ Add a connection",
            Margin    = new Thickness(0, 0, 14, 14),
            MinHeight = 150,
            ToolTip   = "Add a SQL Server connection",
            Style     = (Style)FindResource("AddCard"),
        };
        addCard.Click += New_Click;
        Cards.ItemsSource = new CompositeCollection
        {
            new CollectionContainer { Collection = _cards },
            addCard,
        };

        Build();

        SizeChanged += (_, _) => UpdateCardWidth();
        Loaded += (_, _) =>
        {
            _context.Monitoring.SessionStateChanged += OnSessionStateChanged;
            _context.Monitoring.MonitoringChanged   += Rebuild;
            _context.Service.Changed                += OnSettingsChanged;
            Build();
        };
        Unloaded += (_, _) =>
        {
            _context.Monitoring.SessionStateChanged -= OnSessionStateChanged;
            _context.Monitoring.MonitoringChanged   -= Rebuild;
            _context.Service.Changed                -= OnSettingsChanged;
        };
    }

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>The cards in the order shown, without "+ Add a connection".</summary>
    internal IReadOnlyList<ConnectionCardItem> CardsShown => _cards;

    /// <summary>The connection's page that is open; null while the cards are shown.</summary>
    internal ConnectionEditor? Editor { get; private set; }

    /// <summary>A connection's Check permissions (#177) that is open; null otherwise.</summary>
    internal ConnectionPermissions? Permissions { get; private set; }

    public async Task<bool> TryLeaveAsync() => Editor is null || await Editor.TryCloseAsync();

    /// <summary>
    /// Shows the cards, a connection's tab, or a new connection's page. Leaving a page with
    /// unsaved changes asks first, and stays when the user says so.
    /// </summary>
    public async Task ShowAsync(SettingsLocation location)
    {
        // Already on that connection's page: only the tab changes.
        if (Editor is { } open && !location.NewConnection && location.ConnectionId is { } same && open.ConnectionId == same)
        {
            open.SelectTab(location.Tab);
            return;
        }

        if (!await TryLeaveAsync())
            return;

        if (location.NewConnection)
            OpenEditor(null, ConnectionTab.Connection);
        else if (location.ConnectionId is { } id && _context.Service.Load().Connections.Any(c => c.Id == id))
            OpenEditor(id, location.Tab);
        else
            ShowList(null);
    }

    // ── The cards ─────────────────────────────────────────────────────────────

    private void ShowList(string? status)
    {
        Editor = null;
        Permissions = null;
        EditorHost.Content    = null;
        EditorHost.Visibility = Visibility.Collapsed;
        ListView.Visibility   = Visibility.Visible;
        Build();
        SettingsContext.ShowStatus(TxtStatus, status ?? string.Empty, success: status is null ? null : true);
    }

    private void Build()
    {
        _store = _context.Service.Load();
        Rebuild();
    }

    // From the settings as last read, and the sessions as they stand.
    private void Rebuild()
    {
        var built = ConnectionCard.Order(_store.Connections.Select(c => ConnectionCard.From(InputFor(c, _store, _context.Monitoring))));

        // In place while the same cards are in the same order, so a sample doesn't recreate them
        // and take the keyboard focus off a button.
        if (built.Select(c => c.Id).SequenceEqual(_cards.Select(c => c.Card.Id)))
        {
            for (var i = 0; i < built.Count; i++)
                _cards[i].Update(built[i]);
        }
        else
        {
            var byId = _cards.ToDictionary(c => c.Card.Id);
            _cards.Clear();
            foreach (var card in built)
            {
                if (byId.TryGetValue(card.Id, out var existing))
                {
                    existing.Update(card);
                    _cards.Add(existing);
                }
                else
                    _cards.Add(new ConnectionCardItem(card, _context.CheckPermissions is null ? null : new RelayCommand(() => OpenCheck(card.Id))));
            }
        }

        EmptyState.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Cards.Visibility      = _cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnNew.Visibility     = _cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>What a connection's card shows, from its settings and its session.</summary>
    internal static ConnectionCardInput InputFor(ConnectionSettings conn, ConnectionStore store, MonitoringManager monitoring)
    {
        var session = monitoring.Get(conn.Id);
        var running = session is { IsRunning: true };
        return new ConnectionCardInput(
            conn.Id, conn.DisplayName, conn.Server, conn.DatabaseLabel, SignInLabel(conn.Authentication),
            IsActive:            conn.Id == store.ActiveConnectionId,
            MonitorInBackground: conn.MonitorInBackground,
            OwnThresholds:       conn.HealthThresholds is not null,
            IsMonitored:         running,
            Score:               running ? session!.Score : null);
    }

    /// <summary>"SQL login", "Entra MFA", "Windows auth": short enough for a tag.</summary>
    internal static string SignInLabel(SqlAuthMode mode) => mode switch
    {
        SqlAuthMode.EntraMfa => "Entra MFA",
        SqlAuthMode.Windows  => "Windows auth",
        _                    => "SQL login",
    };

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (Editor is null)
            Rebuild();
    }

    // A connection added, removed, renamed or made active. After the save has been applied.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(Build);

    private void UpdateCardWidth()
    {
        var width   = Math.Max(0, ActualWidth - 32 - 18 - SystemParameters.VerticalScrollBarWidth);
        if (!double.IsFinite(width) || width == 0)
            return;
        var columns = width >= 2 * (MinCardWidth + 14) ? 2 : 1;
        CardWidth   = Math.Floor(width / columns);
    }

    private void New_Click(object sender, RoutedEventArgs e) => OpenEditor(null, ConnectionTab.Connection);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionCardItem item })
            OpenEditor(item.Card.Id, ConnectionTab.Connection);
    }

    // Tries to sign in with the saved details; the result replaces the card's state until the next build.
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConnectionCardItem item })
            return;
        var conn = _context.Service.Load().Connections.FirstOrDefault(c => c.Id == item.Card.Id);
        if (conn is null)
            return;
        if (conn.NeedsPassword)
        {
            item.ShowTest("The password isn't saved: Edit to enter it.", success: false);
            return;
        }

        item.ShowTest(conn.Authentication == SqlAuthMode.EntraMfa ? "Testing… complete the Microsoft sign-in." : "Testing…", success: null);
        var error = await ConnectionTest.TryOpenAsync(conn.ConnectionString);
        item.ShowTest(error is null ? "✓ Connection successful" : $"✗ {error}", success: error is null);
    }

    // ── A connection's page ───────────────────────────────────────────────────

    /// <summary>Check permissions on a card (#177): the check's page over the cards, checking as it opens.</summary>
    internal void OpenCheck(Guid id)
    {
        if (Editor is not null || _context.CheckPermissions is not { } check ||
            _context.Service.Load().Connections.FirstOrDefault(c => c.Id == id) is not { } conn)
            return;

        var page = new ConnectionPermissions(conn, check, _context.Clipboard);
        page.Closed += () => ShowList(null);
        Permissions = page;
        EditorHost.Content    = page;
        EditorHost.Visibility = Visibility.Visible;
        ListView.Visibility   = Visibility.Collapsed;
        SettingsContext.ShowStatus(TxtStatus, string.Empty, null);
        _ = page.Check.Model.CheckAsync();   // says its own errors
    }

    private void OpenEditor(Guid? id, ConnectionTab tab)
    {
        var editor = new ConnectionEditor(_context, id);
        editor.SelectTab(tab);
        editor.Closed  += () => ShowList(null);
        editor.Removed += message => ShowList(message);

        Editor = editor;
        EditorHost.Content    = editor;
        EditorHost.Visibility = Visibility.Visible;
        ListView.Visibility   = Visibility.Collapsed;
        SettingsContext.ShowStatus(TxtStatus, string.Empty, null);
    }
}

/// <summary>A connection's card in Settings → Connections (#131), with the colours and a Test result.</summary>
public sealed class ConnectionCardItem : INotifyPropertyChanged
{
    /// <summary>How long a Test result stays in place of the state; the next sample after puts the state back.</summary>
    public static readonly TimeSpan TestShownFor = TimeSpan.FromSeconds(30);

    private string?  _testText;
    private bool?    _testSucceeded;
    private bool     _testing;
    private DateTime _testShownUntil;

    /// <param name="checkPermissions">Opens its Check permissions (#177); null hides the button.</param>
    public ConnectionCardItem(ConnectionCard card, ICommand? checkPermissions = null)
    {
        Card = card;
        CheckPermissionsCommand = checkPermissions;
    }

    public ConnectionCard Card { get; private set; }

    public ICommand? CheckPermissionsCommand { get; }

    public Visibility CheckPermissionsVisibility => CheckPermissionsCommand is null ? Visibility.Collapsed : Visibility.Visible;

    public string CheckPermissionsName => $"Check the permissions of {Card.Name}";

    private string? TestResult => _testing || DateTime.UtcNow < _testShownUntil ? _testText : null;

    public Brush StatusBrush => HealthBrushes.For(Card.Status);

    public Brush RoleTint => Card.Role switch
    {
        ConnectionRole.Active     => ThemeResources.BrushOf("ConnectionActiveTint"),
        ConnectionRole.Background => ThemeResources.BrushOf("ConnectionBackgroundTint"),
        _                         => HealthBrushes.Tint(TileStatus.Off),
    };

    /// <summary>The state, or the result of Test while there is one.</summary>
    public string StateText => TestResult ?? Card.StateText;

    public string StateToolTip => TestResult ?? Card.StateText;

    public Brush StateBrush => TestResult is null
        ? Card.Status is TileStatus.Off or TileStatus.Waiting ? HealthBrushes.Grey : HealthBrushes.For(Card.Status)
        : _testSucceeded switch { true => HealthBrushes.Green, false => HealthBrushes.Red, _ => HealthBrushes.Grey };

    public bool CanTest => !_testing;

    public string TestName => $"Test {Card.Name}";
    public string EditName => $"Edit {Card.Name}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ConnectionCard card)
    {
        Card = card;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <param name="success">Null while the test is running.</param>
    public void ShowTest(string text, bool? success)
    {
        _testText       = text;
        _testSucceeded  = success;
        _testing        = success is null;
        _testShownUntil = DateTime.UtcNow + TestShownFor;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
