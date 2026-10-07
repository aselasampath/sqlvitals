using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Active and past alerts (#34): health indicators that stayed past a threshold, read from the
/// monitoring history, with the ones active right now taken from the running sessions so they
/// show straight away. Filtered by connection, status and period. On the page template (#150): the
/// verdict names the worst active alert, and a row's details are in the drawer.
/// </summary>
public partial class AlertsPage : Page, IRefreshable, IPageContext, IDetailsSource
{
    // ── The details drawer (#150): the selected alert ─────────────────────────
    private const string SelectHint = "Select an alert to see its figures, its times and what the health dot said.";

    private readonly RowDrawer _drawer = new("Alert details", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowAlert(object row, bool open)
    {
        if (row is AlertRow alert)
            _drawer.Show($"{alert.IndicatorName} · {alert.ConnectionName}",
                         RowDetails.Rows(new NamedAlert(alert.Alert, alert.ConnectionName), DateTime.UtcNow), open);
    }

    // The page template's verdict (#150): the active alerts, the worst first, and whether alerts are raised.
    internal AlertsViewModel ViewModel { get; }

    private const int ActiveOnlyIndex = 1;

    // Alerts as they start and end, from every monitored connection (#140).
    public PageKind Kind => PageKind.Live;

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
    private readonly INavigator                _navigator;

    // A change is queued to the history writer, which saves it within moments; reading straight
    // away could miss it, so the list is re-read shortly after.
    private readonly DispatcherTimer _reloadSoon = new() { Interval = TimeSpan.FromSeconds(1) };

    // Keeps the durations of active alerts moving while nothing else changes.
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };

    // Set once the choices are filled in, so filling them doesn't start a load.
    private readonly bool _ready;

    // Bumped per load; a slower, earlier load's result is dropped.
    private int _loadVersion;

    /// <param name="connectionId">The connection to filter on at first, e.g. the one a notification was about; null for all.</param>
    /// <param name="settings">The connections, and where the verdict's fold is remembered.</param>
    /// <param name="navigator">Where Investigate goes; the app's (#182) when null.</param>
    public AlertsPage(MonitoringManager monitoring, ConnectionSettingsService settings, Guid? connectionId = null,
                      INavigator? navigator = null)
    {
        _monitoring = monitoring;
        _settings   = settings;
        _navigator  = navigator ?? CompositionRoot.Get<INavigator>();
        InitializeComponent();

        ViewModel = new AlertsViewModel(_navigator, activeOnly: () => CmbStatus.SelectedIndex = ActiveOnlyIndex);
        ViewModel.Attach(Shell);
        Shell.Folds = settings;
        DrawerParts.FollowSelection(AlertsGrid, ShowAlert, _drawer.Clear);

        CmbConnection.Items.Add(new ComboBoxItem { Content = "All connections", Tag = null });
        foreach (var conn in settings.Load().Connections.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            CmbConnection.Items.Add(new ComboBoxItem { Content = conn.DisplayName, Tag = conn.Id, ToolTip = conn.Summary });
        CmbConnection.SelectedItem = CmbConnection.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is Guid id && id == connectionId)
                                     ?? CmbConnection.Items[0];

        foreach (var period in Periods)
            CmbPeriod.Items.Add(new ComboBoxItem { Content = period.Label, Tag = period });
        CmbPeriod.SelectedIndex = 1;

        // Investigate (#145): the link in the Evidence column, a double-click on the row, or Enter.
        // The grid handles the mouse itself, so the link needs the clicks it has handled too.
        AlertsGrid.AddHandler(MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler(AlertsGrid_MouseLeftButtonUp), handledEventsToo: true);
        AlertsGrid.MouseDoubleClick += AlertsGrid_MouseDoubleClick;
        AlertsGrid.PreviewKeyDown   += AlertsGrid_PreviewKeyDown;

        _reloadSoon.Tick += async (_, _) => { _reloadSoon.Stop(); await RefreshQuietlyAsync(); };
        _tick.Tick       += async (_, _) => await RefreshQuietlyAsync();

        Loaded += (_, _) =>
        {
            _monitoring.AlertsChanged     += OnAlertsChanged;
            _monitoring.MonitoringChanged += OnMonitoringChanged;
            _tick.Start();
        };
        Unloaded += (_, _) =>
        {
            _monitoring.AlertsChanged     -= OnAlertsChanged;
            _monitoring.MonitoringChanged -= OnMonitoringChanged;
            _tick.Stop();
            _reloadSoon.Stop();
        };

        _ready = true;
    }

    // An alert's Investigate: its evidence, filtered to the problem and set to its time (#145).
    private Task InvestigateAsync(Alert alert) => _navigator.InvestigateAsync(alert, ActivitySources.AlertsPage);

    private async void AlertsGrid_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock { DataContext: AlertRow row } text && CellOf(text)?.Column == InvestigateColumn)
            await InvestigateAsync(row.Alert);
    }

    private async void AlertsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only a row: not the header or the scroll bar.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AlertsGrid, source) is DataGridRow { Item: AlertRow row })
            await InvestigateAsync(row.Alert);
    }

    private async void AlertsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || AlertsGrid.SelectedItem is not AlertRow row)
            return;
        e.Handled = true;
        await InvestigateAsync(row.Alert);
    }

    private static DataGridCell? CellOf(DependencyObject? element)
    {
        while (element is not null and not DataGridCell)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        return element as DataGridCell;
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
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ViewModel.ShowError("the alert history", ex);
            throw;
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

        // Only the connections listed: all of them, or the one picked.
        var notMonitored = _monitoring.NotMonitoredNames()
            .Where(n => connectionId is not { } id || n == NameOf(id))
            .ToList();
        ViewModel.Show(all.Select(a => new NamedAlert(a, NameOf(a.ConnectionId))).ToList(), InPeriod(period), nowUtc,
                       _monitoring.MonitoredCount, notMonitored, activeOnly: status == StatusFilter.Active);
        if (AlertsGrid.SelectedItem is AlertRow selected)
            ShowAlert(selected, open: false);
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
            $"An alert starts after {SamplesText(store.AlertSamples)} in a row past a health threshold and ends after as many back to normal (Settings → Alerts & chat). Times are on this PC's clock.",
        }.Where(t => !string.IsNullOrEmpty(t)));

        TxtNoData.Text       = EmptyText(status, period, connectionId is null);
        TxtNoData.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Monitoring started or stopped: whether alerts are raised changed, which the verdict says.
    private void OnMonitoringChanged()
    {
        _reloadSoon.Stop();
        _reloadSoon.Start();
    }

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
        var culture = CultureInfo.CurrentCulture;
        var when    = period.Span is null ? "in the history kept" : $"in the {period.Label.ToLower(culture)}";
        var who     = allConnections ? "" : " for this connection";
        return status switch
        {
            StatusFilter.Active => $"No active alerts{who}.",
            StatusFilter.Past   => $"No past alerts{who} {when}.",
            _ => $"No alerts{who} {when}. Every sample of a monitored connection is checked against its health thresholds " +
                 "(Settings → Health thresholds); an indicator that stays past one is listed here.",
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

    // The top bar's Refresh reports errors through MainWindow; a load started here reports its own.
    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
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
        public string    InvestigateText => "Investigate →";
        public string    InvestigateToolTip => $"Open {EvidenceRoutes.For(Alert.Indicator).Text} on {ConnectionName}, " +
                                               (IsActive ? "filtered to this alert" : "set to this alert's time");
    }
}
