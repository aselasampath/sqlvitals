using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Active and past alerts (#34): health indicators that stayed past a threshold, read from the
/// monitoring history, with the ones active right now taken from the running sessions so they
/// show straight away. Filtered by connection, status and period.
/// </summary>
public partial class AlertsPage : Page, IRefreshable
{
    private enum StatusFilter { All, Active, Past }

    private sealed record Period(string Label, TimeSpan? Span);

    private static readonly IReadOnlyList<Period> Periods =
    [
        new("Last 24 hours", TimeSpan.FromDays(1)),
        new("Last 7 days",   TimeSpan.FromDays(7)),
        new("Last 30 days",  TimeSpan.FromDays(30)),
        new("All history",   null),
    ];

    private readonly MonitoringManager         _monitoring;
    private readonly ConnectionSettingsService _settings;

    // A change is queued to the history writer, which saves it within moments; reading straight
    // away could miss it, so the list is re-read shortly after.
    private readonly DispatcherTimer _reloadSoon = new() { Interval = TimeSpan.FromSeconds(1) };

    // Keeps the durations of active alerts moving while nothing else changes.
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    // Set once the choices are filled in, so filling them doesn't start a load.
    private readonly bool _ready;

    // Bumped per load; a slower, earlier load's result is dropped.
    private int _loadVersion;

    public AlertsPage(MonitoringManager monitoring, ConnectionSettingsService settings)
    {
        _monitoring = monitoring;
        _settings   = settings;
        InitializeComponent();

        CmbConnection.Items.Add(new ComboBoxItem { Content = "All connections", Tag = null });
        foreach (var conn in settings.Load().Connections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            CmbConnection.Items.Add(new ComboBoxItem { Content = conn.DisplayName, Tag = conn.Id, ToolTip = conn.Summary });
        CmbConnection.SelectedIndex = 0;

        foreach (var period in Periods)
            CmbPeriod.Items.Add(new ComboBoxItem { Content = period.Label, Tag = period });
        CmbPeriod.SelectedIndex = 1;

        _reloadSoon.Tick += async (_, _) => { _reloadSoon.Stop(); await RefreshQuietlyAsync(); };
        _tick.Tick       += async (_, _) => await RefreshQuietlyAsync();

        Loaded += (_, _) =>
        {
            _monitoring.AlertsChanged += OnAlertsChanged;
            _tick.Start();
        };
        Unloaded += (_, _) =>
        {
            _monitoring.AlertsChanged -= OnAlertsChanged;
            _tick.Stop();
            _reloadSoon.Stop();
        };

        _ready = true;
    }

    private Guid? SelectedConnection => (CmbConnection.SelectedItem as ComboBoxItem)?.Tag as Guid?;

    private StatusFilter SelectedStatus => (StatusFilter)Math.Max(0, CmbStatus.SelectedIndex);

    private Period SelectedPeriod => (Period)((ComboBoxItem)CmbPeriod.SelectedItem).Tag;

    public async Task RefreshAsync()
    {
        var version      = ++_loadVersion;
        var connectionId = SelectedConnection;
        var period       = SelectedPeriod;
        var status       = SelectedStatus;
        var nowUtc       = DateTime.UtcNow;
        var reader       = _monitoring.HistoryReader;

        // A minute ahead, so an alert that started on a sample just taken is in range whatever
        // the rounding of the stored times.
        var fromUtc = period.Span is { } span ? nowUtc - span : DateTime.UnixEpoch;
        var toUtc   = nowUtc.AddMinutes(1);

        IReadOnlyList<StoredAlert> stored;
        string? readProblem = null;
        try
        {
            // History reads open the file, so they stay off the UI thread like the trend pages' do.
            stored = await Task.Run(() => reader.ReadAlerts(connectionId, fromUtc, toUtc));
        }
        catch (HistorySchemaTooNewException)
        {
            stored      = [];
            readProblem = "The history file is from a newer SqlVitals, so only the alerts active now are listed.";
        }
        if (version != _loadVersion)
            return;

        var store = _settings.Load();
        var names = store.Connections.ToDictionary(c => c.Id, c => c.DisplayName);

        // The running sessions know the active alerts best: one may not be saved yet, or may
        // have changed since. An alert of a deleted connection keeps the name it was saved with.
        var byId = stored.ToDictionary(s => s.Alert.Id, s => s.Alert);
        foreach (var live in _monitoring.ActiveAlerts.Where(a => connectionId is null || a.ConnectionId == connectionId))
            byId[live.Id] = live;
        var storedNames = stored.GroupBy(s => s.Alert.ConnectionId).ToDictionary(g => g.Key, g => g.First().ConnectionName);

        string NameOf(Guid id) =>
            names.TryGetValue(id, out var name) ? name
            : storedNames.TryGetValue(id, out var saved) && saved.Length > 0 ? saved
            : "(deleted connection)";

        var all  = byId.Values.ToList();
        var rows = all
            .Where(a => status switch
            {
                StatusFilter.Active => a.IsActive,
                StatusFilter.Past   => !a.IsActive,
                _                   => true,
            })
            .OrderByDescending(a => a.IsActive)
            .ThenByDescending(a => a.StartedUtc)
            .Select(a => new AlertRow(a, NameOf(a.ConnectionId), nowUtc))
            .ToList();

        DataGridRefresh.SetItemsSource(AlertsGrid, rows);

        var active = all.Where(a => a.IsActive).ToList();
        TxtSummary.Text = Summary(active, all.Count, period);
        TxtCount.Text   = rows.Count switch
        {
            0     => "Alerts",
            1     => "1 alert",
            var n => $"{n:N0} alerts",
        };
        TxtStatus.Text = string.Join(" ", new[]
        {
            readProblem,
            NotMonitoredNote(connectionId),
            $"An alert starts after {SamplesText(store.AlertSamples)} in a row past a health threshold and ends after as many back to normal (Settings → Alerts). Times are on this PC's clock.",
        }.Where(t => !string.IsNullOrEmpty(t)));

        TxtNoData.Text       = EmptyText(status, period, connectionId is null);
        TxtNoData.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Summary(IReadOnlyList<Alert> active, int total, Period period)
    {
        var culture  = CultureInfo.CurrentCulture;
        var inPeriod = period.Span is null ? "in the history kept" : $"in the {period.Label.ToLower(culture)}";
        var critical = active.Count(a => a.Severity == HealthLevel.Critical);

        var now = active.Count switch
        {
            0 => "No active alerts",
            1 => $"1 active alert ({(critical == 1 ? "critical" : "warning")})",
            var n => $"{n} active alerts ({critical} critical, {n - critical} warning)",
        };
        return string.Format(culture, "{0} · {1:N0} alert{2} {3}", now, total, total == 1 ? "" : "s", inPeriod);
    }

    // Why a connection picked in the filter raises no new alerts, when it doesn't.
    private string? NotMonitoredNote(Guid? connectionId) =>
        connectionId is { } id && _monitoring.Get(id) is not { IsRunning: true }
            ? (_monitoring.Get(id) is null
                  ? $"No new alerts are raised for this connection. {_monitoring.NotMonitoredReason(id)}"
                  : "No new alerts are raised for this connection while its monitoring is stopped (Live Metrics → Start).")
            : null;

    private static string EmptyText(StatusFilter status, Period period, bool allConnections)
    {
        var culture = CultureInfo.CurrentCulture;
        var when    = period.Span is null ? "in the history kept" : $"in the {period.Label.ToLower(culture)}";
        var who     = allConnections ? "" : " for this connection";
        return status switch
        {
            StatusFilter.Active => $"No active alerts{who}.",
            StatusFilter.Past   => $"No past alerts{who} {when}.",
            _ => $"No alerts{who} {when}. Every sample of a monitored connection is checked against its health thresholds " +
                 "(Settings → Health Thresholds); an indicator that stays past one is listed here.",
        };
    }

    private static string SamplesText(int samples) => samples == 1 ? "1 sample" : $"{samples} samples";

    // ── Live updates ──────────────────────────────────────────────────────────

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes)
    {
        _reloadSoon.Stop();
        _reloadSoon.Start();
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && IsLoaded)
            await RefreshQuietlyAsync();
    }

    // The sidebar Refresh reports errors through MainWindow; a load started here reports its own.
    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Alerts: load failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    /// <summary>One alert as the grid shows it; times on this PC's clock.</summary>
    public sealed class AlertRow(Alert alert, string connectionName, DateTime nowUtc)
    {
        public Alert     Alert          { get; } = alert;
        public string    ConnectionName { get; } = connectionName;
        public bool      IsActive       => Alert.IsActive;
        public bool      IsCritical     => Alert.Severity == HealthLevel.Critical;
        public string    StatusText     => IsActive ? "● Active" : "Ended";
        public string    SeverityText   => AlertText.Severity(Alert.Severity);
        public int       SeverityRank   => (int)Alert.Severity;
        public string    IndicatorName  => HealthThresholds.Info(Alert.Indicator).Name;
        public DateTime  Started        => Alert.StartedUtc.ToLocalTime();
        public DateTime? Ended          => Alert.EndedUtc?.ToLocalTime();
        public double    DurationSeconds => Alert.Duration(nowUtc).TotalSeconds;
        public string    DurationText   => AlertText.Duration(Alert.Duration(nowUtc));
        public double    Value          => Alert.Value;
        public string    ValueText      => AlertText.Value(Alert.Indicator, Alert.Value);
        public string    ThresholdText  => AlertText.Threshold(Alert.Indicator, Alert.Threshold);
        public string    EndReasonText  => AlertText.EndReason(Alert.EndReason);
        public string    Detail         => Alert.Detail;
    }
}
