using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Live SP Trace (#149, #188): stored-procedure calls streamed from an Extended Events session when
/// the login can create one, per-procedure figures from the DMVs when it can't
/// (<see cref="ISpTraceRepository"/>). Opening the page or Refresh never starts a trace: that runs
/// a session on the server, so only Start Trace does, and leaving the page drops it. While it runs,
/// the timer polls it. The verdict says what the capture is doing.
/// </summary>
public sealed partial class SpTraceViewModel : PageViewModel
{
    /// <summary>The tabs: the calls, and the figures per procedure.</summary>
    internal const int CallsTab = 0, ProceduresTab = 1;

    /// <summary>How many calls are kept; the oldest go first.</summary>
    internal const int MaxRowsRetained = 5000;

    private readonly ISpTraceRepository _trace;
    private readonly IResourceQueryRepository _plans;
    private readonly IDialogService _dialogs;

    // Every call kept, oldest first; the grid shows those the filters let through, newest first.
    private readonly List<SpTraceEvent> _allCalls = [];

    private TraceSessionStatus _status = TraceSessionStatus.Stopped("SqlVitals_SpTrace");

    // The filters are being reset or refilled: one filtering after, not one per box.
    private bool _filtersHeld;

    // The page was left: a start that finishes after drops its session again.
    private bool _left;

    // Which read is running: a failed poll is said in the top bar only, as before #188.
    private bool _readingTimed;

    public SpTraceViewModel(ISpTraceRepository trace, IResourceQueryRepository plans, IDialogService dialogs)
    {
        _trace   = trace;
        _plans   = plans;
        _dialogs = dialogs;
        ShowsCountdown = true;

        foreach (var box in FilterBoxes)
            box.PropertyChanged += OnFilterBoxChanged;
        ApplyStatus();
        ApplyFilter();
    }

    // ── What the top bar shows ────────────────────────────────────────────

    /// <summary>Polling: the trace runs. Stopped, nothing moves.</summary>
    public bool IsPolling => RefreshInterval is not null;

    /// <summary>How often a running trace is polled; null while it is stopped.</summary>
    public TimeSpan? PollInterval => RefreshInterval;

    // ── Start and Stop ────────────────────────────────────────────────────

    /// <summary>A trace runs, or is starting: the button reads Stop Trace.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TraceButtonText))]
    private bool _isTracing;

    public string TraceButtonText => IsTracing ? "■ Stop Trace" : "● Start Trace";

    /// <summary>Only calls at least this slow are captured, in ms; read as the trace starts.</summary>
    [ObservableProperty]
    private string _minDurationText = "0";

    internal int MinDurationMs => int.TryParse(MinDurationText.Trim(), out var ms) && ms >= 0 ? ms : 0;

    /// <summary>
    /// Starts the trace, or stops it: the button. It can't be pressed again while a start runs. A
    /// start that fails says so and drops whatever session it left; a stop that fails says the
    /// session may be left on the server.
    /// </summary>
    [RelayCommand]
    private Task ToggleTrace() => IsTracing ? StopAsync() : StartAsync();

    private async Task StartAsync()
    {
        IsTracing = true;
        try
        {
            _status = await _trace.StartTraceAsync(SpTraceOptions.Default with { MinDurationMs = MinDurationMs });
            if (_left)
            {
                // Left while it started: nothing is on screen to stop it.
                await DropAsync();
                return;
            }
            ApplyStatus();

            // DMV mode has no per-call data, so send the user to the view that does have it.
            if (_status.Mode == TraceMode.DmvFallback)
                TabIndex = ProceduresTab;
            Poll(true);
        }
        catch (Exception) when (_left)
        {
            // Left while it started, which cancels the start: nothing is on screen to say so,
            // and whatever session it made is dropped.
            await DropAsync();
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"Could not start the trace: {ex.Message}", "SP Trace");
            await StopAsync();
        }
    }

    private async Task StopAsync()
    {
        IsTracing = false;
        Poll(false);
        try
        {
            _status = await _trace.StopTraceAsync();
            ApplyStatus();
        }
        catch (Exception ex)
        {
            _dialogs.Warn($"The trace stopped, but the event session may not have been dropped: {ex.Message}", "SP Trace");
        }
    }

    /// <summary>
    /// Stops polling without waiting on the server. The window calls it before it blocks on
    /// dropping the event session as it closes: a poll in flight holds the repository's gate for a
    /// full round trip, and on shutdown there is no time to spare for it.
    /// </summary>
    public void PrepareForShutdown() => Poll(false);

    protected override void OnShowing() => _left = false;

    // Left: the session would keep collecting on the server after the page is gone, so it is
    // dropped. Nothing is shown about it: the page is gone, and the next start drops any session
    // left behind by name.
    protected override void OnLeaving()
    {
        _left = true;
        Poll(false);
        IsTracing = false;
        if (_status.IsRunning)
            _ = DropAsync();
    }

    private async Task DropAsync()
    {
        try
        {
            _status = await _trace.StopTraceAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("SP Trace: dropping the session on leaving failed", ex);
        }
    }

    // ── Polling ───────────────────────────────────────────────────────────

    private static readonly int[] Intervals = [1, 2, 3, 5, 10, 15, 30];

    /// <summary>How often a running trace is polled.</summary>
    public IReadOnlyList<string> IntervalNames { get; } = Intervals.Select(s => $"{s}s").ToList();

    [ObservableProperty]
    private int _intervalIndex = 1;   // 2 s, as the session's MAX_DISPATCH_LATENCY

    internal int IntervalSeconds => Intervals[Math.Clamp(IntervalIndex, 0, Intervals.Length - 1)];

    // Another interval: a running trace's wait starts again; a stopped one uses it from the next Start.
    partial void OnIntervalIndexChanged(int value)
    {
        if (IsPolling)
            Poll(true);
        ReportContextChanged();
    }

    // The timer polls the running trace (LoadAsync, timed), and reports a failed poll to the top
    // bar; the trace keeps running.
    private void Poll(bool on)
    {
        RefreshInterval = on ? TimeSpan.FromSeconds(IntervalSeconds) : null;
        OnPropertyChanged(nameof(IsPolling));
        ReportContextChanged();
    }

    protected override Task LoadAsync(CancellationToken cancel) => LoadAsync(PageRead.Refresh, cancel);

    // Opening the page and Refresh read the procedures' figures, or poll a running trace; the
    // timer polls. Neither starts a trace.
    protected override Task LoadAsync(PageRead read, CancellationToken cancel)
    {
        _readingTimed = read == PageRead.Timed;
        return _status.IsRunning || _readingTimed ? PollOnceAsync(cancel) : LoadProceduresAsync(cancel);
    }

    protected override void ShowLoadError(Exception exception)
    {
        if (!_readingTimed)
            ShowError("the procedure statistics", exception);
    }

    private async Task PollOnceAsync(CancellationToken cancel)
    {
        if (_status.Mode == TraceMode.ExtendedEvents)
        {
            var fresh = await _trace.PollTraceEventsAsync(cancel);
            if (fresh.Count > 0)
            {
                _allCalls.AddRange(fresh);
                var excess = _allCalls.Count - MaxRowsRetained;
                if (excess > 0)
                    _allCalls.RemoveRange(0, excess);
                RefreshFilterChoices();
                ApplyFilter();
                UpdateVerdict();
            }

            _status = await _trace.GetTraceStatusAsync(cancel);
            ApplyStatus();
        }
        await LoadProceduresAsync(cancel);
    }

    private async Task LoadProceduresAsync(CancellationToken cancel)
    {
        var rows = await _trace.PollProcedureStatsAsync(cancel);

        // A quiet interval yields no rows; keep the previous view rather than blanking it.
        if (rows.Count == 0 && Procedures.IsFilled)
            return;
        Procedures.Replace(rows);
        ShowsNoProcedures = rows.Count == 0;
        UpdateVerdict();
    }

    // ── The calls ─────────────────────────────────────────────────────────

    /// <summary>The tab shown: <see cref="CallsTab"/> or <see cref="ProceduresTab"/>.</summary>
    [ObservableProperty]
    private int _tabIndex = CallsTab;

    /// <summary>The calls the filters let through, newest first.</summary>
    public GridRows<SpTraceEvent> Calls { get; } = [];

    [ObservableProperty]
    private SpTraceEvent? _selectedCall;

    /// <summary>The newest call stays in view as calls arrive.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>A call to scroll into view: the newest, while <see cref="AutoScroll"/> is on.</summary>
    public event Action<SpTraceEvent>? ScrollTo;

    /// <summary>A call to show: the verdict's Show the slowest selected it. The page opens it in the drawer.</summary>
    public event Action<SpTraceEvent>? ShowRow;

    /// <summary>The calls were cleared: the drawer says to select one again.</summary>
    public event Action? Cleared;

    /// <summary>"No calls captured yet." or "No captured calls match the current filters."; null while some show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoCalls))]
    private string? _noCalls;

    public bool ShowsNoCalls => NoCalls is not null;

    /// <summary>"1,204 calls", or "Showing 12 of 1,204" while the filters hide some.</summary>
    [ObservableProperty]
    private string _filterCount = "";

    /// <summary>Every call kept; for tests.</summary>
    internal IReadOnlyList<SpTraceEvent> AllCalls => _allCalls;

    [RelayCommand]
    private void Clear()
    {
        _allCalls.Clear();
        RefreshFilterChoices();
        ApplyFilter();
        Cleared?.Invoke();
        UpdateVerdict();
    }

    // ── The procedures ────────────────────────────────────────────────────

    /// <summary>What each procedure did since the last poll; kept through a quiet poll.</summary>
    public GridRows<SpAggregateRow> Procedures { get; } = [];

    [ObservableProperty]
    private bool _showsNoProcedures = true;

    // ── The filters: on the calls kept, in the app ────────────────────────

    /// <summary>Procedure name or statement text contains it.</summary>
    [ObservableProperty]
    private string _search = "";

    public FilterChoices Database { get; } = new();
    public FilterChoices Login { get; } = new();
    public FilterChoices ClientApp { get; } = new();
    public FilterChoices Host { get; } = new();
    public FilterChoices Event { get; } = new();

    private FilterChoices[] FilterBoxes => [Database, Login, ClientApp, Host, Event];

    [ObservableProperty]
    private string _spidText = "";

    [ObservableProperty]
    private string _minDurationFilterText = "";

    [ObservableProperty]
    private string _minCpuText = "";

    [ObservableProperty]
    private string _minReadsText = "";

    /// <summary>Connection-pool resets are noise for most investigations: hidden by default.</summary>
    [ObservableProperty]
    private bool _hideResets = true;

    /// <summary>Parameterised ad hoc batches; their statement text is often the real query.</summary>
    [ObservableProperty]
    private bool _hideExecuteSql;

    // Any filter changed: the calls are filtered again.
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Search) or nameof(SpidText) or nameof(MinDurationFilterText) or nameof(MinCpuText)
                           or nameof(MinReadsText) or nameof(HideResets) or nameof(HideExecuteSql))
            ApplyFilter();
    }

    private void OnFilterBoxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterChoices.Selected))
            ApplyFilter();
    }

    /// <summary>Every filter back to how the page opens: nothing hidden but sp_reset_connection.</summary>
    [RelayCommand]
    private void ClearFilters() => HoldingFilters(() =>
    {
        Search                = "";
        SpidText              = "";
        MinDurationFilterText = "";
        MinCpuText            = "";
        MinReadsText          = "";
        HideResets            = true;   // back to the default, not "show everything"
        HideExecuteSql        = false;
        foreach (var box in FilterBoxes)
            box.Clear();
    });

    // Changes the filters, then filters once.
    private void HoldingFilters(Action change)
    {
        _filtersHeld = true;
        try
        {
            change();
        }
        finally
        {
            _filtersHeld = false;
        }
        ApplyFilter();
    }

    // Each box's choices are the values in the calls kept, keeping the one picked even if its
    // calls have since gone.
    private void RefreshFilterChoices() => HoldingFilters(() =>
    {
        Database.Replace(_allCalls.Select(c => c.DatabaseName));
        Login.Replace(_allCalls.Select(c => c.LoginName));
        ClientApp.Replace(_allCalls.Select(c => c.ClientAppName));
        Host.Replace(_allCalls.Select(c => c.ClientHostName));
        Event.Replace(_allCalls.Select(c => c.EventName));
    });

    private static long? ReadLong(string text) => long.TryParse(text.Trim(), out var n) && n >= 0 ? n : null;

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        if (_filtersHeld)
            return;

        var search   = Search.Trim();
        var spid     = ReadLong(SpidText);
        var minDur   = ReadLong(MinDurationFilterText);
        var minCpu   = ReadLong(MinCpuText);
        var minReads = ReadLong(MinReadsText);

        IEnumerable<SpTraceEvent> query = _allCalls;
        if (search.Length > 0)
            query = query.Where(c => c.ObjectName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
                                     c.Statement?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
        if (Database.Value is { } database)  query = query.Where(c => Same(c.DatabaseName,   database));
        if (Login.Value is { } login)        query = query.Where(c => Same(c.LoginName,      login));
        if (ClientApp.Value is { } app)      query = query.Where(c => Same(c.ClientAppName,  app));
        if (Host.Value is { } host)          query = query.Where(c => Same(c.ClientHostName, host));
        if (Event.Value is { } evt)          query = query.Where(c => Same(c.EventName,      evt));
        if (spid is not null)                query = query.Where(c => c.SessionId    == spid);
        if (minDur is not null)              query = query.Where(c => c.DurationMs   >= minDur);
        if (minCpu is not null)              query = query.Where(c => c.CpuTimeMs    >= minCpu);
        if (minReads is not null)            query = query.Where(c => c.LogicalReads >= minReads);
        if (HideResets)
            query = query.Where(c => c.ObjectName?.EndsWith("sp_reset_connection", StringComparison.OrdinalIgnoreCase) != true);
        if (HideExecuteSql)
            query = query.Where(c => c.ObjectName?.EndsWith("sp_executesql", StringComparison.OrdinalIgnoreCase) != true);

        // Newest first: the interesting row on a live trace is the one that just arrived.
        var filtered = query.OrderByDescending(c => c.EventTime).ToList();
        var selected = SelectedCall;
        Calls.Replace(filtered);
        SelectedCall = selected is not null && filtered.Contains(selected) ? selected : null;

        NoCalls     = filtered.Count > 0 ? null : _allCalls.Count == 0 ? "No calls captured yet." : "No captured calls match the current filters.";
        FilterCount = filtered.Count == _allCalls.Count ? $"{_allCalls.Count:N0} calls" : $"Showing {filtered.Count:N0} of {_allCalls.Count:N0}";

        if (AutoScroll && filtered.Count > 0)
            ScrollTo?.Invoke(filtered[0]);
    }

    // ── The details drawer's plan ─────────────────────────────────────────

    /// <summary>The cached plan of the procedure a call ran.</summary>
    public Task<string?> PlanOfAsync(string objectName, CancellationToken cancel = default) =>
        _plans.GetObjectQueryPlanAsync(objectName, cancel);

    // ── The status strip ──────────────────────────────────────────────────

    /// <summary>"Idle", "Extended Events" or "DMV fallback".</summary>
    [ObservableProperty]
    private string _modeText = "Idle";

    /// <summary>The theme brush the mode is drawn in: "Info", "Success" or "Warning".</summary>
    [ObservableProperty]
    private string _modeTone = "Info";

    /// <summary>What the trace is doing, in a line, with what it lost.</summary>
    [ObservableProperty]
    private string _traceStatus = "";

    private void ApplyStatus()
    {
        if (!_status.IsRunning)
        {
            (ModeText, ModeTone) = ("Idle", "Info");
            TraceStatus = "Trace is not running. Press Start Trace to begin capturing stored-procedure calls.";
        }
        else if (_status.Mode == TraceMode.ExtendedEvents)
        {
            (ModeText, ModeTone) = ("Extended Events", "Success");
            var text = $"Capturing live calls via session '{_status.SessionName}' — {_status.EventsCaptured:N0} captured.";

            // The ring buffer discards silently under burst. Say so, rather than letting the view
            // imply the server was quiet.
            if (_status.EventsDropped > 0)
                text += $"  ⚠ {_status.EventsDropped:N0} event(s) dropped by the ring buffer — raise Min duration to reduce volume.";
            if (_status.TargetTruncated)
                text += "  ⚠ Target data was truncated; some calls in this window were not read.";
            TraceStatus = text;
        }
        else
        {
            (ModeText, ModeTone) = ("DMV fallback", "Warning");
            TraceStatus = _status.Reason ?? "Per-call tracing is unavailable; showing per-procedure aggregates from DMV counters.";
        }
        UpdateVerdict();
    }

    // ── The verdict ───────────────────────────────────────────────────────

    private void UpdateVerdict() => Verdict = SpTraceVerdict.From(_status, _allCalls, Procedures.Count, MinDurationMs);

    protected override Task OnActionAsync(VerdictAction action) => action.Key switch
    {
        SpTraceVerdict.Start when CanToggle(on: true) => ToggleTraceCommand.ExecuteAsync(null),
        SpTraceVerdict.Stop when CanToggle(on: false) => ToggleTraceCommand.ExecuteAsync(null),
        SpTraceVerdict.ShowSlowest                    => ShowSlowest(),
        _                                             => Task.CompletedTask,
    };

    // The verdict's Start or Stop, as the button: not while a start runs, which the button waits for.
    private bool CanToggle(bool on) => IsTracing != on && !ToggleTraceCommand.IsRunning;

    // The verdict's Show the slowest: on Live Calls, selected, in view and in the drawer; the
    // filters cleared first if they hide it.
    private Task ShowSlowest()
    {
        if (_allCalls.OrderByDescending(c => c.DurationMs).ThenByDescending(c => c.EventTime).FirstOrDefault() is not { } slowest)
            return Task.CompletedTask;
        if (!Calls.Contains(slowest))
            ClearFilters();
        if (!Calls.Contains(slowest))
            HoldingFilters(() => (HideResets, HideExecuteSql) = (false, false));   // the defaults hide it too
        TabIndex     = CallsTab;
        SelectedCall = slowest;
        ShowRow?.Invoke(slowest);
        return Task.CompletedTask;
    }
}
