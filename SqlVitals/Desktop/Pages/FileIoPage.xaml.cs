using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Read and write latency for each database file (#41). sys.dm_io_virtual_file_stats only holds
/// totals since each database came online, so the page reads it every few seconds while it's
/// open and shows the latency over the change between readings (<see cref="FileIoTracker"/>).
/// Files over the threshold for their type are highlighted, and the files on each volume are
/// added up to show whether the storage itself is slow.
/// </summary>
public partial class FileIoPage : Page, IRefreshable
{
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

    private readonly IWaitStatsRepository      _repo;
    private readonly ConnectionSettingsService _settings;
    private readonly FileIoTracker             _tracker = new();

    private FileLatencyThresholds _thresholds;
    private int _intervalSec;

    // What was measured last; changing the filter or window only shows it again.
    private FileLatencyList? _list;

    // Why the last reading couldn't be taken (no permission); readings stop until Start over.
    private string? _problem;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remainingSec;
    private bool _reading;
    private bool _paused;

    // Set once the choices are filled in, so filling them doesn't show or save anything.
    private readonly bool _ready;

    public FileIoPage(IWaitStatsRepository repo, ConnectionSettingsService settings)
    {
        _repo     = repo;
        _settings = settings;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;
        CmbWindow.SelectedIndex = 0;

        var store = settings.Load();
        _thresholds  = store.FileLatencyThresholds;
        _intervalSec = store.FileIoIntervalSeconds;
        ShowThresholds(_thresholds);

        foreach (var sec in ConnectionStore.FileIoIntervalChoices)
            CmbInterval.Items.Add(new ComboBoxItem { Content = $"{sec}s", Tag = sec });
        CmbInterval.SelectedIndex = Math.Max(0, Array.IndexOf(ConnectionStore.FileIoIntervalChoices, _intervalSec));

        _timer.Tick += Timer_Tick;

        // Reading stops when the page is left; a new page starts from a new first reading.
        Loaded += (_, _) =>
        {
            if (!_paused) _timer.Start();
            if (_tracker.Latest is not null || _problem is not null)
                Dispatcher.BeginInvoke(Show, DispatcherPriority.Background);
        };
        Unloaded += (_, _) => _timer.Stop();

        _ready = true;
    }

    /// <summary>
    /// Takes a reading and shows the latency since the one it's compared with. Called on
    /// navigate, by the sidebar Refresh, and by the timer.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_reading) return;
        _reading = true;
        if (_tracker.Latest is null && _problem is null)
            TxtStatus.Text = "Reading the file I/O totals…";
        try
        {
            var reading = await _repo.ReadFileIoAsync();
            _problem = reading.Problem;
            _tracker.Add(reading);
            if (_problem is not null)
                SetPaused(true);   // it won't work on the next tick either
            Show();
        }
        finally
        {
            _reading      = false;
            _remainingSec = _intervalSec;
            UpdateCountdown();
        }
    }

    private FileIoWindow MeasureWindow => CmbWindow.SelectedIndex == 1 ? FileIoWindow.SinceFirstReading : FileIoWindow.LastInterval;

    private void Show()
    {
        // Filled before the page has been laid out, the grids can keep every fixed-width column at
        // its 20 px minimum. A reading that arrives that soon is shown once it has been instead.
        if (!IsLoaded) return;

        _list = _problem is { } problem
            ? FileLatencyList.Unavailable(problem, _thresholds)
            : _tracker.Latencies(MeasureWindow, _thresholds);
        if (_list is not { } list) return;

        var filter = Filters[Math.Max(0, CmbFilter.SelectedIndex)];
        var shown  = list.Files.Where(filter.Keep).ToList();

        // Keep the same file across readings.
        var selected = (FilesGrid.SelectedItem as FileLatency)?.File.Key;
        DataGridRefresh.SetItemsSource(FilesGrid, shown);
        FilesGrid.SelectedItem = shown.FirstOrDefault(f => f.File.Key == selected);
        DataGridRefresh.SetItemsSource(VolumesGrid, list.Volumes);

        TxtCount.Text = !list.Available ? "Files"
            : shown.Count == list.Files.Count ? $"{list.Files.Count:N0} file{(list.Files.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Files.Count:N0} files";
        TxtVolumes.Text = list.Volumes.Count == 1 ? "1 volume" : $"{list.Volumes.Count:N0} volumes";

        ShowSummary(list);

        var note = list.Available ? list.Note : null;
        TxtNote.Text       = note ?? string.Empty;
        TxtNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text     = StatusText(list);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowDetail();
    }

    // "48 files · 2 slower than the threshold (1 five times over) · slowest: Sales_log writes 38.2 ms · 30 with no I/O".
    private void ShowSummary(FileLatencyList list)
    {
        TxtSummary.Inlines.Clear();
        if (!list.Available || list.Measuring)
        {
            TxtSummary.Visibility = Visibility.Collapsed;
            return;
        }
        TxtSummary.Visibility = Visibility.Visible;

        void Add(string text, Brush? brush = null, bool bold = false)
        {
            var run = new Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            if (brush is not null) run.Foreground = brush;
            TxtSummary.Inlines.Add(run);
        }
        var danger  = (Brush)FindResource("Danger");
        var warning = (Brush)FindResource("Warning");
        var muted   = (Brush)FindResource("TextMuted");

        Add($"{list.Files.Count:N0} file{(list.Files.Count == 1 ? "" : "s")}", bold: true);
        Add("  ·  ", muted);
        Add($"{list.SlowCount:N0} slower than the threshold", list.SlowCount > 0 ? warning : null, list.SlowCount > 0);
        if (list.CriticalCount > 0)
            Add($" ({list.CriticalCount:N0} of them at {FileLatencyThresholds.CriticalFactor:0}× or more)", danger, bold: true);

        if (list.Slowest is { } slowest)
        {
            Add("  ·  ", muted);
            // Reads and writes of a file share its threshold, so the slower one is the one over it.
            var (what, ms) = (slowest.WriteLatencyMs ?? 0) > (slowest.ReadLatencyMs ?? 0)
                ? ("writes", slowest.WriteLatencyText)
                : ("reads", slowest.ReadLatencyText);
            Add($"slowest: {slowest.DatabaseName} · {slowest.FileName} {what} {ms} ms",
                slowest.Status == FileLatencyStatus.Critical ? danger : warning);
        }

        Add("  ·  ", muted);
        Add($"{list.IdleCount:N0} with no I/O", muted);
        if (list.RestartedCount > 0)
        {
            Add("  ·  ", muted);
            Add($"{list.RestartedCount:N0} with counters reset", muted);
        }
    }

    private string StatusText(FileLatencyList list)
    {
        if (!list.Available)
            return string.Empty;

        var paused = _paused ? " Paused: the sidebar Refresh takes a reading." : "";
        if (list.Measuring)
            return $"First reading taken at {list.To:HH:mm:ss} (server time). Latency needs a second: it's the change " +
                   $"between two readings, not the totals since startup.{paused}";

        var span = DatabaseBackup.FormatAge(TimeSpan.FromSeconds(list.Seconds));
        if (list.Seconds < 60)
            span = $"{list.Seconds:N0} s";
        var over = MeasureWindow == FileIoWindow.SinceFirstReading
            ? $"the {span} since the first reading"
            : $"the {span} between the last two readings";
        return $"Latency over {over} ({list.From:HH:mm:ss}–{list.To:HH:mm:ss} server time), highlighted for " +
               $"{list.Thresholds.Describe()}.{paused}";
    }

    // ── The selected file ─────────────────────────────────────────────────────

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    private void ShowDetail()
    {
        if (FilesGrid.SelectedItem is not FileLatency file)
        {
            TxtDetail.Text = "Select a file for why it's highlighted and where it is.";
            return;
        }

        var where = string.IsNullOrEmpty(file.PhysicalName) ? "" : $" · {file.PhysicalName}";
        TxtDetail.Text = $"{file.DatabaseName} · {file.FileName} ({file.TypeText}){where}. {file.Explanation}";
    }

    // ── Thresholds ────────────────────────────────────────────────────────────

    private void ShowThresholds(FileLatencyThresholds thresholds)
    {
        TxtDataMs.Text = FileLatencyThresholds.Format(thresholds.DataMs);
        TxtLogMs.Text  = FileLatencyThresholds.Format(thresholds.LogMs);
        TxtCriticalHint.Text = $"(red at {FileLatencyThresholds.CriticalFactor:0}×)";
    }

    // Reads the two boxes, saving them when they changed, and shows the readings against them.
    // The readings themselves don't change, so nothing is read again.
    private void ApplyThresholds()
    {
        if (!FileLatencyThresholds.TryParse(TxtDataMs.Text, TxtLogMs.Text, out var thresholds, out var error))
        {
            TxtError.Text       = error;
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        TxtError.Visibility = Visibility.Collapsed;
        ShowThresholds(thresholds!);
        if (thresholds! == _thresholds)
            return;

        _thresholds = thresholds!;
        SaveSettings();
        Show();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.SetFileIoSettings(_thresholds, _intervalSec);
        }
        catch (Exception ex)
        {
            // The page still uses them; they just won't be remembered.
            AppLog.Error("File I/O: saving the settings failed", ex);
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e) => ApplyThresholds();

    private void Threshold_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        ApplyThresholds();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    private void CmbWindow_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is not ComboBoxItem { Tag: int sec })
            return;

        _intervalSec  = sec;
        _remainingSec = sec;
        UpdateCountdown();
        if (_ready) SaveSettings();
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) => SetPaused(!_paused);

    private void SetPaused(bool paused)
    {
        _paused = paused;
        BtnPause.Content = paused ? "▶ Resume" : "⏸ Pause";
        if (paused)
        {
            _timer.Stop();
            TxtCountdown.Text = "--";
        }
        else
        {
            _remainingSec = _intervalSec;
            UpdateCountdown();
            if (IsLoaded) _timer.Start();
        }
        if (_list is not null) TxtStatus.Text = StatusText(_list);
    }

    private async void BtnStartOver_Click(object sender, RoutedEventArgs e)
    {
        _tracker.StartOver();
        _problem = null;
        SetPaused(false);
        await ReadNowAsync();
    }

    // ── Reading on a timer ────────────────────────────────────────────────────

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_reading) return;
        _remainingSec--;
        UpdateCountdown();
        if (_remainingSec > 0) return;

        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            // The readings so far are kept: the next one is compared with the last that worked.
            MainWindow.ReportBackgroundError(this, "File I/O", ex);
        }
    }

    // Start over reports its own errors; the sidebar Refresh reports through MainWindow.
    private async Task ReadNowAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("File I/O: read failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    private void UpdateCountdown()
    {
        if (_paused)
            return;
        TxtCountdown.Text = $"{Math.Max(0, _remainingSec)}s";
    }
}
