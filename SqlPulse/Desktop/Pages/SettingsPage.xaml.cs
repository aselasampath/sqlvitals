using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using SqlPulse.Desktop;
using SqlPulse.Desktop.Services;

namespace SqlPulse.Desktop.Pages;

public partial class SettingsPage : Page
{
    private readonly ConnectionSettingsService _service;

    public SettingsPage(ConnectionSettingsService service)
    {
        InitializeComponent();
        _service = service;
        LoadCurrentSettings();
        var app = (App)Application.Current;
        BtnThemeToggle.Content = app.IsDarkTheme ? "☀  Light" : "🌙  Dark";
    }

    private void LoadCurrentSettings()
    {
        var settings = _service.Load();
        TxtConnectionString.Text = settings.ConnectionString;
        TxtTimeout.Text          = settings.CommandTimeoutSeconds.ToString();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseInputs(out var connStr, out var timeout))
            return;

        try
        {
            _service.Save(new ConnectionSettings
            {
                ConnectionString      = connStr,
                CommandTimeoutSeconds = timeout
            });

            SetStatus("Settings saved. Restart the application to apply the new connection.", success: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to save: {ex.Message}", success: false);
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
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            SetStatus("Connection successful!", success: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Connection failed: {ex.Message}", success: false);
        }
        finally
        {
            BtnTestConnection.IsEnabled = true;
        }
    }

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        NavigationService?.Navigate(new SettingsPage(_service));
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
