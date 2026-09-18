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
    internal readonly IWaitStatsRepository Repo;
    internal readonly ConnectionSettingsService SettingsService;
    private Button _activeNav;

    public MainWindow()
    {
        InitializeComponent();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"SqlVitals v{version?.Major}.{version?.Minor}.{version?.Build}";
        AppVersionText.Text = $"v{version?.Major}.{version?.Minor}.{version?.Build}";

        SettingsService = new ConnectionSettingsService();

        // Build config: start from appsettings.json then overlay encrypted user settings
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false);

        var userSettings = SettingsService.Load();
        var overrides    = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(userSettings.ConnectionString))
            overrides["ConnectionStrings:SqlServer"] = userSettings.ConnectionString;

        if (userSettings.CommandTimeoutSeconds > 0)
            overrides["QuerySettings:CommandTimeoutSeconds"] = userSettings.CommandTimeoutSeconds.ToString();

        if (overrides.Count > 0)
            configBuilder.AddInMemoryCollection(overrides);

        var config = configBuilder.Build();

        Repo       = new WaitStatsRepository(config);
        _activeNav = BtnLiveMetrics;

        // Display server and database from the resolved connection string
        ApplyConnectionLabel(userSettings.ConnectionString);

        Loaded += async (_, _) =>
        {
            await DisableTempDbIfAzureSqlAsync();
            await NavigateTo("LiveMetrics");
        };
    }

    // TempDB file-level reporting relies on sys.master_files, which Azure SQL Database
    // doesn't expose (see README → Azure SQL vs On-Premises Compatibility). Disable the
    // tab up front instead of letting the user hit a query error after navigating to it.
    private async System.Threading.Tasks.Task DisableTempDbIfAzureSqlAsync()
    {
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
            // Edition check failed (e.g. connection not yet configured) — leave the tab enabled.
        }
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

    private async System.Threading.Tasks.Task NavigateTo(string tag)
    {
        // Update nav button styles
        foreach (var btn in new[] { BtnLiveMetrics, BtnTopWaits, BtnActiveWaits, BtnWaitTrend, BtnTempDb, BtnMemory, BtnQueryStore, BtnIndexHealth, BtnResQueries, BtnImpConv, BtnPlanHealth, BtnStaleStats, BtnDbStorage, BtnAppConn, BtnPerfmon, BtnExport, BtnSettings })
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
            "Export"          => BtnExport,
            "Settings"        => BtnSettings,
            _                 => BtnLiveMetrics,
        };
        active.Style = (Style)FindResource("NavButtonActive");

        // Settings page does not implement IRefreshable — handle separately
        if (tag == "Settings")
        {
            MainFrame.Navigate(new SettingsPage(SettingsService));
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = "Settings";
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
            MessageBox.Show(FormatErrorDetail(ex), "Data Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BtnRefresh.IsEnabled = true; }
    }
}
