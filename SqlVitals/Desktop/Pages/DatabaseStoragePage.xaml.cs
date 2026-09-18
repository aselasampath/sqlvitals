using System.Windows;
using System.Windows.Controls;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

// ── Flat view-model wrappers so DataGrid can bind simple properties ───────────
public sealed class DatabaseFileVm
{
    private readonly DatabaseFile _m;
    public DatabaseFileVm(DatabaseFile m) { _m = m; }

    public string  DatabaseName       => _m.DatabaseName;
    public string  State              => _m.State;
    public string  RecoveryModel      => _m.RecoveryModel;
    public int     CompatibilityLevel => _m.CompatibilityLevel;
    public string  FileType           => _m.FileType;
    public string  LogicalFileName    => _m.LogicalFileName;
    public string  PhysicalPath       => _m.PhysicalPath;
    public decimal FileSizeMB         => _m.FileSizeMB;
    public decimal SpaceUsedMB        => _m.SpaceUsedMB;
    public decimal FreeSpaceMB        => _m.FreeSpaceMB;
    public string  MaxSizeMBDisplay   => _m.MaxSizeMB == -1 ? "Unlimited" : _m.MaxSizeMB.ToString("N2");
    public string  AutoGrowth         => _m.AutoGrowth;
    public string  AutoShrinkDisplay  => _m.AutoShrink   ? "YES" : "";
    public string  QueryStoreOnDisplay=> _m.QueryStoreOn ? "YES" : "";
    public string  EncryptedDisplay   => _m.Encrypted    ? "YES" : "";
}

public sealed class ServerConfigVm
{
    private readonly ServerConfigParam _m;
    public ServerConfigVm(ServerConfigParam m) { _m = m; }

    public string ParameterName  => _m.ParameterName;
    public long   CurrentValue   => _m.CurrentValue;
    public long   MinValue       => _m.MinValue;
    public long   MaxValue       => _m.MaxValue;
    public string Description    => _m.Description;
    public string IsDynamicDisplay => _m.IsDynamic ? "Yes" : "No";
}

// ── Page ─────────────────────────────────────────────────────────────────────
public partial class DatabaseStoragePage : Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public DatabaseStoragePage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        var (config, files, tempFiles, tables) = await _repo.GetDatabaseStorageAsync();

        var fileList    = files.ToList();
        var tempList    = tempFiles.ToList();
        var configList  = config.ToList();
        var tableList   = tables.ToList();

        // KPI strip
        var totalMB    = fileList.Sum(f => f.FileSizeMB);
        var dataMB     = fileList.Where(f => f.FileType == "ROWS").Sum(f => f.FileSizeMB);
        var logMB      = fileList.Where(f => f.FileType == "LOG") .Sum(f => f.FileSizeMB);
        var tempDbMB   = tempList.Sum(f => f.SizeMB);

        KpiTotalSize.Text     = FormatMB(totalMB);
        KpiDataSize.Text      = FormatMB(dataMB);
        KpiLogSize.Text       = FormatMB(logMB);
        KpiTempDbSize.Text    = FormatMB(tempDbMB);

        var maxDop  = configList.FirstOrDefault(c => c.ParameterName == "max degree of parallelism");
        var costThr = configList.FirstOrDefault(c => c.ParameterName == "cost threshold for parallelism");
        KpiMaxDop.Text        = maxDop  != null ? maxDop.CurrentValue.ToString()  : "—";
        KpiCostThreshold.Text = costThr != null ? costThr.CurrentValue.ToString() : "—";

        // Grids
        GridFiles.ItemsSource  = fileList.Select(f => new DatabaseFileVm(f)).ToList();
        GridTempDb.ItemsSource = tempList;
        GridConfig.ItemsSource = configList.Select(c => new ServerConfigVm(c)).ToList();
        GridTables.ItemsSource = tableList;
    }

    private static string FormatMB(decimal mb)
    {
        if (mb >= 1024) return $"{mb / 1024:N2} GB";
        return $"{mb:N1} MB";
    }

    // ── Sub-tab switching ─────────────────────────────────────────────────────
    private bool _tabsWired;
    private void EnsureTabsWired()
    {
        if (_tabsWired) return;
        _tabsWired = true;
    }

    private void SubTab_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;

        // Tabs
        foreach (var btn in new[] { TabBtnFiles, TabBtnTempDb, TabBtnConfig, TabBtnTables })
            btn.Style = (Style)FindResource("NavButton");
        var active = tag switch
        {
            "TempDb"  => TabBtnTempDb,
            "Config"  => TabBtnConfig,
            "Tables"  => TabBtnTables,
            _         => TabBtnFiles,
        };
        active.Style = (Style)FindResource("NavButtonActive");

        // Panels
        PanelFiles.Visibility  = Visibility.Collapsed;
        PanelTempDb.Visibility = Visibility.Collapsed;
        PanelConfig.Visibility = Visibility.Collapsed;
        PanelTables.Visibility = Visibility.Collapsed;

        var visible = tag switch
        {
            "TempDb"  => PanelTempDb,
            "Config"  => PanelConfig,
            "Tables"  => PanelTables,
            _         => PanelFiles,
        };
        visible.Visibility = Visibility.Visible;
    }
}
