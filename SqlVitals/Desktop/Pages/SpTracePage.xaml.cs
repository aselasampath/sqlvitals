using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Live stored-procedure trace. Streams individual calls from an Extended Events session
/// when the login can create one, and falls back to per-procedure DMV aggregates when it
/// cannot — see <see cref="ISpTraceRepository"/>.
/// </summary>
public partial class SpTracePage : Page, IRefreshable
{
    /// <summary>Cap on retained call rows. Oldest are dropped first.</summary>
    private const int MaxRowsRetained = 5000;

    private static readonly int[] Intervals = [1, 2, 3, 5, 10, 15, 30];

    private readonly IWaitStatsRepository _repo;
    private readonly DispatcherTimer      _timer = new();

    // Full unfiltered history; CallsGrid is bound to the filtered projection.
    private readonly List<SpTraceEvent>             _allCalls  = [];
    private readonly ObservableCollection<SpTraceEvent> _visibleCalls = [];

    private TraceSessionStatus _status = TraceSessionStatus.Stopped("SqlVitals_SpTrace");
    private int  _intervalSec  = 2;
    private int  _remainingSec;
    private bool _polling;

    /// <summary>First item of every filter combo; selecting it disables that filter.</summary>
    private const string AllOption = "(All)";

    // False during InitializeComponent (XAML-set IsChecked raises Checked before the grid
    // exists) and while combo items are being rebuilt.
    private bool _filtersReady;

    public SpTracePage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();

        foreach (var combo in FilterCombos)
        {
            combo.Items.Add(AllOption);
            combo.SelectedIndex = 0;
        }
        _filtersReady = true;
        ApplyFilter();

        var darkItemStyle = (Style)FindResource("DarkComboItem");
        foreach (var sec in Intervals)
            CmbInterval.Items.Add(new ComboBoxItem { Content = $"{sec}s", Tag = sec, Style = darkItemStyle });
        CmbInterval.SelectedIndex = 1;   // 2s — matches MAX_DISPATCH_LATENCY

        CallsGrid.ItemsSource = _visibleCalls;

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Timer_Tick;
    }

    /// <summary>
    /// Called by MainWindow on navigate and on manual Refresh. Deliberately does not start
    /// a trace — starting one runs a server-side event session, which must stay an explicit
    /// user action. When a trace is already running this pulls one poll's worth of data.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_status.IsRunning)
            await PollOnceAsync();
        else
            await LoadAggregatesAsync();
    }

    // ── Trace lifecycle ──────────────────────────────────────────────────

    private async void BtnTrace_Checked(object sender, RoutedEventArgs e)
    {
        BtnTrace.Content     = "■ Stop Trace";
        BtnTrace.Foreground  = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        BtnTrace.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        BtnTrace.Background  = new SolidColorBrush(Color.FromArgb(0x1A, 0xF5, 0x9E, 0x0B));
        BtnTrace.IsEnabled   = false;

        try
        {
            var options = SpTraceOptions.Default with { MinDurationMs = ReadMinDurationMs() };
            _status = await _repo.StartTraceAsync(options);
            ApplyStatus();

            // DMV mode has no per-call data, so send the user to the view that does have it.
            if (_status.Mode == TraceMode.DmvFallback)
                TraceTabs.SelectedItem = TabByProcedure;

            StartTimer();
        }
        catch (Exception ex)
        {
            ShowError($"Could not start the trace: {ex.Message}");
            BtnTrace.IsChecked = false;   // raises Unchecked, which resets the button chrome
        }
        finally
        {
            BtnTrace.IsEnabled = true;
        }
    }

    private async void BtnTrace_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnTrace.Content     = "● Start Trace";
        BtnTrace.Foreground  = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        BtnTrace.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        BtnTrace.Background  = new SolidColorBrush(Color.FromArgb(0x1A, 0x22, 0xC5, 0x5E));

        StopTimer();

        try
        {
            _status = await _repo.StopTraceAsync();
            ApplyStatus();
        }
        catch (Exception ex)
        {
            ShowError($"The trace stopped, but the event session may not have been dropped: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops polling without waiting on the server. Called by MainWindow before it blocks
    /// on dropping the event session: a poll in flight holds the repository's gate for a
    /// full round trip, and on shutdown there is no time to spare for it.
    /// </summary>
    internal void PrepareForShutdown() => StopTimer();

    /// <summary>
    /// Drops the server-side event session when the user navigates away. Without this the
    /// session keeps collecting on the monitored server after the page is gone.
    /// </summary>
    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        StopTimer();
        if (!_status.IsRunning) return;

        try
        {
            _status = await _repo.StopTraceAsync();
        }
        catch
        {
            // Nothing useful to show — the page is already gone. The next start drops any
            // session left behind by name.
        }
    }

    // ── Polling ──────────────────────────────────────────────────────────

    private void StartTimer()
    {
        _remainingSec = _intervalSec;
        UpdateCountdown();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer.Stop();
        TxtCountdown.Text = "--";
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSec--;
        UpdateCountdown();

        if (_remainingSec > 0 || _polling) return;

        _polling = true;
        try
        {
            await PollOnceAsync();
        }
        catch
        {
            // Skip failed ticks silently — a transient error must not stop the trace.
        }
        finally
        {
            _polling      = false;
            _remainingSec = _intervalSec;
            UpdateCountdown();
        }
    }

    private async Task PollOnceAsync()
    {
        if (_status.Mode == TraceMode.ExtendedEvents)
        {
            var fresh = await _repo.PollTraceEventsAsync();
            if (fresh.Count > 0)
            {
                _allCalls.AddRange(fresh);
                TrimHistory();
                RefreshFilterChoices();
                ApplyFilter();
            }

            _status = await _repo.GetTraceStatusAsync();
            ApplyStatus();
        }

        await LoadAggregatesAsync();
    }

    private async Task LoadAggregatesAsync()
    {
        var rows = await _repo.PollProcedureStatsAsync();

        // A quiet interval yields no rows; keep the previous view rather than blanking it.
        if (rows.Count == 0 && AggregateGrid.ItemsSource is not null) return;

        AggregateGrid.ItemsSource = rows;
        TxtNoAggregates.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TrimHistory()
    {
        var excess = _allCalls.Count - MaxRowsRetained;
        if (excess > 0) _allCalls.RemoveRange(0, excess);
    }

    private void UpdateCountdown() => TxtCountdown.Text = $"{Math.Max(0, _remainingSec)}s";

    // ── Filtering ────────────────────────────────────────────────────────

    private ComboBox[] FilterCombos => [CmbDatabase, CmbLogin, CmbApp, CmbHost, CmbEvent];

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void FilterCheck_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _filtersReady = false;
        try
        {
            TxtSearch.Text         = string.Empty;
            TxtSpidFilter.Text     = string.Empty;
            TxtMinDurFilter.Text   = string.Empty;
            TxtMinCpuFilter.Text   = string.Empty;
            TxtMinReadsFilter.Text = string.Empty;
            ChkHideResets.IsChecked     = true;    // back to the default, not "show everything"
            ChkHideExecuteSql.IsChecked = false;
            foreach (var combo in FilterCombos) combo.SelectedIndex = 0;
        }
        finally
        {
            _filtersReady = true;
        }
        ApplyFilter();
    }

    /// <summary>
    /// Rebuilds each combo's choices from the distinct values in the retained history,
    /// keeping the current selection even if its value has since aged out.
    /// </summary>
    private void RefreshFilterChoices()
    {
        _filtersReady = false;
        try
        {
            SetChoices(CmbDatabase, _allCalls.Select(c => c.DatabaseName));
            SetChoices(CmbLogin,    _allCalls.Select(c => c.LoginName));
            SetChoices(CmbApp,      _allCalls.Select(c => c.ClientAppName));
            SetChoices(CmbHost,     _allCalls.Select(c => c.ClientHostName));
            SetChoices(CmbEvent,    _allCalls.Select(c => c.EventName));
        }
        finally
        {
            _filtersReady = true;
        }
    }

    private static void SetChoices(ComboBox combo, IEnumerable<string?> values)
    {
        var selected = combo.SelectedItem as string ?? AllOption;

        var choices = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Append(selected)
            .Where(v => v != AllOption)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Prepend(AllOption)
            .ToList();

        // Skip the rebuild when nothing changed — clearing Items closes an open dropdown.
        if (combo.Items.Cast<string>().SequenceEqual(choices)) return;

        combo.Items.Clear();
        foreach (var choice in choices) combo.Items.Add(choice);
        combo.SelectedItem = selected;
    }

    private static string? SelectedValue(ComboBox combo) =>
        combo.SelectedItem is string s && s != AllOption ? s : null;

    private static long? ReadLong(TextBox box) =>
        long.TryParse(box.Text.Trim(), out var n) && n >= 0 ? n : null;

    private static bool EqualsIgnoreCase(string? a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        if (!_filtersReady) return;

        var search   = TxtSearch.Text.Trim();
        var database = SelectedValue(CmbDatabase);
        var login    = SelectedValue(CmbLogin);
        var app      = SelectedValue(CmbApp);
        var host     = SelectedValue(CmbHost);
        var evt      = SelectedValue(CmbEvent);
        var spid     = ReadLong(TxtSpidFilter);
        var minDur   = ReadLong(TxtMinDurFilter);
        var minCpu   = ReadLong(TxtMinCpuFilter);
        var minReads = ReadLong(TxtMinReadsFilter);
        var hideResets = ChkHideResets.IsChecked == true;
        var hideExecSql = ChkHideExecuteSql.IsChecked == true;

        IEnumerable<SpTraceEvent> query = _allCalls;

        if (search.Length > 0)
            query = query.Where(c =>
                c.ObjectName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                c.Statement? .Contains(search, StringComparison.OrdinalIgnoreCase) == true);

        if (database is not null) query = query.Where(c => EqualsIgnoreCase(c.DatabaseName,   database));
        if (login    is not null) query = query.Where(c => EqualsIgnoreCase(c.LoginName,      login));
        if (app      is not null) query = query.Where(c => EqualsIgnoreCase(c.ClientAppName,  app));
        if (host     is not null) query = query.Where(c => EqualsIgnoreCase(c.ClientHostName, host));
        if (evt      is not null) query = query.Where(c => EqualsIgnoreCase(c.EventName,      evt));

        if (spid     is not null) query = query.Where(c => c.SessionId    == spid);
        if (minDur   is not null) query = query.Where(c => c.DurationMs   >= minDur);
        if (minCpu   is not null) query = query.Where(c => c.CpuTimeMs    >= minCpu);
        if (minReads is not null) query = query.Where(c => c.LogicalReads >= minReads);

        if (hideResets)
            query = query.Where(c => c.ObjectName?.EndsWith("sp_reset_connection", StringComparison.OrdinalIgnoreCase) != true);

        if (hideExecSql)
            query = query.Where(c => c.ObjectName?.EndsWith("sp_executesql", StringComparison.OrdinalIgnoreCase) != true);

        // Newest first — the interesting row on a live trace is the one that just arrived.
        var filtered = query.OrderByDescending(c => c.EventTime).ToList();

        _visibleCalls.Clear();
        foreach (var call in filtered) _visibleCalls.Add(call);

        TxtNoCalls.Text = _allCalls.Count == 0
            ? "No calls captured yet."
            : "No captured calls match the current filters.";
        TxtNoCalls.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TxtFilterCount.Text = filtered.Count == _allCalls.Count
            ? $"{_allCalls.Count:N0} calls"
            : $"Showing {filtered.Count:N0} of {_allCalls.Count:N0}";

        if (BtnAutoScroll.IsChecked == true && filtered.Count > 0)
            CallsGrid.ScrollIntoView(filtered[0]);
    }

    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is not ComboBoxItem { Tag: int sec }) return;

        _intervalSec  = sec;
        _remainingSec = sec;
        UpdateCountdown();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _allCalls.Clear();
        RefreshFilterChoices();
        ApplyFilter();
        ClearDetail();
    }

    // ── Detail pane ──────────────────────────────────────────────────────

    private void CallsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CallsGrid.SelectedItem is not SpTraceEvent call)
        {
            ClearDetail();
            return;
        }

        TxtDetailHeader.Text = string.IsNullOrEmpty(call.ObjectName)
            ? "Statement"
            : $"Statement — {call.ObjectName}";
        TxtStatement.Text = call.Statement ?? "(no statement text captured)";

        BtnCopyStatement.IsEnabled = !string.IsNullOrWhiteSpace(call.Statement);
        BtnViewPlan.IsEnabled      = !string.IsNullOrWhiteSpace(call.ObjectName);
    }

    private void ClearDetail()
    {
        TxtDetailHeader.Text       = "Statement";
        TxtStatement.Text          = string.Empty;
        BtnCopyStatement.IsEnabled = false;
        BtnViewPlan.IsEnabled      = false;
    }

    private void BtnCopyStatement_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtStatement.Text))
            ClipboardHelper.SetText(TxtStatement.Text);
    }

    private async void BtnViewPlan_Click(object sender, RoutedEventArgs e)
    {
        if (CallsGrid.SelectedItem is not SpTraceEvent { ObjectName: { Length: > 0 } objectName } call)
            return;

        BtnViewPlan.IsEnabled = false;
        try
        {
            var planXml = await _repo.GetObjectQueryPlanAsync(objectName);
            new QueryExecutionPlanWindow(planXml, call.Statement) { Owner = Window.GetWindow(this) }
                .ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError($"Could not load the execution plan: {ex.Message}");
        }
        finally
        {
            BtnViewPlan.IsEnabled = true;
        }
    }

    // ── Status strip ─────────────────────────────────────────────────────

    private void ApplyStatus()
    {
        if (!_status.IsRunning)
        {
            SetBadge("Idle", "#38BDF8");
            TxtTraceStatus.Text = "Trace is not running. Press Start Trace to begin capturing stored-procedure calls.";
            return;
        }

        if (_status.Mode == TraceMode.ExtendedEvents)
        {
            SetBadge("Extended Events", "#22C55E");

            var text = $"Capturing live calls via session '{_status.SessionName}' — {_status.EventsCaptured:N0} captured.";

            // The ring buffer discards silently under burst. Say so, rather than letting the
            // view imply the server was quiet.
            if (_status.EventsDropped > 0)
                text += $"  ⚠ {_status.EventsDropped:N0} event(s) dropped by the ring buffer — raise Min duration to reduce volume.";

            if (_status.TargetTruncated)
                text += "  ⚠ Target data was truncated; some calls in this window were not read.";

            TxtTraceStatus.Text = text;
        }
        else
        {
            SetBadge("DMV fallback", "#F59E0B");
            TxtTraceStatus.Text = _status.Reason
                ?? "Per-call tracing is unavailable; showing per-procedure aggregates from DMV counters.";
        }
    }

    private void SetBadge(string text, string hexColor)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        TxtModeBadge.Text       = text;
        TxtModeBadge.Foreground = new SolidColorBrush(color);
        ModeBadge.BorderBrush   = new SolidColorBrush(color);
        ModeBadge.Background    = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B));
    }

    private int ReadMinDurationMs() =>
        int.TryParse(TxtMinDuration.Text.Trim(), out var ms) && ms >= 0 ? ms : 0;

    private void ShowError(string message) =>
        MessageBox.Show(message, "SP Trace", MessageBoxButton.OK, MessageBoxImage.Warning);
}
