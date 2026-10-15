using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Active and past alerts (#34, #187): health indicators that stayed past a threshold, read from
/// the monitoring history, with the ones active right now taken from the running sessions so they
/// show straight away. Filtered by connection, status and period. Read again shortly after an
/// alert starts or ends, and every 30 s to keep the durations moving. The verdict (#150) names the
/// worst active alert, and whether alerts are raised at all.
/// </summary>
public sealed partial class AlertsViewModel : PageViewModel
{
    /// <summary>The status filter the verdict's Active only picks.</summary>
    internal const int ActiveOnlyIndex = 1;

    private enum StatusFilter { All, Active, Past }

    private sealed record Period(string Label, TimeSpan? Span);

    private static readonly IReadOnlyList<Period> Periods =
    [
        new("Last 24 hours", TimeSpan.FromDays(1)),
        new("Last 7 days",   TimeSpan.FromDays(7)),
        new("Last 30 days",  TimeSpan.FromDays(30)),
        new("All history",   null),
    ];

    /// <summary>A change is queued to the history writer, which saves it within moments; reading straight away could miss it.</summary>
    internal static readonly TimeSpan ReloadDelay = TimeSpan.FromSeconds(1);

    /// <summary>Keeps the durations of active alerts moving while nothing else changes.</summary>
    internal static readonly TimeSpan DurationsInterval = TimeSpan.FromSeconds(30);

    private readonly IAlertFeed                _monitoring;
    private readonly ConnectionSettingsService _settings;
    private readonly INavigator                _navigator;
    private readonly TimeProvider              _time;

    // A read has started: a filter changed after it reads again.
    private bool _read;

    // The worst active alert, for the verdict's Investigate.
    private Alert? _worst;

    /// <param name="connectionId">The connection to filter on at first, e.g. the one a notification was about; null for all.</param>
    /// <param name="monitoring">The alerts saved and active now: the app's <see cref="MonitoringManager"/>.</param>
    public AlertsViewModel(IAlertFeed monitoring, ConnectionSettingsService settings, INavigator navigator,
                           Guid? connectionId = null, TimeProvider? time = null)
    {
        _monitoring = monitoring;
        _settings   = settings;
        _navigator  = navigator;
        _time       = time ?? TimeProvider.System;

        Connections = [new("All connections", null, null), ..settings.Load().Connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ConnectionChoice(c.DisplayName, c.Id, c.Summary))];
        _selectedConnection = Connections.FirstOrDefault(c => c.Id is { } id && id == connectionId) ?? Connections[0];
        RefreshInterval = DurationsInterval;
    }

    // ── What to list ──────────────────────────────────────────────────────

    /// <summary>One of the connection filter's choices: all connections, or one.</summary>
    public sealed record ConnectionChoice(string Name, Guid? Id, string? Summary);

    /// <summary>"All connections", then each connection by name.</summary>
    public IReadOnlyList<ConnectionChoice> Connections { get; }

    [ObservableProperty]
    private ConnectionChoice _selectedConnection;

    /// <summary>"Active and past", "Active only", "Past only".</summary>
    public IReadOnlyList<string> StatusNames { get; } = ["Active and past", "Active only", "Past only"];

    [ObservableProperty]
    private int _statusIndex;

    public IReadOnlyList<string> PeriodNames { get; } = Periods.Select(p => p.Label).ToList();

    [ObservableProperty]
    private int _periodIndex = 1;

    // Other filters: read again, after a read in progress, which used the old ones (#169).
    // The combo box can push null (its items replaced, a choice not in them): that's all connections.
    partial void OnSelectedConnectionChanged(ConnectionChoice value)
    {
        if (value is null)
            SelectedConnection = Connections[0];
        else
            Reread();
    }
    partial void OnStatusIndexChanged(int value) => Reread();
    partial void OnPeriodIndexChanged(int value) => Reread();

    private void Reread()
    {
        if (_read)
            _ = RereadAsync();
    }

    private StatusFilter SelectedStatus => (StatusFilter)Math.Clamp(StatusIndex, 0, 2);

    private Period SelectedPeriod => Periods[Math.Clamp(PeriodIndex, 0, Periods.Count - 1)];

    // ── The alerts ────────────────────────────────────────────────────────

    /// <summary>Active first, then the newest.</summary>
    public GridRows<AlertRow> Alerts { get; } = [];

    /// <summary>The alert in the drawer; kept across a read by its id.</summary>
    [ObservableProperty]
    private AlertRow? _selectedAlert;

    /// <summary>"Alerts", "1 alert", "12 alerts".</summary>
    [ObservableProperty]
    private string _countText = "Alerts";

    /// <summary>When an alert starts and ends, and why a connection picked raises none.</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    /// <summary>Why no alert is listed.</summary>
    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    // The monitoring's changes are followed while the page is shown.
    protected override void OnShowing()
    {
        OnLeaving();   // once, however often it is shown
        _monitoring.AlertsChanged     += OnAlertsChanged;
        _monitoring.MonitoringChanged += OnMonitoringChanged;
    }

    protected override void OnLeaving()
    {
        _monitoring.AlertsChanged     -= OnAlertsChanged;
        _monitoring.MonitoringChanged -= OnMonitoringChanged;
    }

    private void OnAlertsChanged(Guid connectionId, IReadOnlyList<AlertChange> changes) => RefreshSoon(ReloadDelay);

    // Monitoring started or stopped: whether alerts are raised changed, which the verdict says.
    private void OnMonitoringChanged() => RefreshSoon(ReloadDelay);

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        _read = true;
        var connectionId = SelectedConnection.Id;
        var period       = SelectedPeriod;
        var status       = SelectedStatus;
        var nowUtc       = _time.GetUtcNow().UtcDateTime;
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
            stored = await Task.Run(() => reader.ReadAlerts(connectionId, fromUtc, toUtc), cancel);
        }
        catch (HistorySchemaTooNewException)
        {
            stored      = [];
            readProblem = "The history file is from a newer SqlVitals, so only the alerts active now are listed.";
        }
        cancel.ThrowIfCancellationRequested();

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

        // The grid clears its selection as the rows are replaced: the same alert is selected again.
        var selected = SelectedAlert?.Alert.Id;
        Alerts.Replace(rows);
        SelectedAlert = rows.FirstOrDefault(r => r.Alert.Id == selected);

        // Only the connections listed: all of them, or the one picked.
        var notMonitored = _monitoring.NotMonitoredNames()
            .Where(n => connectionId is not { } id || n == NameOf(id))
            .ToList();
        var named = all.Select(a => new NamedAlert(a, NameOf(a.ConnectionId))).ToList();
        Verdict = AlertsVerdict.From(named, InPeriod(period), nowUtc, _monitoring.MonitoredCount, notMonitored,
                                     activeOnly: status == StatusFilter.Active);
        _worst  = AlertsVerdict.Worst(named)?.Alert;

        CountText = rows.Count switch
        {
            0     => "Alerts",
            1     => "1 alert",
            var n => $"{n:N0} alerts",
        };
        Note = string.Join(" ", new[]
        {
            readProblem,
            NotMonitoredNote(connectionId),
            $"An alert starts after {SamplesText(store.AlertSamples)} in a row past a health threshold and ends after as many back to normal (Settings → Alerts & chat). Times are on this PC's clock.",
        }.Where(t => !string.IsNullOrEmpty(t)));

        NoDataText  = EmptyText(status, period, connectionId is null);
        ShowsNoData = rows.Count == 0;
    }

    protected override void ShowLoadError(Exception exception) => ShowError("the alert history", exception);

    // "in the last 7 days", "in the history kept".
    private static string InPeriod(Period period) =>
        period.Span is null ? "in the history kept" : $"in the {period.Label.ToLower(CultureInfo.CurrentCulture)}";

    // Why a connection picked in the filter raises no new alerts, when it doesn't.
    private string? NotMonitoredNote(Guid? connectionId) =>
        connectionId is { } id && !_monitoring.IsMonitoring(id)
            ? $"No new alerts are raised for this connection. {_monitoring.NotMonitoredReason(id)}"
            : null;

    private static string EmptyText(StatusFilter status, Period period, bool allConnections)
    {
        var who = allConnections ? "" : " for this connection";
        return status switch
        {
            StatusFilter.Active => $"No active alerts{who}.",
            StatusFilter.Past   => $"No past alerts{who} {InPeriod(period)}.",
            _ => $"No alerts{who} {InPeriod(period)}. Every sample of a monitored connection is checked against its health thresholds " +
                 "(Settings → Health thresholds); an indicator that stays past one is listed here.",
        };
    }

    private static string SamplesText(int samples) => samples == 1 ? "1 sample" : $"{samples} samples";

    // ── Investigate (#145) ────────────────────────────────────────────────

    /// <summary>An alert's evidence, filtered to the problem and set to its time: its link, a double-click on its row, or Enter.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Investigate(AlertRow? row) =>
        row is null ? Task.CompletedTask : ReportErrorsOf(_navigator.InvestigateAsync(row.Alert, ActivitySources.AlertsPage));

    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case AlertsVerdict.Investigate when _worst is { } alert: return _navigator.InvestigateAsync(alert, ActivitySources.AlertsPage);
            case AlertsVerdict.ActiveOnly:                           StatusIndex = ActiveOnlyIndex; break;
        }
        return Task.CompletedTask;
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
        [SuppressMessage("Performance", "CA1822", Justification = "Bound in XAML; a binding needs an instance member")]
        public string    InvestigateText => "Investigate →";
        public string    InvestigateToolTip => $"Open {EvidenceRoutes.For(Alert.Indicator).Text} on {ConnectionName}, " +
                                               (IsActive ? "filtered to this alert" : "set to this alert's time");
    }
}
