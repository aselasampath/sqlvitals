using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Processes (#186): every session, as blocking chains on the map, one row each in the grid
/// (#116), or, on the Waiting view (#178, the Active Waits page before), every request waiting now
/// and on what. Read again every few seconds while Start is pressed; the interval, Start/Stop, the
/// view and the grid's grouping are remembered (#37). The verdict names the head blockers and how
/// long they have blocked, or on the Waiting view what the requests wait on (#146).
/// </summary>
/// <remarks>
/// The map and the grid are <see cref="Controls.ProcessMapControl"/>, which draws <see cref="Sessions"/>;
/// what it does on its own (a chain shown, a session selected) the view asks of it, from
/// <see cref="GoToSession"/> and <see cref="ChainsOnly"/>.
/// </remarks>
/// <param name="view">The view to open on, from the route (#178): <see cref="ProcessesView.Waiting"/>
/// for a link to the Active Waits page; null for the view last chosen.</param>
public sealed partial class ProcessesViewModel : PageViewModel
{
    /// <summary>An auto-refresh interval: 5 s to 2 min, in 5 s steps.</summary>
    public sealed record Interval(int Seconds, string Text);

    private static readonly IReadOnlyList<Interval> Choices =
        Enumerable.Range(1, 24).Select(i => i * 5).Select(sec => new Interval(sec, sec < 60 ? $"{sec}s" : $"{sec / 60}m {sec % 60:00}s")).ToList();

    /// <summary>The intervals offered.</summary>
    public IReadOnlyList<Interval> Intervals { get; } = Choices;

    private readonly ISessionRepository _sessions;
    private readonly IWaitsRepository _waitsRepo;
    private readonly ConnectionSettingsService _settings;
    private readonly INavigator _navigator;

    // The active connection's blocking threshold, which colours the verdict as it does the health dot.
    private readonly HealthThreshold? _blocking;

    // No saving while the saved choices are applied, or once the page is left.
    private bool _restoring = true;

    public ProcessesViewModel(ISessionRepository sessions, IWaitsRepository waits, ConnectionSettingsService settings,
                              INavigator navigator, string? view = null)
    {
        _sessions  = sessions;
        _waitsRepo = waits;
        _settings  = settings;
        _navigator = navigator;

        var store = settings.Load();
        _blocking = (store.Active is { } active ? store.ThresholdsFor(active) : store.HealthThresholds).Get(HealthIndicator.Blocking);

        // The interval and the Start/Stop choice are remembered (#37); stopped until it starts.
        SetPaused(true);
        IntervalSeconds = Choices.Any(i => i.Seconds == store.ProcessesRefreshSeconds) ? store.ProcessesRefreshSeconds : Choices[0].Seconds;
        AutoRefresh     = store.ProcessesAutoRefresh;

        // So is the view, and the grid's grouping (#116); a link may open another view (#178).
        _groupByLogin = store.ProcessesGroupByLogin;
        _view = view is not null && Enum.TryParse<ProcessesView>(view, out var opened) && Enum.IsDefined(opened) ? opened : store.ProcessesView;
        _restoring = false;
    }

    // ── Auto-refresh: the base class's timer (#169) ───────────────────────

    /// <summary>How often it reads while started.</summary>
    [ObservableProperty]
    private int _intervalSeconds;

    partial void OnIntervalSecondsChanged(int value)
    {
        ShowsCountdown  = true;
        RefreshInterval = TimeSpan.FromSeconds(value);
        SaveRefreshChoice();
        ReportContextChanged();
    }

    /// <summary>Start (true) or Stop: the top bar's Resume and Pause are the same (#140).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoRefreshText))]
    private bool _autoRefresh;

    partial void OnAutoRefreshChanged(bool value)
    {
        SetPaused(!value);
        SaveRefreshChoice();
        ReportContextChanged();
    }

    /// <summary>The toggle's words: what pressing it does.</summary>
    public string AutoRefreshText => AutoRefresh ? "Stop" : "Start";

    private void SaveRefreshChoice()
    {
        if (_restoring)
            return;
        try
        {
            _settings.SetProcessesRefresh(IntervalSeconds, AutoRefresh);
        }
        catch (Exception ex)
        {
            AppLog.Error("Processes: saving the auto-refresh choice failed", ex);
        }
    }

    // ── The view: Map, Grid or Waiting ────────────────────────────────────

    /// <summary>The view on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMap), nameof(IsGrid), nameof(IsWaiting), nameof(ShowsMap))]
    private ProcessesView _view;

    // The view buttons: checking one chooses it; unchecking the others says nothing.
    public bool IsMap
    {
        get => View == ProcessesView.Map;
        set { if (value) Choose(ProcessesView.Map); }
    }

    public bool IsGrid
    {
        get => View == ProcessesView.Grid;
        set { if (value) Choose(ProcessesView.Grid); }
    }

    public bool IsWaiting
    {
        get => View == ProcessesView.Waiting;
        set { if (value) Choose(ProcessesView.Waiting); }
    }

    /// <summary>The map or the grid is shown, not Waiting.</summary>
    public bool ShowsMap => !IsWaiting;

    /// <summary>The grid is grouped by login; remembered with the view.</summary>
    [ObservableProperty]
    private bool _groupByLogin;

    partial void OnGroupByLoginChanged(bool value) => SaveViewChoice();

    // A view chosen by its button: remembered, and Waiting reads its waits if they weren't. A read
    // still running reads them itself, since it looks at the view once it has the sessions.
    private void Choose(ProcessesView view)
    {
        if (view == View)
            return;
        Show(view);
        SaveViewChoice();
        if (view == ProcessesView.Waiting && _waits is null && _read && !IsBusy)
            _ = RereadAsync();
    }

    // Shows a view, without making it the one Processes opens on: a link's, a chain's.
    private void Show(ProcessesView view)
    {
        View = view;
        ShowVerdict();
        ReportContextChanged();
    }

    private void SaveViewChoice()
    {
        if (_restoring)
            return;
        try
        {
            _settings.SetProcessesView(View, GroupByLogin);
        }
        catch (Exception ex)
        {
            AppLog.Error("Processes: saving the view failed", ex);
        }
    }

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>The last snapshot of the sessions, classified into chains; null before the first.</summary>
    [ObservableProperty]
    private IReadOnlyList<ProcessNode>? _sessionsRead;

    /// <summary>The requests waiting the Waiting view lists: all, or a session's and those it blocks.</summary>
    public GridRows<ActiveWait> Waits { get; } = [];

    // The waits read last, while the Waiting view is shown; null once another view is.
    private List<ActiveWait>? _waits;

    // A session from its link (#145): its waits, and those of the sessions it blocks.
    private int? _session;

    // The page has read once: a view chosen after reads what it lacks.
    private bool _read;

    // What couldn't be read, for the verdict.
    private string _reading = "the sessions";

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        _read    = true;
        _reading = "the sessions";
        SessionsRead = BlockingChains.Classify(await _sessions.GetProcessesAsync(cancel));

        // The waits only while they are shown: the map and the grid don't need them.
        if (View == ProcessesView.Waiting)
        {
            _reading = "the waiting tasks";
            _waits   = (await _waitsRepo.GetActiveWaitsAsync(cancel)).ToList();
            ShowWaits();
        }
        else
        {
            // Out of date once another view is on screen: read again when Waiting is.
            _waits = null;
        }
        ShowVerdict();
    }

    protected override void ShowLoadError(Exception exception) => ShowError(_reading, exception);

    // The verdict of the view on screen, from what was read last.
    private void ShowVerdict()
    {
        if (View == ProcessesView.Waiting)
        {
            if (_waits is not null)
                Verdict = ActiveWaitsVerdict.From(_waits, _blocking);
        }
        else if (SessionsRead is { } sessions)
        {
            Verdict = ProcessesVerdict.From(sessions, _blocking);
        }
    }

    private List<ActiveWait> WaitsShown =>
        _session is { } id
            ? (_waits ?? []).Where(w => w.SessionId == id || w.BlockingSessionId == id).ToList()
            : _waits ?? [];

    private void ShowWaits()
    {
        if (_waits is null)
            return;
        Waits.Replace(WaitsShown);
        ReportContextChanged();
    }

    // ── Evidence (#145): a session's waits; a blocking chain is the map's ──

    /// <summary>The Waiting view, with only a session's waits and those of the sessions it blocks, until Show all.</summary>
    public void FocusOnSession(int sessionId)
    {
        _session = sessionId;
        Show(ProcessesView.Waiting);
    }

    /// <summary>A blocking chain is drawn on the map or the grid: not on Waiting, if that was last chosen.</summary>
    public void ShowChainView()
    {
        if (View == ProcessesView.Waiting)
            Show(ProcessesView.Map);
    }

    /// <summary>Show all: every waiting request again.</summary>
    public void ClearSession()
    {
        if (_session is null)
            return;
        _session = null;
        ShowWaits();
    }

    /// <summary>A session's filter is shown: Waiting is on screen. It is kept while another view is, for Show all.</summary>
    public bool SessionShown => _session is not null && View == ProcessesView.Waiting;

    /// <summary>A session's filter is set, shown or not.</summary>
    public bool HasSession => _session is not null;

    /// <summary>What the Waiting view shows of its rows, while a session's filter is shown.</summary>
    public string? SessionNote => !SessionShown || _waits is null ? null
        : WaitsShown.Count == 0 ? $"session {_session} isn't waiting, or blocking anything, now"
        : $"showing session {_session} and the sessions it blocks only";

    public string SessionShowAllText => _waits is null ? "Show all" : $"Show all {_waits.Count} waiting tasks";

    /// <summary>The map's focus changed: the top bar and the banner follow.</summary>
    public void MapContextChanged() => ReportContextChanged();

    // ── The verdict's actions, and the drawer's ───────────────────────────

    /// <summary>Selects a session on the map or in the grid, and shows its details: the verdict's Go to session.</summary>
    public event Action<int>? GoToSession;

    /// <summary>Shows only the sessions in a blocking chain: the verdict's action.</summary>
    public event Action? ChainsOnly;

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ProcessesVerdict.GoToHead when action.Id is { } id:    GoToSession?.Invoke((int)id); break;
            case ProcessesVerdict.ChainsOnly:                            ChainsOnly?.Invoke();         break;
            case ActiveWaitsVerdict.Processes when action.Id is { } id: ShowChainOnMap((int)id);      break;
        }
    }

    // The Waiting verdict's "Show the chain on the map": the map, the chains only, the blocker
    // selected. A look at the chain, not a choice of view: the view Processes opens on is kept.
    private void ShowChainOnMap(int blocker)
    {
        Show(ProcessesView.Map);
        ChainsOnly?.Invoke();
        GoToSession?.Invoke(blocker);
    }

    /// <summary>A session's plan, for the drawer (#147).</summary>
    public Task<SessionPlan> SessionPlanAsync(int sessionId) => _sessions.GetSessionPlanAsync(sessionId);

    /// <summary>
    /// The Waiting view filtered to the session and the sessions it blocks (#145, #178): a link to
    /// another place, so Back returns to the session on the map.
    /// </summary>
    public Task OpenWaitsAsync(int sessionId) =>
        _navigator.GoToAsync("Processes", new FocusFilter(FocusKind.Session, sessionId), "Processes");

    protected override void OnLeaving() => _restoring = true;
}
