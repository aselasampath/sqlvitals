using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Connections (#131): a card per saved connection, in the sidebar selector's order,
/// and a connection's page over them while one is open, or its Check permissions (#177). Reads the
/// sessions; contacts a server only when Test is pressed.
/// </summary>
public sealed partial class ConnectionsCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;

    // The settings as of the last build; re-read when they change, not per sample.
    private ConnectionStore _store = new();

    internal ConnectionsCategoryViewModel(SettingsContext context)
    {
        _context = context;
        Build();
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Connections).Subtitle;

    /// <summary>The cards in the order shown, without "+ Add a connection".</summary>
    public ObservableCollection<ConnectionCardItem> Cards { get; } = [];

    [ObservableProperty]
    private bool _hasCards;

    /// <summary>"Removed "Quiet"." over the cards.</summary>
    public StatusLine Status { get; } = new();

    /// <summary>What is over the cards: a connection's page, its Check permissions, or null for the cards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Editor), nameof(Permissions), nameof(ShowsCards))]
    private object? _open;

    /// <summary>The connection's page that is open; null while the cards are shown.</summary>
    public ConnectionEditorViewModel? Editor => Open as ConnectionEditorViewModel;

    /// <summary>A connection's Check permissions (#177) that is open; null otherwise.</summary>
    public ConnectionPermissionsViewModel? Permissions => Open as ConnectionPermissionsViewModel;

    public bool ShowsCards => Open is null;

    public override async Task<bool> TryLeaveAsync() => Editor is null || await Editor.TryCloseAsync();

    /// <summary>
    /// Shows the cards, a connection's tab, or a new connection's page. Leaving a page with
    /// unsaved changes asks first, and stays when the user says so.
    /// </summary>
    [ReportsItsErrors]
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

    /// <summary>The cards again, with what to say over them; null says nothing.</summary>
    public void ShowList(string? status)
    {
        Open = null;
        Build();
        Status.Show(status ?? string.Empty, success: status is null ? null : true);
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
        if (built.Select(c => c.Id).SequenceEqual(Cards.Select(c => c.Card.Id)))
        {
            for (var i = 0; i < built.Count; i++)
                Cards[i].Update(built[i]);
        }
        else
        {
            var byId = Cards.ToDictionary(c => c.Card.Id);
            Cards.Clear();
            foreach (var card in built)
            {
                if (byId.TryGetValue(card.Id, out var existing))
                {
                    existing.Update(card);
                    Cards.Add(existing);
                }
                else
                    Cards.Add(NewCard(card));
            }
        }
        HasCards = Cards.Count > 0;
    }

    private ConnectionCardItem NewCard(ConnectionCard card)
    {
        var id = card.Id;
        ConnectionCardItem? item = null;
        item = new ConnectionCardItem(card,
            edit: new RelayCommand(() => OpenEditor(id, ConnectionTab.Connection)),
            test: new AsyncRelayCommand(() => TestAsync(item!)),
            checkPermissions: _context.CheckPermissions is null ? null : new RelayCommand(() => OpenCheck(id)));
        return item;
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

    protected override void OnActivated()
    {
        _context.Monitoring.SessionStateChanged += OnSessionStateChanged;
        _context.Monitoring.MonitoringChanged   += Rebuild;
        _context.Service.Changed                += OnSettingsChanged;
        Build();
    }

    protected override void OnDeactivated()
    {
        _context.Monitoring.SessionStateChanged -= OnSessionStateChanged;
        _context.Monitoring.MonitoringChanged   -= Rebuild;
        _context.Service.Changed                -= OnSettingsChanged;
    }

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (Open is null)
            Rebuild();
    }

    // A connection added, removed, renamed or made active. After the save has been applied.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => _context.Ui.Post(Build);

    /// <summary>"+ New connection" and "+ Add a connection".</summary>
    [RelayCommand]
    private void New() => OpenEditor(null, ConnectionTab.Connection);

    // Tries to sign in with the saved details; the result replaces the card's state until the next build.
    [ReportsItsErrors]
    private async Task TestAsync(ConnectionCardItem item)
    {
        var conn = _context.Service.Load().Connections.FirstOrDefault(c => c.Id == item.Card.Id);
        if (conn is null)
            return;
        if (conn.NeedsPassword)
        {
            item.ShowTest("The password isn't saved: Edit to enter it.", success: false);
            return;
        }

        item.ShowTest(conn.Authentication == SqlAuthMode.EntraMfa ? "Testing… complete the Microsoft sign-in." : "Testing…", success: null);
        var error = await _context.TestConnection(conn.ConnectionString);
        item.ShowTest(error is null ? "✓ Connection successful" : $"✗ {error}", success: error is null);
    }

    // ── A connection's page ───────────────────────────────────────────────────

    /// <summary>Check permissions on a card (#177): the check's page over the cards, checking as it opens.</summary>
    internal void OpenCheck(Guid id)
    {
        if (Editor is not null || _context.CheckPermissions is not { } check ||
            _context.Service.Load().Connections.FirstOrDefault(c => c.Id == id) is not { } conn)
            return;

        var page = new ConnectionPermissionsViewModel(conn, check, _context.Clipboard);
        page.Closed += () => ShowList(null);
        Open = page;
        Status.Clear();
        _ = page.Check.CheckAsync();   // says its own errors
    }

    private void OpenEditor(Guid? id, ConnectionTab tab)
    {
        var editor = new ConnectionEditorViewModel(_context, id);
        editor.SelectTab(tab);
        editor.Closed  += () => ShowList(null);
        editor.Removed += ShowList;

        Open = editor;
        Status.Clear();
    }
}
