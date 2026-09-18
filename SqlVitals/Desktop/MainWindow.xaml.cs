using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop;

public partial class MainWindow : Window
{
    // Replaced when the user saves new connection settings, so pages created after that
    // point pick up the new server without restarting the app.
    internal IWaitStatsRepository Repo { get; private set; }
    internal readonly ConnectionSettingsService SettingsService;
    private bool _isConnectionConfigured;

    public MainWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"SqlVitals v{version?.Major}.{version?.Minor}.{version?.Build}";
        AppVersionText.Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}";

        SettingsService = new ConnectionSettingsService();
        Repo = BuildRepository(SettingsService.Load());

        Loaded += async (_, _) =>
        {
            if (!_isConnectionConfigured)
            {
                // First run (or settings cleared): send the user straight to Settings
                // instead of letting every page fail against an empty connection string.
                await NavigateTo("Settings",
                    "Welcome to SqlVitals! Enter a SQL Server connection string below, then click Save & Connect.");
                return;
            }

            await DisableTempDbIfAzureSqlAsync();
            await NavigateTo("LiveMetrics");
        };
    }

    // Builds config from appsettings.json overlaid with the encrypted user settings, and
    // refreshes the connection labels in the header.
    private IWaitStatsRepository BuildRepository(ConnectionSettings userSettings)
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false);

        var overrides = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(userSettings.ConnectionString))
            overrides["ConnectionStrings:SqlServer"] = userSettings.ConnectionString;

        if (userSettings.CommandTimeoutSeconds > 0)
            overrides["QuerySettings:CommandTimeoutSeconds"] = userSettings.CommandTimeoutSeconds.ToString();

        if (overrides.Count > 0)
            configBuilder.AddInMemoryCollection(overrides);

        var config = configBuilder.Build();
        var resolvedConnectionString = config.GetConnectionString("SqlServer") ?? string.Empty;

        _isConnectionConfigured = !string.IsNullOrWhiteSpace(resolvedConnectionString);
        ApplyConnectionLabel(resolvedConnectionString);

        return new WaitStatsRepository(config);
    }

    /// <summary>
    /// Swaps in a repository for newly saved settings and, when the server is reachable,
    /// opens the dashboard — so a new connection works without restarting the app.
    /// </summary>
    internal async System.Threading.Tasks.Task ApplyConnectionSettingsAsync(ConnectionSettings settings, bool navigateToDashboard)
    {
        var previous = Repo;
        Repo = BuildRepository(settings);

        // Best effort: a trace session started against the old connection shouldn't be
        // left running on that server.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await previous.StopTraceAsync(); } catch { /* old server may be unreachable */ }
        });

        BtnTempDb.IsEnabled = true;
        BtnTempDb.ToolTip = null;
        await DisableTempDbIfAzureSqlAsync();

        if (navigateToDashboard)
            await NavigateTo("LiveMetrics");
    }

    // TempDB file-level reporting relies on sys.master_files, which Azure SQL Database
    // doesn't expose (see README → Azure SQL vs On-Premises Compatibility). Disable the
    // tab up front instead of letting the user hit a query error after navigating to it.
    private async System.Threading.Tasks.Task DisableTempDbIfAzureSqlAsync()
    {
        if (!_isConnectionConfigured)
            return;

        try
        {
            if (await Repo.IsAzureSqlDatabaseAsync())
            {
                BtnTempDb.IsEnabled = false;
                BtnTempDb.ToolTip = "Not available on Azure SQL Database (sys.master_files is not accessible)";
            }
        }
        catch
        {
            // Edition check failed (e.g. server unreachable) — leave the tab enabled.
        }
    }

    // Last line of defence for the live SP trace: SpTracePage drops its event session when
    // the user navigates away, but closing the window while that page is open does not
    // raise Unloaded reliably. Without this the session keeps collecting on the monitored
    // server after the app is gone.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            // Stop the page's poll timer first. Each poll holds the trace repository's gate
            // for a full server round trip, so leaving it running eats the budget below and
            // the session survives the app — which is what it did before this call existed.
            if (MainFrame.Content is SpTracePage tracePage)
                tracePage.PrepareForShutdown();

            // Task.Run keeps the await continuations off the UI thread — blocking on them
            // here with a live SynchronizationContext would deadlock. The budget covers a
            // cold connection to a remote server (Azure SQL routinely needs several
            // seconds) plus the DROP itself.
            System.Threading.Tasks.Task.Run(() => Repo.StopTraceAsync())
                .Wait(TimeSpan.FromSeconds(20));
        }
        catch
        {
            // Shutting down regardless. A session left behind is dropped by name on the
            // next Start Trace.
        }

        base.OnClosing(e);
    }

    // Returns a compact one-line status bar string, e.g. "Error [WaitStatsRepository.GetCumulativeWaitsAsync]: Timeout"
    private static string FormatErrorStatus(Exception ex) =>
        ex is WaitStatsException wse
            ? $"Error [{wse.ErrorTag}]: {ex.InnerException?.Message ?? ex.Message}"
            : $"Error: {ex.Message}";

    // Returns the full message for the MessageBox, with the searchable tag on its own line.
    private static string FormatErrorDetail(Exception ex)
    {
        if (ex is WaitStatsException wse)
        {
            var sqlNote = wse.SqlErrorNumber != 0 ? $"\nSQL error number: {wse.SqlErrorNumber}" : string.Empty;
            return $"Error tag:  {wse.ErrorTag}{sqlNote}\n\n{ex.InnerException?.Message ?? ex.Message}\n\nSearch the codebase for \"{wse.ErrorTag}\" to locate the originating query.";
        }
        return ex.Message;
    }

    private void ApplyConnectionLabel(string connectionString)
    {
        TxtServerName.Text   = "—";
        TxtDatabaseName.Text = "—";

        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        try
        {
            var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            TxtServerName.Text   = string.IsNullOrWhiteSpace(csb.DataSource)         ? "—" : csb.DataSource;
            TxtDatabaseName.Text = string.IsNullOrWhiteSpace(csb.InitialCatalog) ? "—" : csb.InitialCatalog;
        }
        catch
        {
            // Malformed connection string — leave defaults
        }
    }

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
            await NavigateTo(btn.Tag?.ToString() ?? "Overview");
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (MainFrame.Content is IRefreshable page)
        {
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = "Refreshing…";
            try
            {
                await page.RefreshAsync();
                TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = FormatErrorStatus(ex);
            }
            finally { BtnRefresh.IsEnabled = true; }
        }
    }

    private async System.Threading.Tasks.Task NavigateTo(string tag, string? settingsMessage = null)
    {
        // Without a connection every data page would just fail — point the user at Settings.
        if (!_isConnectionConfigured && tag != "Settings")
        {
            tag = "Settings";
            settingsMessage ??= "No SQL Server connection is configured yet. Enter a connection string below, then click Save & Connect.";
        }

        // Update nav button styles
        foreach (var btn in new[] { BtnLiveMetrics, BtnTopWaits, BtnActiveWaits, BtnWaitTrend, BtnTempDb, BtnMemory, BtnQueryStore, BtnIndexHealth, BtnResQueries, BtnImpConv, BtnPlanHealth, BtnStaleStats, BtnDbStorage, BtnAppConn, BtnPerfmon, BtnSpTrace, BtnExport, BtnSettings })
            btn.Style = (Style)FindResource("NavButton");

        Button active = tag switch
        {
            "TopWaits"        => BtnTopWaits,
            "ActiveWaits"     => BtnActiveWaits,
            "WaitTrend"       => BtnWaitTrend,
            "TempDb"          => BtnTempDb,
            "Memory"          => BtnMemory,
            "QueryStore"      => BtnQueryStore,
            "IndexHealth"     => BtnIndexHealth,
            "ResourceQueries" => BtnResQueries,
            "ImplicitConv"    => BtnImpConv,
            "PlanCacheHealth" => BtnPlanHealth,
            "StaleStats"      => BtnStaleStats,
            "DbStorage"       => BtnDbStorage,
            "AppConnections" => BtnAppConn,
            "Perfmon"        => BtnPerfmon,
            "SpTrace"         => BtnSpTrace,
            "Export"          => BtnExport,
            "Settings"        => BtnSettings,
            _                 => BtnLiveMetrics,
        };
        active.Style = (Style)FindResource("NavButtonActive");

        // Settings page does not implement IRefreshable — handle separately
        if (tag == "Settings")
        {
            MainFrame.Navigate(new SettingsPage(SettingsService, ApplyConnectionSettingsAsync, settingsMessage));
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = _isConnectionConfigured ? "Settings" : "Not connected";
            return;
        }

        IRefreshable page = tag switch
        {
            "LiveMetrics"     => new LiveMetricsDashboardPage(Repo),
            "TopWaits"        => new TopWaitsPage(Repo),
            "ActiveWaits"     => new ActiveWaitsPage(Repo),
            "WaitTrend"       => new WaitStatsTrendPage(Repo),
            "TempDb"          => new TempDbPage(Repo),
            "Memory"          => new MemoryGrantsPage(Repo),
            "QueryStore"      => new QueryStorePage(Repo),
            "IndexHealth"     => new IndexHealthPage(Repo),
            "ResourceQueries" => new ResourceQueriesPage(Repo),
            "ImplicitConv"    => new ImplicitConversionsPage(Repo),
            "PlanCacheHealth" => new PlanCacheHealthPage(Repo),
            "StaleStats"      => new StaleStatisticsPage(Repo),
            "DbStorage"       => new DatabaseStoragePage(Repo),
            "AppConnections" => new ApplicationConnectionsPage(Repo),
            "Perfmon"        => new PerfmonPage(Repo),
            "SpTrace"         => new SpTracePage(Repo),
            "Export"          => new ExportPage(Repo),
            _                 => new LiveMetricsDashboardPage(Repo),
        };

        MainFrame.Navigate(page);

        BtnRefresh.IsEnabled = false;
        TxtStatus.Text = "Loading…";
        try
        {
            await page.RefreshAsync();
            TxtStatus.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = FormatErrorStatus(ex);
            MessageBox.Show(
                FormatErrorDetail(ex) + "\n\nIf the server details are wrong, update the connection in Settings — no restart needed.",
                "Data Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnRefresh.IsEnabled = true; }
    }
}
