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
        TxtConnectionString.Text = settings.ConnectionString;
        TxtTimeout.Text          = settings.CommandTimeoutSeconds.ToString();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseInputs(out var connStr, out var timeout))
            return;

        var settings = new ConnectionSettings
        {
            ConnectionString      = connStr,
            CommandTimeoutSeconds = timeout
        };

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
        SetStatus("Settings saved. Connecting…", success: null);

        try
        {
            var error = await TryOpenConnectionAsync(connStr);

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
        if (!TryParseInputs(out var connStr, out _))
            return;

        BtnTestConnection.IsEnabled = false;
        SetStatus("Testing connection�", success: null);

        try
        {
            var error = await TryOpenConnectionAsync(connStr);
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

    // ?? Helpers ???????????????????????????????????????????????????????????????

    private bool TryParseInputs(out string connStr, out int timeout)
    {
        connStr = TxtConnectionString.Text.Trim();
        timeout = 30;

        if (string.IsNullOrWhiteSpace(connStr))
        {
            SetStatus("Connection string cannot be empty.", success: false);
            return false;
        }

        if (!int.TryParse(TxtTimeout.Text.Trim(), out timeout) || timeout <= 0)
        {
            SetStatus("Timeout must be a positive integer.", success: false);
            return false;
        }

        return true;
    }

    private void SetStatus(string message, bool? success)
    {
        TxtStatus.Text = success switch
        {
            true  => "\u2713  " + message,   // ?
            false => "\u2717  " + message,   // ?
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
