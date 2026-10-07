using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Monitoring &amp; history (#131): Start monitoring automatically, Command timeout,
/// the history's snapshot interval and retention, and Clear history. Each saves as it changes.
/// </summary>
internal partial class MonitoringCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;

    // The history file grows with every sample and shrinks after a purge, which the writer
    // runs on its own schedule, so the size shown is re-read while the category is open.
    private readonly DispatcherTimer _historySizeTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Set while the history choices are filled in or put back in code, so they aren't saved.
    private bool _isLoadingHistorySettings;
    private int  _savedIntervalMinutes;
    private int  _savedRetentionDays;
    private int  _savedTimeout;

    public MonitoringCategory(SettingsContext context)
    {
        _context = context;
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Monitoring).Subtitle;

        var store = _context.Service.Load();
        ChkStartMonitoringAutomatically.IsChecked = store.StartMonitoringAutomatically;
        _savedTimeout   = store.CommandTimeoutSeconds;
        TxtTimeout.Text = _savedTimeout.ToString(CultureInfo.CurrentCulture);
        LoadHistorySettings(store);
        UpdateHistorySize();

        _historySizeTimer.Tick += (_, _) => UpdateHistorySize();
        Loaded   += (_, _) => { UpdateHistorySize(); _historySizeTimer.Start(); };
        Unloaded += (_, _) => _historySizeTimer.Stop();
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

    private async void TxtTimeout_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => await SaveTimeoutAsync();

    private async void TxtTimeout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await SaveTimeoutAsync();
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
