using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

// ── Flat view-model wrappers so DataGrid can bind simple properties ───────────
public sealed class DatabaseFileVm
{
    private readonly DatabaseFile _m;
    public DatabaseFileVm(DatabaseFile m) { _m = m; }

    /// <summary>The file read, for the verdict's Show the file (#150).</summary>
    public DatabaseFile File => _m;

    public string  DatabaseName       => _m.DatabaseName;
    public string  State              => _m.State;
    public string  RecoveryModel      => _m.RecoveryModel;
    public int     CompatibilityLevel => _m.CompatibilityLevel;
    public string  FileType           => _m.FileType;
    public string  LogicalFileName    => _m.LogicalFileName;
    public string  PhysicalPath       => _m.PhysicalPath;
    public decimal FileSizeMB         => _m.FileSizeMB;
    public decimal? SpaceUsedMB       => _m.SpaceUsedMB;
    public decimal? FreeSpaceMB       => _m.FreeSpaceMB;
    public string  MaxSizeMBDisplay   => _m.MaxSizeMB == -1 ? "Unlimited" : _m.MaxSizeMB.ToString("N2", CultureInfo.CurrentCulture);
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
public partial class DatabaseStoragePage : Page, IRefreshable, IPageContext, IFocusPage
{
    // Read once, and again on Refresh, from the connection's database (#140).
    public PageKind Kind => PageKind.Snapshot;
    public bool UsesDatabase => true;

    // ── A log space alert's evidence (#145): the database's files, until Show all ──
    private string? _database;
    private List<DatabaseFileVm>? _files;

    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter is not { Kind: FocusKind.Database, Name: { } name })
            return;
        _database = name;
        ShowFiles();
    }

    public void ClearFocus()
    {
        _database = null;
        ShowFiles();
    }

    public string? FocusNote => _database is not { } db || _files is null ? null
        : FilesShown.Count == 0 ? $"{db} has no files on this server now"
        : $"showing the files of {db} only";

    public string? ShowAllText => _database is null ? null : _files is null ? "Show all" : $"Show all {_files.Count} files";

    private List<DatabaseFileVm> FilesShown =>
        _database is { } db
            ? (_files ?? []).Where(f => string.Equals(f.DatabaseName, db, StringComparison.OrdinalIgnoreCase)).ToList()
            : _files ?? [];

    private void ShowFiles()
    {
        if (_files is null)
            return;
        GridFiles.ItemsSource = FilesShown;
        MainWindow.ReportContextChanged(this);
    }

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#150): files near their maximum size, auto-shrink, or the storage at a glance.
    internal DatabaseStorageViewModel ViewModel { get; }

    private DatabaseFile? _subject;

    /// <param name="folds">Where the verdict's and the sizes' folds are remembered; none in some tests.</param>
    public DatabaseStoragePage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new DatabaseStorageViewModel(showFile: ShowSubject);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    // The verdict's Show the file: the Database Files tab with the file selected and in view (while
    // an alert's focus shows one database only, a file of another stays hidden until Show all).
    private void ShowSubject()
    {
        if (_subject is not { } subject)
            return;
        Tabs.SelectedItem = TabFiles;
        if (GridFiles.Items.OfType<DatabaseFileVm>().FirstOrDefault(f => f.File == subject) is { } row)
        {
            GridFiles.SelectedItem = row;
            GridFiles.ScrollIntoView(row);
        }
    }

    public async Task RefreshAsync()
    {
        IEnumerable<ServerConfigParam> config;
        IEnumerable<DatabaseFile> files;
        IEnumerable<TempDbFileSummary> tempFiles;
        IEnumerable<TableStorage> tables;
        try
        {
            (config, files, tempFiles, tables) = await _repo.GetDatabaseStorageAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the database storage", ex);
            throw;
        }

        var fileList    = files.ToList();
        var tempList    = tempFiles.ToList();
        var configList  = config.ToList();
        var tableList   = tables.ToList();

        ViewModel.Show(fileList, tempList);
        _subject = DbStorageVerdict.Subject(fileList);

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
        KpiMaxDop.Text        = maxDop  != null ? maxDop.CurrentValue.ToString(CultureInfo.CurrentCulture)  : "—";
        KpiCostThreshold.Text = costThr != null ? costThr.CurrentValue.ToString(CultureInfo.CurrentCulture) : "—";

        // Folded, the sizes say it in one line (#150).
        SizesSection.FoldedSummary = $"{FormatMB(totalMB)} in all · data {FormatMB(dataMB)} · log {FormatMB(logMB)} · TempDB {FormatMB(tempDbMB)} · " +
                                     $"MAXDOP {KpiMaxDop.Text} · cost threshold {KpiCostThreshold.Text}";

        // Grids
        _files = fileList.Select(f => new DatabaseFileVm(f)).ToList();
        ShowFiles();
        GridTempDb.ItemsSource = tempList;
        GridConfig.ItemsSource = configList.Select(c => new ServerConfigVm(c)).ToList();
        GridTables.ItemsSource = tableList;
    }

    private static string FormatMB(decimal mb)
    {
        if (mb >= 1024) return $"{mb / 1024:N2} GB";
        return $"{mb:N1} MB";
    }
}
