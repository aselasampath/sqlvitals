using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Read and write latency for each database file (#41, #187). sys.dm_io_virtual_file_stats only
/// holds totals since each database came online, so the page reads it every few seconds while it's
/// shown and shows the latency over the change between readings (<see cref="FileIoTracker"/>).
/// Files over the threshold for their type are highlighted, and the files on each volume are added
/// up to show whether the storage itself is slow. The verdict (#150) names the slow files.
/// </summary>
public sealed partial class FileIoViewModel : PageViewModel
{
    /// <summary>The filter the verdict's Show the slow files picks.</summary>
    internal const int SlowFilter = 1;

    private static readonly (string Label, Func<FileLatency, bool> Keep, string Empty)[] Filters =
    [
        ("All files",              _ => true,
            "There are no database files to show."),
        ("Slower than threshold",  f => f.IsSlow,
            "No file's reads or writes averaged more than the thresholds above."),
        ("With I/O",               f => f.Change is { IsIdle: false },
            "No file read or wrote anything between the readings."),
        ("Data files",             f => !f.IsLog,
            "There are no data files to show."),
        ("Log files",              f => f.IsLog,
            "There are no log files to show."),
    ];

    private readonly IFileIoRepository         _repo;
    private readonly ConnectionSettingsService _settings;
    private readonly FileIoTracker             _tracker = new();

    private FileLatencyThresholds _thresholds;

    // What was measured last; changing the filter or window only shows it again.
    private FileLatencyList? _list;

    // Why the last reading couldn't be taken (no permission); readings stop until Start over.
    private string? _problem;

    // Set once the choices are filled in, so filling them doesn't save anything.
    private readonly bool _ready;

    /// <param name="settings">The thresholds and interval, read and saved.</param>
    public FileIoViewModel(IFileIoRepository repo, ConnectionSettingsService settings)
    {
        _repo     = repo;
        _settings = settings;

        var store = settings.Load();
        _thresholds = store.FileLatencyThresholds;
        ShowThresholds(_thresholds);
        IntervalIndex   = Math.Max(0, Array.IndexOf(ConnectionStore.FileIoIntervalChoices, store.FileIoIntervalSeconds));
        ShowsCountdown  = true;
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        Verdict         = FileIoVerdict.From(null);
        ShowMeasuring();
        _ready = true;
        PropertyChanged += OnOwnPropertyChanged;
    }

    // Paused or resumed, from the top bar or a failed reading: the notes say so and the bar follows (#140).
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsPaused))
            return;
        if (_list is not null) ReadNote = StatusText(_list);
        ShowMeasuring();
        ReportContextChanged();
    }

    // ── How it measures ───────────────────────────────────────────────────

    /// <summary>"5s", "10s", …: how often it reads.</summary>
    public IReadOnlyList<string> IntervalNames { get; } = ConnectionStore.FileIoIntervalChoices.Select(s => $"{s}s").ToList();

    [ObservableProperty]
    private int _intervalIndex;

    public int IntervalSeconds => ConnectionStore.FileIoIntervalChoices[Math.Clamp(IntervalIndex, 0, ConnectionStore.FileIoIntervalChoices.Length - 1)];

    // Another interval: remembered, and the wait for the next reading starts again.
    partial void OnIntervalIndexChanged(int value)
    {
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        if (!_ready)
            return;
        SaveSettings();
        ShowMeasuring();
        ReportContextChanged();
    }

    /// <summary>"The last interval", "Since the first reading".</summary>
    public IReadOnlyList<string> WindowNames { get; } = ["The last interval", "Since the first reading"];

    [ObservableProperty]
    private int _windowIndex;

    partial void OnWindowIndexChanged(int value) => Show();

    private FileIoWindow Window => WindowIndex == 1 ? FileIoWindow.SinceFirstReading : FileIoWindow.LastInterval;

    /// <summary>The thresholds' boxes, in ms; applied by Apply or Enter.</summary>
    [ObservableProperty]
    private string _dataMsText = string.Empty;

    [ObservableProperty]
    private string _logMsText = string.Empty;

    /// <summary>"(red at 5×)".</summary>
    public string CriticalHint { get; } = $"(red at {FileLatencyThresholds.CriticalFactor:0}×)";

    /// <summary>Why the thresholds typed can't be used; null when they can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsThresholdError))]
    private string? _thresholdError;

    public bool ShowsThresholdError => ThresholdError is not null;

    /// <summary>Folded, Measuring says it in one line (#150): "The last interval · every 10 s · data files over 20 ms, log files over 10 ms".</summary>
    [ObservableProperty]
    private string? _measuringSummary;

    /// <summary>What the latency is over, and what is highlighted; "Reading…" before the first reading.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    /// <summary>Something that makes the figures less than they seem, though they were read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNote))]
    private string? _note;

    public bool ShowsNote => Note is not null;

    // ── The files and the volumes ─────────────────────────────────────────

    public IReadOnlyList<string> FilterNames { get; } = Filters.Select(f => f.Label).ToList();

    [ObservableProperty]
    private int _filterIndex;

    partial void OnFilterIndexChanged(int value) => Show();

    /// <summary>The files the filter keeps, the slowest first.</summary>
    public GridRows<FileLatency> Files { get; } = [];

    /// <summary>The file in the drawer; kept across readings by its database and file id.</summary>
    [ObservableProperty]
    private FileLatency? _selectedFile;

    /// <summary>Each volume's files added up.</summary>
    public GridRows<VolumeLatency> Volumes { get; } = [];

    /// <summary>"12 files", "3 of 12 files".</summary>
    [ObservableProperty]
    private string _countText = "Files";

    /// <summary>"1 volume", "3 volumes".</summary>
    [ObservableProperty]
    private string _volumesText = "Volumes";

    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>A file to show: the verdict's Show the slow files selected it. The view scrolls to it, past a search, and opens the drawer.</summary>
    public event Action<FileLatency>? ShowRow;

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>
    /// Takes a reading and shows the latency since the one it's compared with: when the page is
    /// opened, on Refresh or Start over, and every interval while not paused.
    /// </summary>
    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (_tracker.Latest is null && _problem is null)
            ReadNote = "Reading the file I/O totals…";
        var reading = await _repo.ReadFileIoAsync(cancel);
        _problem = reading.Problem;
        _tracker.Add(reading);
        if (_problem is not null)
            SetPaused(true);   // it won't work on the next tick either
        Show();
    }

    // Nothing measured yet: the verdict says why. Later, the readings so far stay shown.
    protected override void ShowLoadError(Exception exception)
    {
        if (_tracker.Latest is not null)
            return;
        ReadNote = string.Empty;
        ShowError("the file I/O totals", exception);
    }

    private void Show()
    {
        _list = _problem is { } problem
            ? FileLatencyList.Unavailable(problem, _thresholds)
            : _tracker.Latencies(Window, _thresholds);
        if (_list is not { } list) return;

        var filter = Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)];
        var shown  = list.Files.Where(filter.Keep).ToList();

        // Keep the same file across readings.
        var selected = SelectedFile?.File.Key;
        Files.Replace(shown);
        SelectedFile = shown.FirstOrDefault(f => f.File.Key == selected);
        Volumes.Replace(list.Volumes);

        Verdict = FileIoVerdict.From(list);
        ShowMeasuring();

        CountText = !list.Available ? "Files"
            : shown.Count == list.Files.Count ? $"{list.Files.Count:N0} file{(list.Files.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Files.Count:N0} files";
        VolumesText = list.Volumes.Count == 1 ? "1 volume" : $"{list.Volumes.Count:N0} volumes";

        Note        = list.Available ? list.Note : null;
        ReadNote    = StatusText(list);
        NoDataText  = !list.Available ? list.Problem : filter.Empty;
        ShowsNoData = shown.Count == 0;
    }

    private void ShowMeasuring() =>
        MeasuringSummary = $"{(Window == FileIoWindow.SinceFirstReading ? "Since the first reading" : "The last interval")} · " +
                           $"every {IntervalSeconds} s{(IsPaused ? " (paused)" : "")} · {_thresholds.Describe()}";

    private string StatusText(FileLatencyList list)
    {
        if (!list.Available)
            return string.Empty;

        var paused = IsPaused ? " Paused: the top bar's Refresh takes a reading." : "";
        if (list.Measuring)
            return $"First reading taken at {list.To:HH:mm:ss} (server time). Latency needs a second: it's the change " +
                   $"between two readings, not the totals since startup.{paused}";

        var span = DatabaseBackup.FormatAge(TimeSpan.FromSeconds(list.Seconds));
        if (list.Seconds < 60)
            span = $"{list.Seconds:N0} s";
        var over = Window == FileIoWindow.SinceFirstReading
            ? $"the {span} since the first reading"
            : $"the {span} between the last two readings";
        return $"Latency over {over} ({list.From:HH:mm:ss}–{list.To:HH:mm:ss} server time), highlighted for " +
               $"{list.Thresholds.Describe()}.{paused}";
    }

    /// <summary>Forgets the readings so far and reads again, from a new first reading; resumes if paused.</summary>
    [RelayCommand]
    private Task StartOver()
    {
        _tracker.StartOver();
        _problem = null;
        SetPaused(false);
        return RefreshAsync();
    }

    // ── Thresholds ────────────────────────────────────────────────────────

    private void ShowThresholds(FileLatencyThresholds thresholds)
    {
        DataMsText = FileLatencyThresholds.Format(thresholds.DataMs);
        LogMsText  = FileLatencyThresholds.Format(thresholds.LogMs);
    }

    /// <summary>
    /// Reads the two boxes, saving them when they changed, and shows the readings against them.
    /// The readings themselves don't change, so nothing is read again.
    /// </summary>
    [RelayCommand]
    private void ApplyThresholds()
    {
        if (!FileLatencyThresholds.TryParse(DataMsText, LogMsText, out var thresholds, out var error))
        {
            ThresholdError = error;
            return;
        }

        ThresholdError = null;
        ShowThresholds(thresholds!);
        if (thresholds! == _thresholds)
            return;

        _thresholds = thresholds!;
        SaveSettings();
        ShowMeasuring();
        Show();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.SetFileIoSettings(_thresholds, IntervalSeconds);
        }
        catch (Exception ex)
        {
            // The page still uses them; they just won't be remembered.
            AppLog.Error("File I/O: saving the settings failed", ex);
        }
    }

    // ── The verdict ───────────────────────────────────────────────────────

    // The verdict's Show the slow files: only those, the slowest selected and in the drawer.
    protected override void OnAction(VerdictAction action)
    {
        if (action.Key != FileIoVerdict.ShowSlow)
            return;
        FilterIndex = SlowFilter;   // shows them again
        if (_list?.Slowest is not { } slowest || Files.FirstOrDefault(f => f.File.Key == slowest.File.Key) is not { } row)
            return;
        SelectedFile = row;
        ShowRow?.Invoke(row);
    }
}
