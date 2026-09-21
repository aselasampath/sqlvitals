using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using SqlVitals.Desktop;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages;

public partial class SettingsPage : Page
{
    private readonly ConnectionSettingsService _service;

    // Applies saved settings to the running app. The bool asks it to open the dashboard.
    private readonly Func<ConnectionSettings, bool, Task> _applySettings;

    // Connection string the database list was last loaded with. Any change to the server or
    // credentials clears it, so reopening the list queries the new server.
    private string? _databaseListSource;
    private bool _isLoadingDatabases;

    public SettingsPage(
        ConnectionSettingsService service,
        Func<ConnectionSettings, bool, Task> applySettings,
        string? initialMessage = null)
    {
        InitializeComponent();
        _service       = service;
        _applySettings = applySettings;
        LoadCurrentSettings();
        var app = (App)Application.Current;
        BtnThemeToggle.Content = app.IsDarkTheme ? "☀  Light" : "🌙  Dark";

        if (!string.IsNullOrWhiteSpace(initialMessage))
            SetStatus(initialMessage, success: null);
    }

    private void LoadCurrentSettings()
    {
        var settings = _service.Load();

        TxtServer.Text                      = settings.Server;
        SelectByTag(CmbAuthentication, settings.Authentication.ToString());
        TxtUserName.Text                    = settings.UserName;
        TxtPassword.Password                = settings.Password;
        ChkSavePassword.IsChecked           = settings.SavePassword;
        CmbDatabase.Text                    = settings.Database;
        SelectByTag(CmbEncrypt, settings.Encrypt.ToString());
        ChkTrustServerCertificate.IsChecked = settings.TrustServerCertificate;
        TxtConnectTimeout.Text              = settings.ConnectTimeoutSeconds.ToString();
        TxtAdditionalParameters.Text        = settings.AdditionalParameters;
        TxtTimeout.Text                     = settings.CommandTimeoutSeconds.ToString();

        ApplyAuthenticationLayout();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInputs(out var settings))
            return;

        try
        {
            _service.Save(settings);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save: {ex.Message}", success: false);
            return;
        }

        BtnSave.IsEnabled = false;
        BtnTestConnection.IsEnabled = false;
        SetStatus(ConnectingMessage(settings, "Settings saved. Connecting…"), success: null);

        try
        {
            var error = await TryOpenConnectionAsync(settings.ConnectionString);

            // Apply even when the server is unreachable, so the app never keeps using a
            // connection the user has replaced. Only open the dashboard when it works.
            await _applySettings(settings, error is null);

            if (error is not null)
                SetStatus($"Settings saved, but the connection failed: {error}", success: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Settings saved, but could not be applied: {ex.Message}", success: false);
        }
        finally
        {
            BtnSave.IsEnabled = true;
            BtnTestConnection.IsEnabled = true;
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

    // Returns null on success, otherwise the failure message.
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
            return ex.Message;
        }
    }

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        NavigationService?.Navigate(new SettingsPage(_service, _applySettings));
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
            _ = settings.BuildConnectionString();
        }
        catch (Exception ex)
        {
            error = $"Additional parameters are not valid: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    private void SetStatus(string message, bool? success)
    {
        TxtStatus.Text = success switch
        {
            true  => "✓  " + message,   // ✓
            false => "✗  " + message,   // ✗
            null  => message,
        };
        TxtStatus.Foreground = success switch
        {
            true  => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),   // green
            false => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),   // red
            null  => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),   // muted
        };
    }
}
