using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Data.SqlClient;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// A connection's page in Settings → Connections (#131), or a new connection's: tabs Connection,
/// Monitoring &amp; alerts, Health thresholds and Advanced, and one bar that saves them all.
/// Moving between tabs keeps what's typed; leaving with unsaved changes asks first.
/// </summary>
internal partial class ConnectionEditor : UserControl
{
    private readonly SettingsContext _context;

    // The connection being edited; null until a new one is saved.
    private Guid? _id;

    // The settings as of the last load or save, and the page as it was then.
    private ConnectionStore _store;
    private ConnectionDraft _saved;

    // Set while the fields are filled in code, so that isn't a change.
    private bool _loading;
    private bool _busy;

    // Connection string the database list was last loaded with, so it's only queried again for another server.
    private string? _databaseListSource;
    private bool    _isLoadingDatabases;

    public ConnectionEditor(SettingsContext context, Guid? connectionId)
    {
        _context = context;
        InitializeComponent();

        // Any field on any tab: they all bubble up here.
        AddHandler(TextBoxBase.TextChangedEvent,     new TextChangedEventHandler((_, _) => OnFieldChanged()));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((_, _) => OnFieldChanged()));
        AddHandler(ToggleButton.CheckedEvent,        new RoutedEventHandler((_, _) => OnFieldChanged()));
        AddHandler(ToggleButton.UncheckedEvent,      new RoutedEventHandler((_, _) => OnFieldChanged()));
        AddHandler(Selector.SelectionChangedEvent,   new SelectionChangedEventHandler((_, e) =>
        {
            // Not a tab being picked.
            if (!ReferenceEquals(e.OriginalSource, Tabs))
                OnFieldChanged();
        }));
        ConnectionThresholds.Changed += OnFieldChanged;

        _store = _context.Service.Load();
        _id    = _store.Connections.Any(c => c.Id == connectionId) ? connectionId : null;
        _saved = Load(Saved());

        Loaded += (_, _) =>
        {
            _context.Monitoring.SessionStateChanged += OnSessionStateChanged;
            _context.Service.Changed                += OnSettingsChanged;
            RenderHeader();
            UpdatePreview();
        };
        Unloaded += (_, _) =>
        {
            _context.Monitoring.SessionStateChanged -= OnSessionStateChanged;
            _context.Service.Changed                -= OnSettingsChanged;
        };
    }

    /// <summary>"← Connections", with nothing left unsaved.</summary>
    public event Action? Closed;

    /// <summary>The connection was removed, with what to say about it on the cards.</summary>
    public event Action<string>? Removed;

    /// <summary>The connection on the page; null for a new one not saved yet.</summary>
    public Guid? ConnectionId => _id;

    public bool IsNew => _id is null;

    /// <summary>Whether anything on any tab differs from what's saved.</summary>
    public bool IsDirty => !ReadDraft().Equals(_saved);

    public ConnectionTab SelectedTab => (ConnectionTab)Math.Max(0, Tabs.SelectedIndex);

    public void SelectTab(ConnectionTab tab) => Tabs.SelectedIndex = (int)tab;

    // ── Loading ───────────────────────────────────────────────────────────────

    private ConnectionSettings Saved() =>
        _store.Connections.FirstOrDefault(c => c.Id == _id)?.Clone() ?? new ConnectionSettings();

    // Fills every tab from the settings, and returns the page as it now stands: what's saved.
    private ConnectionDraft Load(ConnectionSettings settings)
    {
        _loading = true;
        try
        {
            TxtConnectionName.Text              = settings.Name;
            TxtServer.Text                      = settings.Server;
            SelectByTag(CmbAuthentication, settings.Authentication.ToString());
            TxtUserName.Text                    = settings.UserName;
            TxtPassword.Password                = settings.Password;
            ChkSavePassword.IsChecked           = settings.SavePassword;
            CmbDatabase.ItemsSource             = null;
            CmbDatabase.Text                    = settings.Database;
            SelectByTag(CmbEncrypt, settings.Encrypt.ToString());
            ChkTrustServerCertificate.IsChecked = settings.TrustServerCertificate;
            TxtConnectTimeout.Text              = settings.ConnectTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
            TxtAdditionalParameters.Text        = settings.AdditionalParameters;
            ChkMonitorInBackground.IsChecked    = settings.MonitorInBackground;
            ChkNotify.IsChecked                 = _id is not { } id || _store.NotifiesFor(id);

            // Starts from the shared thresholds, so turning the switch on begins from what the dot uses now.
            ConnectionThresholds.Defaults = _store.HealthThresholds;
            ConnectionThresholds.Show(settings.HealthThresholds ?? _store.HealthThresholds);
            ChkCustomThresholds.IsChecked = settings.HealthThresholds is not null;

            _databaseListSource = null;
            ApplyAuthenticationLayout();
            ApplyThresholdsMode();
            ShowChannels();
        }
        finally
        {
            _loading = false;
        }

        RenderHeader();
        UpdatePreview();
        var draft = ReadDraft();
        UpdateBar(draft, draft);
        return draft;
    }

    private ConnectionDraft ReadDraft()
    {
        var own = ChkCustomThresholds.IsChecked == true;
        return new ConnectionDraft(
            Name:                   TxtConnectionName.Text.Trim(),
            Server:                 TxtServer.Text.Trim(),
            Authentication:         SelectedAuthentication(),
            UserName:               TxtUserName.Text.Trim(),
            Password:               TxtPassword.Password,
            SavePassword:           ChkSavePassword.IsChecked == true,
            Database:               CmbDatabase.Text.Trim(),
            Encrypt:                SelectedEncrypt(),
            TrustServerCertificate: ChkTrustServerCertificate.IsChecked == true,
            ConnectTimeout:         TxtConnectTimeout.Text.Trim(),
            AdditionalParameters:   TxtAdditionalParameters.Text.Trim(),
            MonitorInBackground:    ChkMonitorInBackground.IsChecked == true,
            Notifies:               ChkNotify.IsChecked == true,
            OwnThresholds:          own,
            Thresholds:             own ? ConnectionThresholds.Snapshot : string.Empty);
    }

    private void OnFieldChanged()
    {
        if (_loading)
            return;
        UpdatePreview();
        UpdateBar(ReadDraft(), _saved);
    }

    // "● Unsaved changes on Connection and Health thresholds", and which buttons apply.
    private void UpdateBar(ConnectionDraft draft, ConnectionDraft saved)
    {
        var tabs  = draft.ChangedTabs(saved);
        var dirty = tabs.Count > 0;
        TxtUnsaved.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        TxtUnsaved.Text = dirty
            ? "●  Unsaved changes on " + JoinAnd(tabs.Select(SettingsNav.Title).ToList())
            : string.Empty;

        var active = _id is { } id && _store.ActiveConnectionId == id;
        BtnDiscard.IsEnabled        = dirty && !_busy;
        BtnSave.IsEnabled           = (dirty || IsNew) && !_busy;
        BtnSaveAndConnect.IsEnabled = !_busy;
        BtnSaveAndConnect.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        BtnTest.IsEnabled           = !_busy;
        BtnMore.IsEnabled           = !_busy;
        OwnTag.Visibility           = draft.OwnThresholds ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    // ── Header ────────────────────────────────────────────────────────────────

    // The name, its status and score, and where it points: as saved, not as typed.
    private void RenderHeader()
    {
        var conn = _store.Connections.FirstOrDefault(c => c.Id == _id);
        if (conn is null)
        {
            TxtName.Text = RunCrumb.Text = "New connection";
            Dot.Visibility = StatusChip.Visibility = ActiveChip.Visibility = Visibility.Collapsed;
            TxtLine.Text = "Enter the server's details, then Save. SqlVitals signs in to check them first.";
            TxtViewOnHome.Visibility = BtnMore.Visibility = Visibility.Collapsed;
            BtnSave.ToolTip = "Save this connection without switching to it";
            return;
        }

        var tile = ServerTile.From(HomePage.InputFor(conn, _store, _context.Monitoring), DateTime.Now);
        TxtName.Text = RunCrumb.Text = conn.DisplayName;
        Dot.Visibility            = Visibility.Visible;
        Dot.Fill                  = HealthBrushes.For(tile.Status);
        StatusChip.Visibility     = Visibility.Visible;
        StatusChip.Background     = HealthBrushes.Tint(tile.Status);
        TxtStatusChip.Text        = tile.Status is TileStatus.Off or TileStatus.Waiting or TileStatus.Unreachable
            ? tile.StatusLabel
            : $"{tile.StatusLabel} · {tile.ScoreText}";
        ActiveChip.Visibility     = tile.IsActive ? Visibility.Visible : Visibility.Collapsed;
        TxtLine.Text              = $"{conn.Server.Trim()} / {conn.DatabaseLabel} · {conn.AuthenticationLabel}";
        TxtViewOnHome.Visibility  = Visibility.Visible;
        BtnMore.Visibility        = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(StatusChip, tile.AutomationName);
    }

    private void OnSessionStateChanged(Guid connectionId)
    {
        if (connectionId != _id)
            return;
        RenderHeader();
        UpdatePreview();
    }

    // Renamed, made active, or another connection's change. The header and the defaults follow;
    // what's typed stays.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(() =>
    {
        _store = _context.Service.Load();
        var own = ChkCustomThresholds.IsChecked == true;
        _loading = true;
        try
        {
            ConnectionThresholds.Defaults = _store.HealthThresholds;
            // Showing the defaults, which may have just changed: what's saved changes with them.
            if (!own)
                ConnectionThresholds.Show(_store.HealthThresholds);
            ShowChannels();
        }
        finally
        {
            _loading = false;
        }
        RenderHeader();
        UpdatePreview();
        UpdateBar(ReadDraft(), _saved);
    });

    // ── Monitoring & alerts ───────────────────────────────────────────────────

    // The channels that would post its alerts: on, and for every connection or this one.
    private void ShowChannels()
    {
        var lines = _store.ChatChannels
            .Where(c => _id is { } id ? c.Includes(id) : c.AllConnections)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => $"{c.Name} · {ChatServices.Name(c.Service)} · " +
                         (c.CriticalOnly ? "critical only" : "warnings and critical") +
                         (c.Enabled ? string.Empty : " · off"))
            .ToList();
        LstChannels.ItemsSource  = lines;
        TxtNoChannels.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AlertsAndChat_Click(object sender, RoutedEventArgs e) =>
        _context.Open(new SettingsLocation(SettingsCategory.Alerts));

    // ── Health thresholds ─────────────────────────────────────────────────────

    private void ChkCustomThresholds_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the table exists.
        if (ConnectionThresholds is null)
            return;

        // Off shows the defaults again, so turning it back on starts from them.
        if (!_loading && ChkCustomThresholds.IsChecked != true)
        {
            _loading = true;
            try { ConnectionThresholds.Show(_store.HealthThresholds); }
            finally { _loading = false; }
        }
        ApplyThresholdsMode();
    }

    private void ApplyThresholdsMode()
    {
        var own  = ChkCustomThresholds.IsChecked == true;
        var name = _store.Connections.FirstOrDefault(c => c.Id == _id)?.DisplayName ?? "This connection";
        ConnectionThresholds.IsReadOnly    = !own;
        ConnectionThresholds.ShowsDefaults = own;
        TxtResetThresholds.Visibility      = own ? Visibility.Visible : Visibility.Collapsed;
        RunOwnHelp.Text = $"For a server that always runs hot. Off: {name} uses the ";
        TxtThresholdsNote.Text = own
            ? "Edited fields have an accent border. Leave a threshold blank to turn that level off. Weights are relative, and 0 leaves " +
              "a check out of the score. Past warning a check loses half its points; past critical, all of them."
            : $"{name} uses the default thresholds, shown here read-only. Turn on Use its own health thresholds to change them for it alone.";
    }

    private void ResetThresholds_Click(object sender, RoutedEventArgs e) => ConnectionThresholds.Show(_store.HealthThresholds);

    private void DefaultThresholds_Click(object sender, RoutedEventArgs e) =>
        _context.Open(new SettingsLocation(SettingsCategory.Thresholds));

    // Grades the latest sample on what's in the boxes: "On the latest sample, UAT would score 70 → 80".
    private void UpdatePreview()
    {
        var session = _context.Monitoring.Get(_id);
        var latest  = session is { IsRunning: true } ? session.Score : null;
        var own     = ChkCustomThresholds.IsChecked == true;

        HealthThresholds? thresholds = _store.HealthThresholds;
        if (own && !ConnectionThresholds.TryRead(out thresholds, out _, focus: false))
        {
            // A box can't be read yet: keep the last grades, without a score that doesn't add up.
            TxtPreview.Visibility = Visibility.Collapsed;
            return;
        }

        var preview = ThresholdsPreview.For(latest, thresholds!);
        ConnectionThresholds.ShowSample(latest, preview?.Regraded);

        var name = _store.Connections.FirstOrDefault(c => c.Id == _id)?.DisplayName;
        TxtPreview.Text       = preview is null || name is null ? string.Empty : preview.Text(name);
        TxtPreview.Visibility = own && preview is not null && name is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Save, discard, leave ──────────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(connect: false).Reported("save the connection");

    private async void SaveAndConnect_Click(object sender, RoutedEventArgs e) => await SaveAsync(connect: true).Reported("save the connection");

    private void Discard_Click(object sender, RoutedEventArgs e) => Discard();

    /// <summary>Puts back what's saved, on every tab.</summary>
    public void Discard()
    {
        _store = _context.Service.Load();
        _saved = Load(Saved());
        SetStatus(string.Empty, null);
    }

    /// <summary>
    /// Saves every tab in one write. Anything that changes how SqlVitals connects is checked
    /// against the server first, and nothing is saved when it can't sign in, so the list only
    /// ever holds working connections. True when it saved.
    /// </summary>
    /// <param name="connect">Also make it the active connection, and open its Overview.</param>
    public async Task<bool> SaveAsync(bool connect)
    {
        if (_busy)
            return false;
        if (!TryBuildSettings(out var settings, out var error))
        {
            SetStatus(error!, success: false);
            return false;
        }

        if (ChkCustomThresholds.IsChecked == true)
        {
            if (!ConnectionThresholds.TryRead(out var thresholds, out var thresholdError))
            {
                SelectTab(ConnectionTab.Thresholds);
                SetStatus($"Health thresholds — {thresholdError}", success: false);
                return false;
            }
            settings.HealthThresholds = thresholds;
        }

        var store = _context.Service.Load();
        var duplicate = store.Connections.FirstOrDefault(c =>
            c.Id != settings.Id && string.Equals(c.DisplayName, settings.DisplayName, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            SelectTab(ConnectionTab.Connection);
            SetStatus($"A connection named \"{settings.DisplayName}\" already exists. Enter a different connection name.", success: false);
            TxtConnectionName.Focus();
            return false;
        }

        var draft = ReadDraft();
        SetBusy(true);
        try
        {
            if (IsNew || draft.NeedsServerCheck(_saved))
            {
                SetStatus(ConnectingMessage(settings, "Validating connection…"), success: null);
                var failure = await ConnectionTest.TryOpenAsync(settings.ConnectionString);
                if (failure is not null)
                {
                    SetStatus($"Connection failed — not saved: {failure}", success: false);
                    return false;
                }
            }

            // Only when it was changed here: a mute from the tray menu meanwhile stays.
            _context.Service.Upsert(settings, notifications: draft.Notifies != _saved.Notifies ? draft.Notifies : null);

            // The first connection saved becomes active, so the dashboard has something to show.
            var makeActive = connect || store.ActiveConnectionId is null;
            if (makeActive)
                _context.Service.SetActive(settings.Id, ActivitySources.Settings);

            _id    = settings.Id;
            _store = _context.Service.Load();
            _saved = draft;
            ApplyThresholdsMode();
            RenderHeader();
            UpdatePreview();
            UpdateBar(ReadDraft(), _saved);
            SetStatus(makeActive
                ? $"Saved \"{settings.DisplayName}\", and it's now the active connection."
                : $"Saved \"{settings.DisplayName}\". Switch to it from the sidebar selector.",
                success: true);

            // Not busy any more: making it active opens its Overview, which asks this page whether it can be left.
            SetBusy(false);
            await _context.ApplyConnections(connect);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save: {SettingsContext.Redact(ex.Message)}", success: false);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Whether the page can be left. With unsaved changes it asks: Yes saves them (and stays
    /// when that fails), No discards them, Cancel stays.
    /// </summary>
    public async Task<bool> TryCloseAsync()
    {
        if (_busy)
            return false;
        if (!IsDirty)
            return true;

        var name = IsNew ? "the new connection" : $"\"{RunCrumb.Text}\"";
        var answer = _context.Ask(
            $"Save the changes to {name}?\n\nYes saves them, No discards them, Cancel stays on this page.",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        switch (answer)
        {
            case MessageBoxResult.Yes:
                return await SaveAsync(connect: false);
            case MessageBoxResult.No:
                Discard();
                return true;
            default:
                return false;
        }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (await TryCloseAsync().Reported("close the connection editor"))
            Closed?.Invoke();
    }

    // The leave prompt runs as the app leaves Settings for the Overview.
    private void ViewOnHome_Click(object sender, RoutedEventArgs e)
    {
        if (_id is { } id)
            _context.OpenServer(id);
    }

    // ── Test and remove ───────────────────────────────────────────────────────

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings, out var error))
        {
            SetStatus(error!, success: false);
            return;
        }

        BtnTest.IsEnabled = false;
        SetStatus(ConnectingMessage(settings, "Testing connection…"), success: null);
        try
        {
            var failure = await ConnectionTest.TryOpenAsync(settings.ConnectionString);
            SetStatus(failure is null ? "Connection successful!" : $"Connection failed: {failure}", success: failure is null);
        }
        finally
        {
            BtnTest.IsEnabled = !_busy;
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        BtnMore.ContextMenu.PlacementTarget = BtnMore;
        BtnMore.ContextMenu.Placement       = PlacementMode.Bottom;
        BtnMore.ContextMenu.IsOpen          = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => _ = RemoveAsync();

    /// <summary>Asks, then removes the connection. True when it was removed.</summary>
    public async Task<bool> RemoveAsync()
    {
        var conn = _store.Connections.FirstOrDefault(c => c.Id == _id);
        if (conn is null)
            return false;

        var confirm = _context.Ask(
            $"Remove the connection \"{conn.DisplayName}\" ({conn.Server})?\n\nThis cannot be undone.",
            "Remove Connection", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return false;

        var wasActive = _store.ActiveConnectionId == conn.Id;
        try
        {
            _context.Service.Remove(conn.Id);
            await _context.ApplyConnections(false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to remove the connection: {SettingsContext.Redact(ex.Message)}", success: false);
            return false;
        }

        var left = _context.Service.Load().Connections.Count;
        Removed?.Invoke(!wasActive
            ? $"Removed \"{conn.DisplayName}\"."
            : left > 0
                ? $"Removed \"{conn.DisplayName}\", which was the active connection. Choose another in the sidebar selector, or add a new one."
                : $"Removed \"{conn.DisplayName}\". No connections are left: add a new one to continue.");
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateBar(ReadDraft(), _saved);
    }

    private void SetStatus(string message, bool? success) => SettingsContext.ShowStatus(TxtStatus, message, success);

    // ── Connection fields (as before #131) ────────────────────────────────────

    private void CmbAuthentication_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyAuthenticationLayout();

    // Mirrors SSMS: the credential fields shown depend on the authentication type.
    private void ApplyAuthenticationLayout()
    {
        // Fires during InitializeComponent, before the named fields below exist.
        if (PanelPassword is null || PanelUserName is null)
            return;

        var auth = SelectedAuthentication();
        PanelUserName.Visibility   = auth != SqlAuthMode.Windows ? Visibility.Visible : Visibility.Collapsed;
        RunUserNameText.Text       = auth == SqlAuthMode.EntraMfa ? "User name" : "Login";
        RunUserNameRequired.Text   = auth == SqlAuthMode.SqlServer ? " *" : string.Empty;
        TxtUserNameHint.Visibility = auth == SqlAuthMode.EntraMfa ? Visibility.Visible : Visibility.Collapsed;
        PanelPassword.Visibility   = auth == SqlAuthMode.SqlServer ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CmbDatabase_DropDownOpened(object sender, EventArgs e)
    {
        if (_isLoadingDatabases || !TryBuildSettings(out var settings, out _))
            return;

        // Browse from master: the typed database may not exist, or the login may lack access to it.
        var connStr = settings.BuildConnectionString(databaseOverride: string.Empty);
        if (connStr == _databaseListSource)
            return;

        _isLoadingDatabases = true;
        var typed = CmbDatabase.Text;
        TxtDatabaseHint.Text = settings.Authentication == SqlAuthMode.EntraMfa
            ? "Loading databases… complete the Microsoft sign-in if prompted."
            : "Loading databases…";

        try
        {
            var names = await Task.Run(async () =>
            {
                await using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 ORDER BY name;", conn);
                await using var reader = await cmd.ExecuteReaderAsync();

                var list = new List<string>();
                while (await reader.ReadAsync())
                    list.Add(reader.GetString(0));
                return list;
            });

            _loading = true;
            try
            {
                CmbDatabase.ItemsSource = names;
                CmbDatabase.Text        = typed;   // replacing ItemsSource clears the typed text
            }
            finally
            {
                _loading = false;
            }
            _databaseListSource  = connStr;
            TxtDatabaseHint.Text = $"{names.Count} database(s) on {settings.Server}.";
        }
        catch (Exception ex)
        {
            CmbDatabase.IsDropDownOpen = false;
            TxtDatabaseHint.Text = $"Could not list databases: {SettingsContext.Redact(ex.Message)} You can still type a name.";
        }
        finally
        {
            _isLoadingDatabases = false;
        }
    }

    private SqlAuthMode SelectedAuthentication() =>
        Enum.TryParse<SqlAuthMode>((CmbAuthentication.SelectedItem as ComboBoxItem)?.Tag as string, out var mode)
            ? mode
            : SqlAuthMode.SqlServer;

    private EncryptMode SelectedEncrypt() =>
        Enum.TryParse<EncryptMode>((CmbEncrypt.SelectedItem as ComboBoxItem)?.Tag as string, out var mode)
            ? mode
            : EncryptMode.Mandatory;

    private static void SelectByTag(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private static string ConnectingMessage(ConnectionSettings settings, string message) =>
        settings.Authentication == SqlAuthMode.EntraMfa
            ? message + " Complete the Microsoft sign-in in the window that opens."
            : message;

    // Reads the Connection and Advanced tabs, and Monitor in background; the first problem as the error.
    private bool TryBuildSettings(out ConnectionSettings settings, out string? error)
    {
        var auth = SelectedAuthentication();
        settings = new ConnectionSettings
        {
            Id                     = _id ?? Guid.NewGuid(),
            Name                   = TxtConnectionName.Text.Trim(),
            Server                 = TxtServer.Text.Trim(),
            Authentication         = auth,
            UserName               = auth == SqlAuthMode.Windows ? string.Empty : TxtUserName.Text.Trim(),
            Password               = auth == SqlAuthMode.SqlServer ? TxtPassword.Password : string.Empty,
            SavePassword           = auth == SqlAuthMode.SqlServer && ChkSavePassword.IsChecked == true,
            Database               = CmbDatabase.Text.Trim(),
            Encrypt                = SelectedEncrypt(),
            TrustServerCertificate = ChkTrustServerCertificate.IsChecked == true,
            AdditionalParameters   = TxtAdditionalParameters.Text.Trim(),
            MonitorInBackground    = ChkMonitorInBackground.IsChecked == true,
            // For every connection, in Monitoring & history: kept as it is.
            CommandTimeoutSeconds  = _context.Service.Load().CommandTimeoutSeconds,
        };

        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            error = "Server name is required.";
            return false;
        }

        if (auth == SqlAuthMode.SqlServer && string.IsNullOrWhiteSpace(settings.UserName))
        {
            error = "Login is required for SQL Server Authentication.";
            return false;
        }

        if (auth == SqlAuthMode.SqlServer && string.IsNullOrEmpty(settings.Password))
        {
            error = "Password is required for SQL Server Authentication.";
            return false;
        }

        if (!int.TryParse(TxtConnectTimeout.Text.Trim(), out var connectTimeout) || connectTimeout <= 0)
        {
            error = "Connection timeout (Advanced) must be a positive integer.";
            return false;
        }
        settings.ConnectTimeoutSeconds = connectTimeout;

        try
        {
            // Credentials typed here would sit in plain text in a visible field, and the
            // form's own fields would silently override them anyway.
            var extra = new SqlConnectionStringBuilder(settings.AdditionalParameters);
            if (!string.IsNullOrEmpty(extra.Password) || !string.IsNullOrEmpty(extra.UserID))
            {
                error = "Additional parameters (Advanced) must not contain a user name or password. Use the Login and Password fields instead.";
                return false;
            }

            _ = settings.BuildConnectionString();
        }
        catch (Exception ex)
        {
            error = $"Additional parameters (Advanced) are not valid: {SettingsContext.Redact(ex.Message)}";
            return false;
        }

        error = null;
        return true;
    }
}
