using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Pages;

// ── View-model for a single checkbox row ─────────────────────────────────────
public sealed class ExportGroupItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Page ─────────────────────────────────────────────────────────────────────
public partial class ExportPage : Page, IRefreshable, IPageContext
{
    // Builds a report to save or paste into an AI chat: not a view of the server's data (#140).
    public PageKind Kind => PageKind.None;

    private readonly ExportService                    _svc;
    private readonly ObservableCollection<ExportGroupItem> _items = [];

    // Friendly one-line descriptions for each group
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        [ExportService.G_TOP_WAITS]        = "Top 25 wait types by total wait time",
        [ExportService.G_ACTIVE_WAITS]     = "Currently waiting sessions",
        [ExportService.G_SIGNAL_VS_RES]    = "Signal (CPU) vs resource wait split",
        [ExportService.G_TEMPDB]           = "TempDB contention & space usage",
        [ExportService.G_MEMORY_GRANTS]    = "Pending / executing memory grant requests",
        [ExportService.G_QUERY_STORE]      = "Query Store plan forcing & health",
        [ExportService.G_INDEX_HEALTH]     = "Missing index DMV suggestions + unused indexes",
        [ExportService.G_RESOURCE_QUERIES] = "Top queries by reads & CPU",
        [ExportService.G_INDEX_USAGE]      = "Seeks / scans / lookups per index",
        [ExportService.G_INDEX_FRAG]       = "Fragmentation % for indexes > 1 000 pages",
        [ExportService.G_IMPLICIT_CONV]    = "Implicit type conversions causing scans",
        [ExportService.G_STALE_STATS]      = "Statistics with high modification counters",
        [ExportService.G_DB_STORAGE]       = "DB / log / tempdb sizes, server config, top tables",
        [ExportService.G_SP_TRACE]         = "Stored procedure execution counts, duration & reads",
    };

    // The page template's verdict (#150): what an export will write and where, or what the last one did.
    internal ExportViewModel ViewModel { get; }

    // What the last export from this page did; null before the first.
    private ExportOutcome? _last;

    /// <param name="folds">Where the verdict's and the explanation's folds are remembered; none in some tests.</param>
    public ExportPage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _svc = new ExportService(repo);
        InitializeComponent();

        ViewModel = new ExportViewModel(export: async () => await ExportAsync(), open: OpenLast);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;

        // Build the item list from the canonical AllGroups array
        foreach (var g in ExportService.AllGroups)
        {
            var item = new ExportGroupItem
            {
                Name        = g,
                Description = Descriptions.TryGetValue(g, out var d) ? d : "",
                IsChecked   = true,
            };
            item.PropertyChanged += (_, _) => UpdateCheckedCount();
            _items.Add(item);
        }

        GroupList.ItemsSource = _items;

        // Seed default path to Desktop
        TxtFilePath.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        TxtFilePath.TextChanged += (_, _) => RefreshFullPath();
        TxtFileName.TextChanged += (_, _) => RefreshFullPath();

        UpdateCheckedCount();
        RefreshFullPath();
    }

    // What an export would write now, unless the last one is still the news.
    private void UpdateVerdict() =>
        ViewModel.Show(_items.Count(i => i.IsChecked), _items.Count, GetFullPath(), _last);

    // A choice changed after an export: the verdict is about the next one again.
    private void ChoiceChanged()
    {
        _last = null;
        UpdateVerdict();
    }

    // The verdict's Open the file, as "Open file after export" does.
    private void OpenLast()
    {
        if (_last is not { Error: null } last)
            return;
        try { Process.Start(new ProcessStartInfo(last.Path) { UseShellExecute = true }); }
        catch (Exception ex) { TxtExportStatus.Text = $"✗  Couldn't open it: {ex.Message}"; }
    }

    // ── Path helpers ─────────────────────────────────────────────────────────

    private void RefreshFullPath()
    {
        try
        {
            var dir  = TxtFilePath.Text.Trim();
            var file = TxtFileName.Text.Trim();
            TxtFullPath.Text = (dir.Length > 0 && file.Length > 0)
                ? Path.Combine(dir, file)
                : "";
        }
        catch { TxtFullPath.Text = ""; }
        ChoiceChanged();
    }

    private string? GetFullPath()
    {
        var dir  = TxtFilePath.Text.Trim();
        var file = TxtFileName.Text.Trim();
        if (dir.Length == 0 || file.Length == 0) return null;
        try   { return Path.Combine(dir, file); }
        catch { return null; }
    }

    // ── UI events ─────────────────────────────────────────────────────────────

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title            = "Choose export file location",
            Filter           = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt       = ".txt",
            FileName         = TxtFileName.Text.Trim(),
            InitialDirectory = TxtFilePath.Text.Trim(),
        };
        if (dlg.ShowDialog() == true)
        {
            TxtFilePath.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
            TxtFileName.Text = Path.GetFileName(dlg.FileName);
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsChecked = true;
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsChecked = false;
    }

    private void CheckBox_Changed(object sender, RoutedEventArgs e)
        => UpdateCheckedCount();

    private void UpdateCheckedCount()
    {
        var n = _items.Count(i => i.IsChecked);
        TxtCheckedCount.Text = $"{n} of {_items.Count} selected";
        BtnExport.IsEnabled  = n > 0;
        ChoiceChanged();
    }

    // ── Export ─────────────────────────────────────────────────────────────────

    private async void BtnExport_Click(object sender, RoutedEventArgs e) => await ExportAsync();

    // Export Now, from the button or the verdict.
    private async Task ExportAsync()
    {
        if (!BtnExport.IsEnabled)
            return;   // an export is running, or nothing is selected

        var path = GetFullPath();

        if (path is null)
        {
            MessageBox.Show("Please enter a valid folder and filename.",
                            "Missing path", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = _items.Where(i => i.IsChecked).Select(i => i.Name).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Please select at least one counter group.",
                            "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Create directory if needed
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot create directory:\n{ex.Message}",
                            "Path error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Disable UI while exporting
        SetExporting(true);
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.IsIndeterminate = true;

        var sw = Stopwatch.StartNew();

        try
        {
            var progress = new Progress<string>(msg =>
            {
                TxtExportStatus.Text = msg;
            });

            await _svc.ExportAsync(selected, path, progress);

            sw.Stop();
            var info     = new FileInfo(path);
            var sizeKb   = info.Length / 1024.0;
            var summary  = $"{selected.Count} group(s) · {sizeKb:F1} KB · {sw.Elapsed.TotalSeconds:F1}s";

            TxtExportStatus.Text       = $"✓  Saved: {path} · {summary}";
            _last = new ExportOutcome(path, selected.Count, info.Length, sw.Elapsed);
            UpdateVerdict();

            if (ChkOpenAfter.IsChecked == true)
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch { /* notepad not mandatory */ }
            }
        }
        catch (Exception ex)
        {
            TxtExportStatus.Text = $"✗  Export failed: {ex.Message}";
            _last = new ExportOutcome(path, selected.Count, 0, sw.Elapsed, ex.Message);
            UpdateVerdict();
            MessageBox.Show($"Export failed:\n\n{ex.Message}",
                            "Export error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExporting(false);
            ExportProgress.IsIndeterminate = false;
            ExportProgress.Value           = 100;
        }
    }

    private void SetExporting(bool busy)
    {
        BtnExport.IsEnabled      = !busy;
        BtnSelectAll.IsEnabled   = !busy;
        BtnSelectNone.IsEnabled  = !busy;
        TxtFilePath.IsEnabled    = !busy;
        TxtFileName.IsEnabled    = !busy;
        GroupList.IsEnabled      = !busy;
    }

    // Export is on-demand; auto-refresh is a no-op
    public Task RefreshAsync() => Task.CompletedTask;
}
