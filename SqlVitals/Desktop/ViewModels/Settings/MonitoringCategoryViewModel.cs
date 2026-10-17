using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Monitoring &amp; history (#131): Start monitoring automatically, Command timeout,
/// the history's snapshot interval and retention, how query text is kept (#175), and Clear history
/// and Clear stored query text. Each saves as it changes.
/// </summary>
public sealed partial class MonitoringCategoryViewModel : SettingsCategoryViewModel
{
    // The history file grows with every sample and shrinks after a purge, which the writer runs on
    // its own schedule, so the size shown is re-read while the category is open, on the app's
    // refresh clock (#169).
    private static readonly TimeSpan HistorySizeInterval = TimeSpan.FromSeconds(5);

    private readonly SettingsContext _context;
    private IRefreshSchedule? _historySizeTimer;

    private bool _startAutomatically;
    private int  _savedTimeout;
    private int  _intervalMinutes;
    private int  _retentionDays;
    private QueryTextStorage _queryText;

    internal MonitoringCategoryViewModel(SettingsContext context)
    {
        _context = context;
        var store = context.Service.Load();
        _startAutomatically = store.StartMonitoringAutomatically;
        _savedTimeout       = store.CommandTimeoutSeconds;
        TimeoutText         = _savedTimeout.ToString(CultureInfo.CurrentCulture);
        _intervalMinutes    = store.HistoryIntervalMinutes;
        _retentionDays      = store.HistoryRetentionDays;
        _queryText          = store.QueryTextStorage;
        UpdateHistorySize();
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Monitoring).Subtitle;

    public override async Task<bool> TryLeaveAsync()
    {
        // A timeout still being typed is saved on the way out, as leaving the box would.
        await SaveTimeoutAsync();
        return true;
    }

    protected override void OnActivated()
    {
        UpdateHistorySize();
        _historySizeTimer = _context.Scheduler?.Every(HistorySizeInterval, () =>
        {
            UpdateHistorySize();
            return Task.CompletedTask;
        });
    }

    protected override void OnDeactivated()
    {
        _historySizeTimer?.Dispose();
        _historySizeTimer = null;
    }

    // ── Monitoring ────────────────────────────────────────────────────────────

    public bool StartAutomatically => _startAutomatically;

    /// <summary>
    /// The switch. Applies straight away: on starts the active connection and the background ones,
    /// off stops every session (their active alerts end).
    /// </summary>
    [RelayCommand]
    [ReportsItsErrors]
    private async Task ToggleStartAutomaticallyAsync()
    {
        var start = !_startAutomatically;
        try
        {
            _context.Service.SetStartMonitoringAutomatically(start);
        }
        catch (Exception ex)
        {
            MonitoringStatus.Show($"Could not save the setting: {ex.Message}", success: false);
            OnPropertyChanged(nameof(StartAutomatically));
            return;
        }
        _startAutomatically = start;
        OnPropertyChanged(nameof(StartAutomatically));

        try
        {
            await _context.ApplyConnections(false);
            MonitoringStatus.Show(start
                    ? $"Saved: {MonitoredText()} now, and from every start of SqlVitals."
                    : "Saved: monitoring stopped for every connection. Nothing is collected until you press Start.",
                success: true);
        }
        catch (Exception ex)
        {
            // Saved, so the switch stays as it is; it applies at the latest when SqlVitals next starts.
            MonitoringStatus.Show($"Saved, but it couldn't be applied straight away: {ex.Message}", success: false);
        }
    }

    private string MonitoredText() => _context.Monitoring.MonitoredCount switch
    {
        0     => "no connection can be monitored yet (the active one needs its password, or there is none)",
        1     => "1 connection is monitored",
        var n => $"{n} connections are monitored",
    };

    /// <summary>The command timeout as typed; saved when the box is left or Enter is pressed.</summary>
    [ObservableProperty]
    private string _timeoutText = string.Empty;

    /// <summary>Saves the command timeout when it changed and can be used; says why not otherwise.</summary>
    [RelayCommand]
    [ReportsItsErrors]
    internal async Task SaveTimeoutAsync()
    {
        if (!int.TryParse(TimeoutText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var timeout) || timeout <= 0)
        {
            MonitoringStatus.Show($"Command timeout must be a positive whole number of seconds. Still {_savedTimeout} s.", success: false);
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
            MonitoringStatus.Show($"Saved: queries may run for {timeout} s before they're stopped.", success: true);
        }
        catch (Exception ex)
        {
            MonitoringStatus.Show($"Could not save the command timeout: {ex.Message}", success: false);
        }
    }

    public StatusLine MonitoringStatus { get; } = new();

    // ── Monitoring history ────────────────────────────────────────────────────

    public IReadOnlyList<Choice<int>> IntervalChoices { get; } =
        HistorySettings.IntervalChoicesMinutes.Select(m => new Choice<int>(m, MinutesText(m))).ToList();

    public IReadOnlyList<Choice<int>> RetentionChoices { get; } =
        HistorySettings.RetentionChoicesDays.Select(d => new Choice<int>(d, $"{d} days")).ToList();

    public IReadOnlyList<Choice<QueryTextStorage>> QueryTextChoices { get; } =
        QueryTextStorages.All.Select(s => new Choice<QueryTextStorage>(s, QueryTextStorages.Describe(s))).ToList();

    /// <summary>Snapshot every: saved and applied as it's picked.</summary>
    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set => SaveHistorySettings(value, _retentionDays, nameof(IntervalMinutes));
    }

    /// <summary>Keep history for: a shorter time asks first, since older history is deleted straight away.</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => SaveHistorySettings(_intervalMinutes, value, nameof(RetentionDays));
    }

    private void SaveHistorySettings(int minutes, int days, string property)
    {
        if (minutes == _intervalMinutes && days == _retentionDays)
            return;

        if (days < _retentionDays && !_context.Dialogs.Confirm(
                $"Keep monitoring history for {days} days instead of {_retentionDays}?\n\n" +
                $"History older than {days} days is deleted straight away, for every connection. This cannot be undone.",
                "Monitoring History"))
        {
            PutBack(property);
            return;
        }

        try
        {
            _context.Service.SetHistorySettings(minutes, days);
            var store = _context.Service.Load();
            _context.Monitoring.ApplyHistorySettings(store);
            _intervalMinutes = store.HistoryIntervalMinutes;
            _retentionDays   = store.HistoryRetentionDays;
            OnPropertyChanged(property);

            HistoryStatus.Show($"Saved: a snapshot every {MinutesText(store.HistoryIntervalMinutes)}, " +
                               $"history older than {store.HistoryRetentionDays} days deleted automatically.", success: true);
        }
        catch (Exception ex)
        {
            HistoryStatus.Show($"Could not save the history settings: {ex.Message}", success: false);
            PutBack(property);
        }
    }

    /// <summary>How the text of the busiest queries is kept (#175): from the texts read next.</summary>
    public QueryTextStorage QueryText
    {
        get => _queryText;
        set
        {
            if (value == _queryText)
                return;
            try
            {
                // Here, and in the collector service when it watches one of these servers (MainWindow passes it on);
                // what's already saved stays until Clear stored query text.
                _context.Service.SetQueryTextStorage(value, ActivitySources.Settings);
                _context.Monitoring.ApplyHistorySettings(_context.Service.Load());
                _queryText = value;
                OnPropertyChanged();
                HistoryStatus.Show(value switch
                {
                    QueryTextStorage.Full            => "Saved: query text read from now on is kept in full.",
                    QueryTextStorage.WithoutLiterals => "Saved: query text read from now on is kept with its values replaced by ?.",
                    _                                => "Saved: no query text is kept from now on, only each query's hash.",
                } + " Text already saved stays until you clear it.", success: true);
            }
            catch (Exception ex)
            {
                HistoryStatus.Show($"Could not save how query text is kept: {ex.Message}", success: false);
                PutBack(nameof(QueryText));
            }
        }
    }

    // Later: the box picked it, and only shows the saved choice again once its own change is done.
    private void PutBack(string property) => _context.Ui.Post(() => OnPropertyChanged(property));

    public StatusLine HistoryStatus { get; } = new();

    /// <summary>"Size on disk: 12.3 MB", or "No history saved yet."</summary>
    [ObservableProperty]
    private string _historySize = string.Empty;

    [ObservableProperty]
    private string _historyPath = string.Empty;

    private void UpdateHistorySize()
    {
        var path  = _context.Monitoring.HistoryPath;
        var bytes = HistoryStore.SizeOnDisk(path);
        HistorySize = bytes == 0 ? "No history saved yet." : $"Size on disk: {HistorySettings.FormatSize(bytes)}";
        HistoryPath = path;
    }

    private static string MinutesText(int minutes) => minutes == 1 ? "1 minute" : $"{minutes} minutes";

    // ── Clear ─────────────────────────────────────────────────────────────────

    public StatusLine ClearStatus { get; } = new();

    public StatusLine ClearQueryTextStatus { get; } = new();

    /// <summary>Clear history…: asks first. Its button waits while it clears.</summary>
    [RelayCommand]
    [ReportsItsErrors]
    private async Task ClearHistoryAsync()
    {
        if (!_context.Dialogs.Confirm(
                "Delete all monitoring history, for every connection?\n\n" +
                "Monitoring carries on, and new history is saved from the next sample. The activity log is kept, " +
                "and records that history was cleared. This cannot be undone.",
                "Clear History"))
            return;

        ClearStatus.Show("Clearing history…", success: null);
        try
        {
            await _context.Monitoring.ClearHistoryAsync(ActivitySources.Settings);
            ClearStatus.Show("History cleared. The activity log is kept.", success: true);
        }
        catch (Exception ex)
        {
            ClearStatus.Show($"Could not clear the history: {ex.Message}", success: false);
        }
        finally
        {
            UpdateHistorySize();
        }
    }

    /// <summary>Clear stored query text… (#175): asks first, then clears here and asks the collector service to.</summary>
    [RelayCommand]
    [ReportsItsErrors]
    internal async Task ClearQueryTextAsync()
    {
        if (!_context.Dialogs.Confirm(
                "Delete the text of every query saved in the monitoring history, for every connection?\n\n" +
                "CPU, reads and executions are kept, by each query's hash, and monitoring carries on: text is saved again as " +
                "Store query text in history says. The collector service on this PC clears its history's too. " +
                "The activity log records it. This cannot be undone.",
                "Clear Stored Query Text"))
            return;

        ClearQueryTextStatus.Show("Clearing query text…", success: null);
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
            ClearQueryTextStatus.Show(
                serviceFailed is not null ? $"Query text cleared here, but the collector service couldn't be asked to clear its own: {serviceFailed}"
                : service                 ? "Query text cleared. The collector service clears its own within a few seconds, or when it next starts."
                :                           "Query text cleared.",
                success: serviceFailed is null);
        }
        catch (Exception ex)
        {
            ClearQueryTextStatus.Show($"Could not clear the query text: {ex.Message}", success: false);
        }
        finally
        {
            UpdateHistorySize();
        }
    }
}
