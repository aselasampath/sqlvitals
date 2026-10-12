using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Monitoring &amp; history (#131): Start monitoring automatically, Command timeout,
/// the history's snapshot interval and retention, how query text is kept (#175), and Clear history
/// and Clear stored query text. Each saves as it changes.
/// </summary>
internal partial class MonitoringCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;

    // The history file grows with every sample and shrinks after a purge, which the writer
    // runs on its own schedule, so the size shown is re-read while the category is open, on the
    // app's refresh clock (#169).
    private static readonly TimeSpan HistorySizeInterval = TimeSpan.FromSeconds(5);
    private IRefreshSchedule? _historySizeTimer;

    // Set while the history choices are filled in or put back in code, so they aren't saved.
    private bool _isLoadingHistorySettings;
    private int  _savedIntervalMinutes;
    private int  _savedRetentionDays;
    private int  _savedTimeout;
    private QueryTextStorage _savedQueryText;

    public MonitoringCategory(SettingsContext context)
    {
        _context = context;
        // Before InitializeComponent: the button binds to it, and it raises no change.
        ClearQueryText = new RelayCommand(() => _ = ClearQueryTextAsync());
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Monitoring).Subtitle;

        var store = _context.Service.Load();
        ChkStartMonitoringAutomatically.IsChecked = store.StartMonitoringAutomatically;
        _savedTimeout   = store.CommandTimeoutSeconds;
        TxtTimeout.Text = _savedTimeout.ToString(CultureInfo.CurrentCulture);
        LoadHistorySettings(store);
        UpdateHistorySize();

        Loaded += (_, _) =>
        {
            UpdateHistorySize();
            _historySizeTimer?.Dispose();
            _historySizeTimer = CompositionRoot.Get<IRefreshScheduler>().Every(HistorySizeInterval, () =>
            {
                UpdateHistorySize();
                return Task.CompletedTask;
            });
        };
        Unloaded += (_, _) =>
        {
            _historySizeTimer?.Dispose();
            _historySizeTimer = null;
        };
    }

    public async Task<bool> TryLeaveAsync()
    {
        // A timeout still being typed is saved on the way out, as leaving the box would.
        await SaveTimeoutAsync();
        return true;
    }

    // ── Monitoring ────────────────────────────────────────────────────────────

    // Applies straight away: on starts the active connection and the background ones, off stops
    // every session (their active alerts end).
    private async void ChkStartMonitoringAutomatically_Click(object sender, RoutedEventArgs e)
    {
        var start = ChkStartMonitoringAutomatically.IsChecked == true;
        try
        {
            _context.Service.SetStartMonitoringAutomatically(start);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtMonitoringStatus, $"Could not save the setting: {SettingsContext.Redact(ex.Message)}", success: false);
            ChkStartMonitoringAutomatically.IsChecked = !start;
            return;
        }

        try
        {
            await _context.ApplyConnections(false);
            SettingsContext.ShowStatus(TxtMonitoringStatus,
                start
                    ? $"Saved: {MonitoredText()} now, and from every start of SqlVitals."
                    : "Saved: monitoring stopped for every connection. Nothing is collected until you press Start.",
                success: true);
        }
        catch (Exception ex)
        {
            // Saved, so the switch stays as it is; it applies at the latest when SqlVitals next starts.
            SettingsContext.ShowStatus(TxtMonitoringStatus, $"Saved, but it couldn't be applied straight away: {SettingsContext.Redact(ex.Message)}", success: false);
        }
    }

    private string MonitoredText() => _context.Monitoring.MonitoredCount switch
    {
        0     => "no connection can be monitored yet (the active one needs its password, or there is none)",
        1     => "1 connection is monitored",
        var n => $"{n} connections are monitored",
    };

    private async void TxtTimeout_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => await SaveTimeoutAsync().Reported("save the command timeout");

    private async void TxtTimeout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SaveTimeoutAsync().Reported("save the command timeout");
    }

    /// <summary>Saves the command timeout when it changed and can be used; says why not otherwise.</summary>
    internal async Task SaveTimeoutAsync()
    {
        if (!int.TryParse(TxtTimeout.Text.Trim(), out var timeout) || timeout <= 0)
        {
            SettingsContext.ShowStatus(TxtMonitoringStatus,
                $"Command timeout must be a positive whole number of seconds. Still {_savedTimeout} s.", success: false);
            return;
        }
        if (timeout == _savedTimeout)
            return;

        try
        {
            _context.Service.SetCommandTimeout(timeout);
            _savedTimeout = timeout;
            // Every page's queries, and the monitoring sessions, pick it up.
            await _context.ApplyConnections(false);
            SettingsContext.ShowStatus(TxtMonitoringStatus, $"Saved: queries may run for {timeout} s before they're stopped.", success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtMonitoringStatus, $"Could not save the command timeout: {SettingsContext.Redact(ex.Message)}", success: false);
        }
    }

    // ── Monitoring history ────────────────────────────────────────────────────

    private void LoadHistorySettings(ConnectionStore store)
    {
        _isLoadingHistorySettings = true;
        try
        {
            foreach (var minutes in HistorySettings.IntervalChoicesMinutes)
                CmbHistoryInterval.Items.Add(new ComboBoxItem { Content = MinutesText(minutes), Tag = minutes });
            foreach (var days in HistorySettings.RetentionChoicesDays)
                CmbHistoryRetention.Items.Add(new ComboBoxItem { Content = $"{days} days", Tag = days });

            ShowHistorySettings(store.HistoryIntervalMinutes, store.HistoryRetentionDays);

            foreach (var storage in QueryTextStorages.All)
                CmbQueryText.Items.Add(new ComboBoxItem { Content = QueryTextStorages.Describe(storage), Tag = storage });
            ShowQueryText(store.QueryTextStorage);
        }
        finally
        {
            _isLoadingHistorySettings = false;
        }
    }

    // Selects the saved choices; the values are always ones offered (see HistorySettings.Normalize*).
    private void ShowHistorySettings(int intervalMinutes, int retentionDays)
    {
        _savedIntervalMinutes = intervalMinutes;
        _savedRetentionDays   = retentionDays;
        CmbHistoryInterval.SelectedItem  = FindChoice(CmbHistoryInterval, intervalMinutes);
        CmbHistoryRetention.SelectedItem = FindChoice(CmbHistoryRetention, retentionDays);
    }

    internal static ComboBoxItem FindChoice(ComboBox combo, int value) =>
        combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == value)
        ?? (ComboBoxItem)combo.Items[0];

    private void HistorySetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingHistorySettings ||
            CmbHistoryInterval.SelectedItem is not ComboBoxItem { Tag: int minutes } ||
            CmbHistoryRetention.SelectedItem is not ComboBoxItem { Tag: int days })
            return;
        if (minutes == _savedIntervalMinutes && days == _savedRetentionDays)
            return;

        if (days < _savedRetentionDays)
        {
            var confirm = _context.Ask(
                $"Keep monitoring history for {days} days instead of {_savedRetentionDays}?\n\n" +
                $"History older than {days} days is deleted straight away, for every connection. This cannot be undone.",
                "Monitoring History", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                RevertHistorySettings();
                return;
            }
        }

        try
        {
            _context.Service.SetHistorySettings(minutes, days);
            var store = _context.Service.Load();
            _context.Monitoring.ApplyHistorySettings(store);
            _savedIntervalMinutes = store.HistoryIntervalMinutes;
            _savedRetentionDays   = store.HistoryRetentionDays;

            SettingsContext.ShowStatus(TxtHistoryStatus,
                $"Saved: a snapshot every {MinutesText(store.HistoryIntervalMinutes)}, " +
                $"history older than {store.HistoryRetentionDays} days deleted automatically.",
                success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtHistoryStatus, $"Could not save the history settings: {ex.Message}", success: false);
            RevertHistorySettings();
        }
    }

    // Puts back the saved choices. Deferred: changing the selection inside its own
    // SelectionChanged handler can leave the ComboBox showing the rejected choice.
    private void RevertHistorySettings() =>
        Dispatcher.BeginInvoke(() =>
        {
            _isLoadingHistorySettings = true;
            try
            {
                ShowHistorySettings(_savedIntervalMinutes, _savedRetentionDays);
            }
            finally
            {
                _isLoadingHistorySettings = false;
            }
        });

    private void ShowQueryText(QueryTextStorage storage)
    {
        _savedQueryText = storage;
        CmbQueryText.SelectedItem = CmbQueryText.Items.OfType<ComboBoxItem>().First(i => (QueryTextStorage)i.Tag == storage);
    }

    // From the texts read next, here and in the collector service when it watches one of these
    // servers (MainWindow passes it on); what's already saved stays until Clear stored query text.
    private void QueryText_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingHistorySettings || CmbQueryText.SelectedItem is not ComboBoxItem { Tag: QueryTextStorage storage }
            || storage == _savedQueryText)
            return;

        try
        {
            _context.Service.SetQueryTextStorage(storage, ActivitySources.Settings);
            _context.Monitoring.ApplyHistorySettings(_context.Service.Load());
            _savedQueryText = storage;
            SettingsContext.ShowStatus(TxtHistoryStatus, storage switch
            {
                QueryTextStorage.Full            => "Saved: query text read from now on is kept in full.",
                QueryTextStorage.WithoutLiterals => "Saved: query text read from now on is kept with its values replaced by ?.",
                _                                => "Saved: no query text is kept from now on, only each query's hash.",
            } + " Text already saved stays until you clear it.", success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtHistoryStatus, $"Could not save how query text is kept: {ex.Message}", success: false);
            Dispatcher.BeginInvoke(() =>
            {
                _isLoadingHistorySettings = true;
                try { ShowQueryText(_savedQueryText); }
                finally { _isLoadingHistorySettings = false; }
            });
        }
    }

    /// <summary>Clear stored query text… (#175): bound by the button, as the MVVM rule asks of a new one.</summary>
    public ICommand ClearQueryText { get; }

    internal async Task ClearQueryTextAsync()
    {
        var confirm = _context.Ask(
            "Delete the text of every query saved in the monitoring history, for every connection?\n\n" +
            "CPU, reads and executions are kept, by each query's hash, and monitoring carries on: text is saved again as " +
            "Store query text in history says. The collector service on this PC clears its history's too. " +
            "The activity log records it. This cannot be undone.",
            "Clear Stored Query Text", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        BtnClearQueryText.IsEnabled = false;
        SettingsContext.ShowStatus(TxtClearQueryTextStatus, "Clearing query text…", success: null);
        try
        {
            await _context.Monitoring.ClearQueryTextAsync(ActivitySources.Settings);
            var service = false;
            string? serviceFailed = null;
            if (_context.Collectors is { } collectors)
            {
                try { service = await collectors.RequestQueryTextClearAsync(); }
                catch (Exception ex) { serviceFailed = ex.Message; }
            }
            SettingsContext.ShowStatus(TxtClearQueryTextStatus,
                serviceFailed is not null ? $"Query text cleared here, but the collector service couldn't be asked to clear its own: {serviceFailed}"
                : service                 ? "Query text cleared. The collector service clears its own within a few seconds, or when it next starts."
                :                           "Query text cleared.",
                success: serviceFailed is null);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtClearQueryTextStatus, $"Could not clear the query text: {ex.Message}", success: false);
        }
        finally
        {
            BtnClearQueryText.IsEnabled = true;
            UpdateHistorySize();
        }
    }

    private async void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirm = _context.Ask(
            "Delete all monitoring history, for every connection?\n\n" +
            "Monitoring carries on, and new history is saved from the next sample. The activity log is kept, " +
            "and records that history was cleared. This cannot be undone.",
            "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        BtnClearHistory.IsEnabled = false;
        SettingsContext.ShowStatus(TxtClearStatus, "Clearing history…", success: null);
        try
        {
            await _context.Monitoring.ClearHistoryAsync(ActivitySources.Settings);
            SettingsContext.ShowStatus(TxtClearStatus, "History cleared. The activity log is kept.", success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtClearStatus, $"Could not clear the history: {ex.Message}", success: false);
        }
        finally
        {
            BtnClearHistory.IsEnabled = true;
            UpdateHistorySize();
        }
    }

    private void UpdateHistorySize()
    {
        var path  = _context.Monitoring.HistoryPath;
        var bytes = HistoryStore.SizeOnDisk(path);
        TxtHistorySize.Text = bytes == 0
            ? "No history saved yet."
            : $"Size on disk: {HistorySettings.FormatSize(bytes)}";
        TxtHistoryPath.Text = path;
    }

    private static string MinutesText(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";
}
