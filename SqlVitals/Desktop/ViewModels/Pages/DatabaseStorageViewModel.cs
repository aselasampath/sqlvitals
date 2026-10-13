using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// DB Storage (#185): the sizes as cards, then every database file, TempDB's files, the server's
/// configuration and the largest tables on tabs, with the verdict on files near their maximum size
/// or set to auto-shrink (#150). Opened from a log space alert (#145) it lists the files of that
/// database only, until Show all.
/// </summary>
public sealed partial class DatabaseStorageViewModel(IStorageRepository repo) : PageViewModel
{
    /// <summary>The tabs, in the view's order.</summary>
    public enum Tab { Files, TempDb, Config, Tables }

    /// <summary>A database file as the grid shows it: the maximum size and the YES flags in words.</summary>
    public sealed class FileRow(DatabaseFile file)
    {
        /// <summary>The file read, for the verdict's Show the file (#150).</summary>
        public DatabaseFile File => file;

        public string   DatabaseName        => file.DatabaseName;
        public string   State               => file.State;
        public string   RecoveryModel       => file.RecoveryModel;
        public int      CompatibilityLevel  => file.CompatibilityLevel;
        public string   FileType            => file.FileType;
        public string   LogicalFileName     => file.LogicalFileName;
        public string   PhysicalPath        => file.PhysicalPath;
        public decimal  FileSizeMB          => file.FileSizeMB;
        public decimal? SpaceUsedMB         => file.SpaceUsedMB;
        public decimal? FreeSpaceMB         => file.FreeSpaceMB;
        public string   MaxSizeMBDisplay    => file.MaxSizeMB == -1 ? "Unlimited" : file.MaxSizeMB.ToString("N2", CultureInfo.CurrentCulture);
        public string   AutoGrowth          => file.AutoGrowth;
        public string   AutoShrinkDisplay   => file.AutoShrink   ? "YES" : "";
        public string   QueryStoreOnDisplay => file.QueryStoreOn ? "YES" : "";
        public string   EncryptedDisplay    => file.Encrypted    ? "YES" : "";
    }

    /// <summary>A server configuration parameter as the grid shows it.</summary>
    public sealed class ConfigRow(ServerConfigParam parameter)
    {
        public string ParameterName    => parameter.ParameterName;
        public long   CurrentValue     => parameter.CurrentValue;
        public long   MinValue         => parameter.MinValue;
        public long   MaxValue         => parameter.MaxValue;
        public string Description      => parameter.Description;
        public string IsDynamicDisplay => parameter.IsDynamic ? "Yes" : "No";
    }

    private const string NotRead = "—";

    [ObservableProperty] private string _totalSize     = NotRead;
    [ObservableProperty] private string _dataSize      = NotRead;
    [ObservableProperty] private string _logSize       = NotRead;
    [ObservableProperty] private string _tempDbSize    = NotRead;
    [ObservableProperty] private string _maxDop        = NotRead;
    [ObservableProperty] private string _costThreshold = NotRead;

    /// <summary>Folded, the sizes say it in one line (#150).</summary>
    [ObservableProperty]
    private string? _sizesSummary;

    /// <summary>The tab shown; the verdict's Show the file opens the files'.</summary>
    [ObservableProperty]
    private int _selectedTab;

    /// <summary>The database files shown: every one, or those of the database an alert names.</summary>
    public GridRows<FileRow> Files { get; } = [];

    /// <summary>The selected file; kept across a refresh by its database and logical name.</summary>
    [ObservableProperty]
    private FileRow? _selectedFile;

    public GridRows<TempDbFileSummary> TempDbFiles { get; } = [];

    public GridRows<ConfigRow> Config { get; } = [];

    public GridRows<TableStorage> Tables { get; } = [];

    /// <summary>A row to scroll into view: the verdict's Show the file selected it.</summary>
    public event Action<object>? ShowRow;

    // Every file read; null until the first read.
    private List<FileRow>? _files;

    // The file the verdict is about.
    private DatabaseFile? _subject;

    // The database a log space alert opened the page on (#145); null shows every database.
    private string? _database;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        var (config, files, tempFiles, tables) = await repo.GetDatabaseStorageAsync(cancel);
        var fileList   = files.ToList();
        var tempList   = tempFiles.ToList();
        var configList = config.ToList();

        Verdict  = DbStorageVerdict.From(fileList, tempList);
        _subject = DbStorageVerdict.Subject(fileList);

        var totalMB  = fileList.Sum(f => f.FileSizeMB);
        var dataMB   = fileList.Where(f => f.FileType == "ROWS").Sum(f => f.FileSizeMB);
        var logMB    = fileList.Where(f => f.FileType == "LOG").Sum(f => f.FileSizeMB);
        var tempDbMB = tempList.Sum(f => f.SizeMB);
        TotalSize  = FormatMB(totalMB);
        DataSize   = FormatMB(dataMB);
        LogSize    = FormatMB(logMB);
        TempDbSize = FormatMB(tempDbMB);
        MaxDop        = Setting(configList, "max degree of parallelism");
        CostThreshold = Setting(configList, "cost threshold for parallelism");
        SizesSummary  = $"{TotalSize} in all · data {DataSize} · log {LogSize} · TempDB {TempDbSize} · " +
                        $"MAXDOP {MaxDop} · cost threshold {CostThreshold}";

        _files = fileList.Select(f => new FileRow(f)).ToList();
        ShowFiles();
        TempDbFiles.Replace(tempList);
        Config.Replace(configList.Select(c => new ConfigRow(c)));
        Tables.Replace(tables);
    }

    private static string Setting(List<ServerConfigParam> config, string name) =>
        config.FirstOrDefault(c => c.ParameterName == name) is { } setting ? setting.CurrentValue.ToString(CultureInfo.CurrentCulture) : NotRead;

    internal static string FormatMB(decimal mb) => mb >= 1024 ? $"{mb / 1024:N2} GB" : $"{mb:N1} MB";

    protected override void ShowLoadError(Exception exception) => ShowError("the database storage", exception);

    // ── A log space alert's evidence (#145): the database's files, until Show all ──

    /// <summary>Lists the files of <paramref name="database"/> only; null lists them all again (Show all).</summary>
    public void FocusOn(string? database)
    {
        _database = database;
        ShowFiles();
    }

    /// <summary>For the banner: "showing the files of SalesDb only"; null before the first read or with no focus.</summary>
    public string? FocusNote => _database is not { } db || _files is null ? null
        : Files.Count == 0 ? $"{db} has no files on this server now"
        : $"showing the files of {db} only";

    /// <summary>Show all's words: "Show all 214 files"; null with no focus.</summary>
    public string? ShowAllText => _database is null ? null : _files is null ? "Show all" : $"Show all {_files.Count} files";

    // The files the focus keeps, the same one selected again.
    private void ShowFiles()
    {
        if (_files is null)
            return;
        (string, string)? selected = SelectedFile is { } row ? (row.DatabaseName, row.LogicalFileName) : null;
        Files.Replace(_database is { } db
            ? _files.Where(f => string.Equals(f.DatabaseName, db, StringComparison.OrdinalIgnoreCase))
            : _files);
        SelectedFile = selected is { } key ? Files.FirstOrDefault(f => (f.DatabaseName, f.LogicalFileName) == key) : null;
    }

    // The verdict's Show the file: the files' tab with the file selected and in view (while an
    // alert's focus shows one database only, a file of another stays hidden until Show all).
    protected override void OnAction(VerdictAction action)
    {
        if (action.Key != DbStorageVerdict.ShowFile || _subject is not { } subject)
            return;
        SelectedTab = (int)Tab.Files;
        if (Files.FirstOrDefault(f => f.File == subject) is not { } row)
            return;
        SelectedFile = row;
        ShowRow?.Invoke(row);
    }
}
