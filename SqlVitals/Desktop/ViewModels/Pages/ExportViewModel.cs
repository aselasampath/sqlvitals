using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Export / AI (#186): the sections of a plain text report of the open server to write, where to
/// save it, and the export itself, to save or paste into an AI chat. It reads nothing on its own:
/// Export Now reads the sections chosen. The verdict says what an export will write, or what the
/// last one did (#150).
/// </summary>
/// <param name="open">Opens a file as Windows would; the shell's by default.</param>
public sealed partial class ExportViewModel : PageViewModel
{
    /// <summary>One section of the report, and whether it is written.</summary>
    public sealed partial class Section(string name, string description) : ObservableObject
    {
        public string Name { get; } = name;

        public string Description { get; } = description;

        [ObservableProperty]
        private bool _isChecked = true;
    }

    // A line under each section's name.
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        [ExportService.GroupTopWaits]         = "Top 25 wait types by total wait time",
        [ExportService.GroupActiveWaits]      = "Currently waiting sessions",
        [ExportService.GroupSignalVsRes]      = "Signal (CPU) vs resource wait split",
        [ExportService.GroupTempdb]           = "TempDB contention & space usage",
        [ExportService.GroupMemoryGrants]     = "Pending / executing memory grant requests",
        [ExportService.GroupQueryStore]       = "Query Store plan forcing & health",
        [ExportService.GroupIndexHealth]      = "Missing index DMV suggestions + unused indexes",
        [ExportService.GroupResourceQueries]  = "Top queries by reads & CPU",
        [ExportService.GroupIndexUsage]       = "Seeks / scans / lookups per index",
        [ExportService.GroupIndexFrag]        = "Fragmentation % for indexes > 1 000 pages",
        [ExportService.GroupImplicitConv]     = "Implicit type conversions causing scans",
        [ExportService.GroupStaleStats]       = "Statistics with high modification counters",
        [ExportService.GroupDbStorage]        = "DB / log / tempdb sizes, server config, top tables",
        [ExportService.GroupSpTrace]          = "Stored procedure execution counts, duration & reads",
    };

    private readonly ExportService _export;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogs _files;
    private readonly TimeProvider _time;
    private readonly Action<string> _open;

    // What the last export from this page did; null before the first, and once a choice changes.
    private ExportOutcome? _last;

    /// <param name="export">Writes the report from the open connection (#166: made by the container).</param>
    public ExportViewModel(ExportService export, IDialogService dialogs, IFileDialogs files, TimeProvider? time = null, Action<string>? open = null)
    {
        _export  = export;
        _dialogs = dialogs;
        _files   = files;
        _time    = time ?? TimeProvider.System;
        _open    = open ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));

        Sections = ExportService.AllGroups.Select(g => new Section(g, Descriptions.GetValueOrDefault(g, ""))).ToList();
        foreach (var section in Sections)
            section.PropertyChanged += (_, _) => SectionsChanged();
        _filePath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        SectionsChanged();
    }

    /// <summary>Every section, in the report's order; all checked at first.</summary>
    public IReadOnlyList<Section> Sections { get; }

    private int CheckedCount => Sections.Count(s => s.IsChecked);

    /// <summary>"14 of 14 selected".</summary>
    [ObservableProperty]
    private string _checkedCountText = string.Empty;

    /// <summary>The folder to save to; the desktop at first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPathText))]
    private string _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPathText))]
    private string _fileName = "SqlVitals_Report.txt";

    partial void OnFilePathChanged(string value) => ChoiceChanged();

    partial void OnFileNameChanged(string value) => ChoiceChanged();

    /// <summary>Where it will be saved, or nothing while the folder or the name is missing.</summary>
    public string FullPathText => FullPath() ?? string.Empty;

    /// <summary>Opens the report once written, as Windows opens a .txt file.</summary>
    [ObservableProperty]
    private bool _openAfter = true;

    /// <summary>Writing now: the choices wait for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(SelectAllCommand), nameof(SelectNoneCommand), nameof(BrowseCommand))]
    private bool _isExporting;

    public bool IsIdle => !IsExporting;

    /// <summary>The progress bar: shown from the first export on, moving while it writes.</summary>
    [ObservableProperty]
    private bool _showsProgress;

    [ObservableProperty]
    private double _progress;

    /// <summary>The section being written, then "✓  Saved: …" or "✗  Export failed: …".</summary>
    [ObservableProperty]
    private string _exportStatus = string.Empty;

    // Export reads only when asked.
    protected override Task LoadAsync(CancellationToken cancel) => Task.CompletedTask;

    private string? FullPath()
    {
        var dir  = FilePath.Trim();
        var file = FileName.Trim();
        if (dir.Length == 0 || file.Length == 0)
            return null;
        try   { return Path.Combine(dir, file); }
        catch { return null; }
    }

    private void SectionsChanged()
    {
        CheckedCountText = $"{CheckedCount} of {Sections.Count} selected";
        ExportCommand.NotifyCanExecuteChanged();
        ChoiceChanged();
    }

    // A choice changed after an export: the verdict is about the next one again.
    private void ChoiceChanged()
    {
        _last = null;
        ShowVerdict();
    }

    // What an export would write now, unless the last one is still the news.
    private void ShowVerdict() => Verdict = ExportVerdict.From(CheckedCount, Sections.Count, FullPath(), _last);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectAll()
    {
        foreach (var section in Sections)
            section.IsChecked = true;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectNone()
    {
        foreach (var section in Sections)
            section.IsChecked = false;
    }

    /// <summary>"…": where to save it, in a file dialog; not while it writes, as the path boxes.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Browse()
    {
        var path = _files.Save(new FileDialogOptions("Choose export file location", "Text files (*.txt)|*.txt|All files (*.*)|*.*")
        {
            DefaultExt       = ".txt",
            FileName         = FileName.Trim(),
            InitialDirectory = FilePath.Trim(),
        });
        if (path is null)
            return;
        FilePath = Path.GetDirectoryName(path) ?? "";
        FileName = Path.GetFileName(path);
    }

    /// <summary>Export Now, from the button or the verdict: reads the sections chosen and writes the report.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (!CanExport())
            return;   // an export is running, or nothing is selected

        if (FullPath() is not { } path)
        {
            _dialogs.Warn("Please enter a valid folder and filename.", "Missing path");
            return;
        }
        var selected = Sections.Where(s => s.IsChecked).Select(s => s.Name).ToList();

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _dialogs.Error($"Cannot create directory:\n{ex.Message}", "Path error");
            return;
        }

        IsExporting   = true;
        ShowsProgress = true;
        var started   = _time.GetTimestamp();
        try
        {
            await _export.ExportAsync(selected, path, new Progress<string>(message => ExportStatus = message));

            var took  = _time.GetElapsedTime(started);
            var bytes = new FileInfo(path).Length;
            ExportStatus = $"✓  Saved: {path} · {selected.Count} group(s) · {bytes / 1024.0:F1} KB · {took.TotalSeconds:F1}s";
            _last = new ExportOutcome(path, selected.Count, bytes, took);
            ShowVerdict();

            if (OpenAfter)
            {
                try { _open(path); }
                catch (Exception ex) { AppLog.Error("Export: opening the report failed", ex); }   // not needed to export
            }
        }
        catch (Exception ex)
        {
            ExportStatus = $"✗  Export failed: {ex.Message}";
            _last = new ExportOutcome(path, selected.Count, 0, _time.GetElapsedTime(started), ex.Message);
            ShowVerdict();
            _dialogs.Error($"Export failed:\n\n{ex.Message}", "Export error");
        }
        finally
        {
            IsExporting = false;
            Progress    = 100;
        }
    }

    private bool CanExport() => !IsExporting && CheckedCount > 0;

    // Export is awaited by the verdict's command, so nothing it throws is lost (#169).
    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case ExportVerdict.Export: return ExportAsync();
            case ExportVerdict.Open:   OpenLast(); break;
        }
        return Task.CompletedTask;
    }

    // The verdict's Open the file, as "Open file after export" does.
    private void OpenLast()
    {
        if (_last is not { Error: null } last)
            return;
        try { _open(last.Path); }
        catch (Exception ex) { ExportStatus = $"✗  Couldn't open it: {ex.Message}"; }
    }
}
