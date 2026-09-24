using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;
using SqlVitals.Desktop;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Pages;

public partial class SettingsPage : Page
{
    private readonly ConnectionSettingsService _service;
    private readonly MonitoringManager         _monitoring;

    // The history file grows with every sample and shrinks after a purge, which the writer
    // runs on its own schedule, so the size shown is re-read while the page is open.
    private readonly DispatcherTimer _historySizeTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Set while the history choices are filled in or put back in code, so they aren't saved.
    private bool _isLoadingHistorySettings;
    private int  _savedIntervalMinutes;
    private int  _savedRetentionDays;

    // Tells the running app the saved connections changed so it can pick up a new or edited
    // active connection. The bool asks it to open the dashboard afterwards.
    private readonly Func<bool, Task> _applyConnections;

    // The saved connection the form is editing; null while adding a new one.
    private Guid? _editingId;

    // Set while the list is repopulated in code, so the selection handler doesn't reload the form.
    private bool _isPopulatingList;

    // Connection string the database list was last loaded with. Any change to the server or
    // credentials clears it, so reopening the list queries the new server.
    private string? _databaseListSource;
    private bool _isLoadingDatabases;

    public SettingsPage(
        ConnectionSettingsService service,
        MonitoringManager monitoring,
        Func<bool, Task> applyConnections,
        string? initialMessage = null,
        Guid? selectConnectionId = null)
    {
        InitializeComponent();
        _service          = service;
        _monitoring       = monitoring;
        _applyConnections = applyConnections;

        var store = _service.Load();
        TxtTimeout.Text = store.CommandTimeoutSeconds.ToString();
        LoadHistorySettings(store);
        _historySizeTimer.Tick += (_, _) => UpdateHistorySize();
        Loaded   += (_, _) => { UpdateHistorySize(); _historySizeTimer.Start(); };
        Unloaded += (_, _) => _historySizeTimer.Stop();
        var initial = (selectConnectionId is { } id ? store.Connections.FirstOrDefault(c => c.Id == id) : null)
                      ?? store.Active
                      ?? store.Connections.FirstOrDefault();
        PopulateConnectionList(store, initial?.Id);
        if (initial is not null)
            LoadIntoForm(initial);
        else
            ClearForm();

        var app = (App)Application.Current;
        BtnThemeToggle.Content = app.IsDarkTheme ? "☀  Light" : "🌙  Dark";
        TxtLogInfo.Text = $"{AppLog.Directory} — one file per day, kept for {AppLog.RetentionDays} days. " +
                          "Passwords and connection strings are never written. Attach the latest file to a bug report.";

        if (!string.IsNullOrWhiteSpace(initialMessage))
            SetStatus(initialMessage, success: null);
    }

    // ── Saved connections list ────────────────────────────────────────────────

    private void PopulateConnectionList(ConnectionStore store, Guid? selectId)
    {
        _isPopulatingList = true;
        try
        {
            var items = store.Connections
                .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new ConnectionListItem(c, c.Id == store.ActiveConnectionId))
                .ToList();

            LstConnections.ItemsSource  = items;
            LstConnections.SelectedItem = items.FirstOrDefault(i => i.Settings.Id == selectId);
            LstConnections.Visibility   = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtNoConnections.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            BtnRemoveConnection.IsEnabled = LstConnections.SelectedItem is not null;
        }
        finally
        {
            _isPopulatingList = false;
        }
    }

    private void LstConnections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnRemoveConnection.IsEnabled = LstConnections.SelectedItem is not null;
        if (_isPopulatingList || LstConnections.SelectedItem is not ConnectionListItem item)
            return;

        LoadIntoForm(item.Settings);
        SetStatus(string.Empty, success: null);
    }

    private void BtnNewConnection_Click(object sender, RoutedEventArgs e)
    {
        _isPopulatingList = true;
        LstConnections.SelectedItem = null;
        _isPopulatingList = false;
        BtnRemoveConnection.IsEnabled = false;

        ClearForm();
        TxtServer.Focus();
        SetStatus("Enter the details for the new connection, then click Save or Save & Connect.", success: null);
    }

    private async void BtnRemoveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (LstConnections.SelectedItem is not ConnectionListItem item)
            return;

        var confirm = MessageBox.Show(
            $"Remove the connection \"{item.Settings.DisplayName}\" ({item.Settings.Server})?\n\nThis cannot be undone.",
            "Remove Connection", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            _service.Remove(item.Settings.Id);
            await _applyConnections(false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to remove the connection: {Redact(ex.Message)}", success: false);
            return;
        }

        var store = _service.Load();
        PopulateConnectionList(store, selectId: null);
        ClearForm();

        if (!item.IsActive)
            SetStatus($"Removed \"{item.Settings.DisplayName}\".", success: true);
        else if (store.Connections.Count > 0)
            SetStatus($"Removed \"{item.Settings.DisplayName}\", which was the active connection. " +
                      "Select another connection in the sidebar (or above, then Save & Connect), or add a new one.",
                      success: null);
        else
            SetStatus($"Removed \"{item.Settings.DisplayName}\". No connections are left — add a new one below to continue.",
                      success: null);
    }

    // ── Save / test ───────────────────────────────────────────────────────────

    private async void BtnSave_Click(object sender, RoutedEventArgs e) => await SaveAsync(connect: false);

    private async void BtnSaveAndConnect_Click(object sender, RoutedEventArgs e) => await SaveAsync(connect: true);

    // Validates the connection against the server, then adds or updates it. Nothing is saved
    // when the server can't be reached, so the list only ever holds working connections.
    private async Task SaveAsync(bool connect)
    {
        if (!TryReadInputs(out var settings))
            return;

        var store = _service.Load();
        var duplicate = store.Connections.FirstOrDefault(c =>
            c.Id != settings.Id && string.Equals(c.DisplayName, settings.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        if (duplicate is not null)
        {
            SetStatus($"A connection named \"{settings.DisplayName}\" already exists. Enter a different connection name.", success: false);
            TxtConnectionName.Focus();
            return;
        }

        SetBusy(true);
        SetStatus(ConnectingMessage(settings, "Validating connection…"), success: null);

        try
        {
            var error = await TryOpenConnectionAsync(settings.ConnectionString);
            if (error is not null)
            {
                SetStatus($"Connection failed — not saved: {error}", success: false);
                return;
            }

            _service.Upsert(settings);

            // The first connection saved becomes active, so the dashboard has something to show.
            var makeActive = connect || store.ActiveConnectionId is null;
            if (makeActive)
                _service.SetActive(settings.Id);

            _editingId = settings.Id;
            PopulateConnectionList(_service.Load(), settings.Id);
            TxtFormTitle.Text = $"Edit Connection — {settings.DisplayName}";

            await _applyConnections(connect);

            SetStatus(makeActive
                ? $"Connection \"{settings.DisplayName}\" saved and is now active."
                : $"Connection \"{settings.DisplayName}\" saved. Switch to it from the sidebar selector.",
                success: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save: {Redact(ex.Message)}", success: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnTestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInputs(out var settings))
            return;

        BtnTestConnection.IsEnabled = false;
        SetStatus(ConnectingMessage(settings, "Testing connection…"), success: null);

        try
        {
            var error = await TryOpenConnectionAsync(settings.ConnectionString);
            if (error is null)
                SetStatus("Connection successful!", success: true);
            else
                SetStatus($"Connection failed: {error}", success: false);
        }
        finally
        {
            BtnTestConnection.IsEnabled = true;
        }
    }

    // Returns null on success, otherwise the failure message with any credentials masked.
    private static async Task<string?> TryOpenConnectionAsync(string connStr)
    {
        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            return null;
        }
        catch (Exception ex)
        {
            return Redact(ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        BtnSave.IsEnabled             = !busy;
        BtnSaveAndConnect.IsEnabled   = !busy;
        BtnTestConnection.IsEnabled   = !busy;
        BtnNewConnection.IsEnabled    = !busy;
        BtnRemoveConnection.IsEnabled = !busy && LstConnections.SelectedItem is not null;
        LstConnections.IsEnabled      = !busy;
    }

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        NavigationService?.Navigate(new SettingsPage(_service, _monitoring, _applyConnections, selectConnectionId: _editingId));
    }

    private void LnkOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Created up front so the link works before anything has been logged.
            System.IO.Directory.CreateDirectory(AppLog.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = AppLog.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the log folder: {ex.Message}", success: false);
        }
    }

    // ── Monitoring history ────────────────────────────────────────────────────

    private void LoadHistorySettings(ConnectionStore store)
    {
        _isLoadingHistorySettings = true;
        try
        {
            foreach (var minutes in HistorySettings.IntervalChoicesMinutes)
                CmbHistoryInterval.Items.Add(new ComboBoxItem { Content = MinutesText(minutes), Tag = minutes });
            foreach (var days in HistorySettings.RetentionChoicesDays)
                CmbHistoryRetention.Items.Add(new ComboBoxItem { Content = $"{days} days", Tag = days });

            ShowHistorySettings(store.HistoryIntervalMinutes, store.HistoryRetentionDays);
        }
        finally
        {
            _isLoadingHistorySettings = false;
        }
    }

    // Selects the saved choices; the values are always ones offered (see HistorySettings.Normalize*).
    private void ShowHistorySettings(int intervalMinutes, int retentionDays)
    {
        _savedIntervalMinutes = intervalMinutes;
        _savedRetentionDays   = retentionDays;
        CmbHistoryInterval.SelectedItem  = FindChoice(CmbHistoryInterval, intervalMinutes);
        CmbHistoryRetention.SelectedItem = FindChoice(CmbHistoryRetention, retentionDays);
    }

    private static ComboBoxItem FindChoice(ComboBox combo, int value) =>
        combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == value)
        ?? (ComboBoxItem)combo.Items[0];

    private void HistorySetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingHistorySettings ||
            CmbHistoryInterval.SelectedItem is not ComboBoxItem { Tag: int minutes } ||
            CmbHistoryRetention.SelectedItem is not ComboBoxItem { Tag: int days })
            return;
        if (minutes == _savedIntervalMinutes && days == _savedRetentionDays)
            return;

        if (days < _savedRetentionDays)
        {
            var confirm = MessageBox.Show(
                $"Keep monitoring history for {days} days instead of {_savedRetentionDays}?\n\n" +
                $"History older than {days} days is deleted straight away, for every connection. This cannot be undone.",
                "Monitoring History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                RevertHistorySettings();
                return;
            }
        }

        try
        {
            _service.SetHistorySettings(minutes, days);
            var store = _service.Load();
            _monitoring.ApplyHistorySettings(store);
            _savedIntervalMinutes = store.HistoryIntervalMinutes;
            _savedRetentionDays   = store.HistoryRetentionDays;

            ShowStatus(TxtHistoryStatus,
                $"Saved: a snapshot every {MinutesText(store.HistoryIntervalMinutes)}, " +
                $"history older than {store.HistoryRetentionDays} days deleted automatically.",
                success: true);
        }
        catch (Exception ex)
        {
            ShowStatus(TxtHistoryStatus, $"Could not save the history settings: {ex.Message}", success: false);
            RevertHistorySettings();
        }
    }

    // Puts back the saved choices. Deferred: changing the selection inside its own
    // SelectionChanged handler can leave the ComboBox showing the rejected choice.
    private void RevertHistorySettings() =>
        Dispatcher.BeginInvoke(() =>
        {
            _isLoadingHistorySettings = true;
            try
            {
                ShowHistorySettings(_savedIntervalMinutes, _savedRetentionDays);
            }
            finally
            {
                _isLoadingHistorySettings = false;
            }
        });

    private async void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Delete all monitoring history, for every connection?\n\n" +
            "Monitoring carries on, and new history is saved from the next sample. This cannot be undone.",
            "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        BtnClearHistory.IsEnabled = false;
        ShowStatus(TxtHistoryStatus, "Clearing history…", success: null);
        try
        {
            await _monitoring.ClearHistoryAsync();
            ShowStatus(TxtHistoryStatus, "History cleared.", success: true);
        }
        catch (Exception ex)
        {
            ShowStatus(TxtHistoryStatus, $"Could not clear the history: {ex.Message}", success: false);
        }
        finally
        {
            BtnClearHistory.IsEnabled = true;
            UpdateHistorySize();
        }
    }

    private void UpdateHistorySize()
    {
        var path  = _monitoring.HistoryPath;
        var bytes = HistoryStore.SizeOnDisk(path);
        TxtHistorySize.Text = bytes == 0
            ? "No history saved yet."
            : $"Size on disk: {HistorySettings.FormatSize(bytes)}";
        TxtHistoryPath.Text = path;
    }

    private static string MinutesText(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    // ── Form ──────────────────────────────────────────────────────────────────

    private void LoadIntoForm(ConnectionSettings settings)
    {
        _editingId = settings.Id;
        TxtFormTitle.Text = $"Edit Connection — {settings.DisplayName}";

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
        TxtConnectTimeout.Text              = settings.ConnectTimeoutSeconds.ToString();
        TxtAdditionalParameters.Text        = settings.AdditionalParameters;
        ChkMonitorInBackground.IsChecked    = settings.MonitorInBackground;

        _databaseListSource = null;
        ApplyAuthenticationLayout();
    }

    private void ClearForm()
    {
        var blank = new ConnectionSettings();
        LoadIntoForm(blank);
        _editingId        = null;
        TxtFormTitle.Text = "New Connection";
    }

    // ── Connection form ───────────────────────────────────────────────────────

    private void CmbAuthentication_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyAuthenticationLayout();
        ConnectionField_Changed(sender, e);
    }

    // Mirrors SSMS: the credential fields shown depend on the authentication type.
    private void ApplyAuthenticationLayout()
    {
        // Fires during InitializeComponent, before the named fields below exist.
        if (TxtPassword is null)
            return;

        var auth = SelectedAuthentication();

        var showUser     = auth != SqlAuthMode.Windows;
        var showPassword = auth == SqlAuthMode.SqlServer;

        LblUserName.Visibility     = showUser ? Visibility.Visible : Visibility.Collapsed;
        PanelUserName.Visibility   = showUser ? Visibility.Visible : Visibility.Collapsed;
        RunUserNameText.Text       = auth == SqlAuthMode.EntraMfa ? "User name" : "Login";
        RunUserNameRequired.Text   = auth == SqlAuthMode.SqlServer ? " *" : string.Empty;
        TxtUserNameHint.Visibility = auth == SqlAuthMode.EntraMfa ? Visibility.Visible : Visibility.Collapsed;

        LblPassword.Visibility     = showPassword ? Visibility.Visible : Visibility.Collapsed;
        TxtPassword.Visibility     = showPassword ? Visibility.Visible : Visibility.Collapsed;
        ChkSavePassword.Visibility = showPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConnectionField_Changed(object sender, RoutedEventArgs e) => _databaseListSource = null;

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

            CmbDatabase.ItemsSource = names;
            CmbDatabase.Text        = typed;   // replacing ItemsSource clears the typed text
            _databaseListSource     = connStr;
            TxtDatabaseHint.Text    = $"{names.Count} database(s) on {settings.Server}.";
        }
        catch (Exception ex)
        {
            CmbDatabase.IsDropDownOpen = false;
            TxtDatabaseHint.Text = $"Could not list databases: {ex.Message} You can still type a name.";
        }
        finally
        {
            _isLoadingDatabases = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqlAuthMode SelectedAuthentication() =>
        Enum.TryParse<SqlAuthMode>((CmbAuthentication.SelectedItem as ComboBoxItem)?.Tag as string, out var mode)
            ? mode
            : SqlAuthMode.SqlServer;

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag)
                             ?? combo.Items[0];
    }

    private static string ConnectingMessage(ConnectionSettings settings, string message) =>
        settings.Authentication == SqlAuthMode.EntraMfa
            ? message + " Complete the Microsoft sign-in in the window that opens."
            : message;

    // Reads the form, reporting the first problem in the status line.
    private bool TryReadInputs(out ConnectionSettings settings)
    {
        if (!TryBuildSettings(out settings, out var error))
        {
            SetStatus(error!, success: false);
            return false;
        }
        return true;
    }

    private bool TryBuildSettings(out ConnectionSettings settings, out string? error)
    {
        var auth = SelectedAuthentication();
        settings = new ConnectionSettings
        {
            Id                     = _editingId ?? Guid.NewGuid(),
            Name                   = TxtConnectionName.Text.Trim(),
            Server                 = TxtServer.Text.Trim(),
            Authentication         = auth,
            UserName               = auth == SqlAuthMode.Windows ? string.Empty : TxtUserName.Text.Trim(),
            Password               = auth == SqlAuthMode.SqlServer ? TxtPassword.Password : string.Empty,
            SavePassword           = auth == SqlAuthMode.SqlServer && ChkSavePassword.IsChecked == true,
            Database               = CmbDatabase.Text.Trim(),
            Encrypt                = Enum.TryParse<EncryptMode>((CmbEncrypt.SelectedItem as ComboBoxItem)?.Tag as string, out var enc)
                                         ? enc : EncryptMode.Mandatory,
            TrustServerCertificate = ChkTrustServerCertificate.IsChecked == true,
            AdditionalParameters   = TxtAdditionalParameters.Text.Trim(),
            MonitorInBackground    = ChkMonitorInBackground.IsChecked == true,
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
            error = "Connection timeout must be a positive integer.";
            return false;
        }
        settings.ConnectTimeoutSeconds = connectTimeout;

        if (!int.TryParse(TxtTimeout.Text.Trim(), out var timeout) || timeout <= 0)
        {
            error = "Command timeout must be a positive integer.";
            return false;
        }
        settings.CommandTimeoutSeconds = timeout;

        try
        {
            // Credentials typed here would sit in plain text in a visible field, and the
            // form's own fields would silently override them anyway.
            var extra = new SqlConnectionStringBuilder(settings.AdditionalParameters);
            if (!string.IsNullOrEmpty(extra.Password) || !string.IsNullOrEmpty(extra.UserID))
            {
                error = "Additional parameters must not contain a user name or password. Use the Login and Password fields instead.";
                return false;
            }

            _ = settings.BuildConnectionString();
        }
        catch (Exception ex)
        {
            error = $"Additional parameters are not valid: {Redact(ex.Message)}";
            return false;
        }

        error = null;
        return true;
    }

    private static string Redact(string message) => ConnectionSettingsService.RedactSecrets(message);

    /// <summary>Row in the saved connections list.</summary>
    public sealed record ConnectionListItem(ConnectionSettings Settings, bool IsActive)
    {
        public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message, bool? success) => ShowStatus(TxtStatus, message, success);

    private static void ShowStatus(TextBlock target, string message, bool? success)
    {
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        target.Text = success switch
        {
            true  => "✓  " + message,   // ✓
            false => "✗  " + message,   // ✗
            null  => message,
        };
        target.Foreground = success switch
        {
            true  => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),   // green
            false => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),   // red
            null  => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),   // muted
        };
    }
}
