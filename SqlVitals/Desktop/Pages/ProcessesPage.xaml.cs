using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ProcessesPage : Page, ICodeBehindPage, IPageContext, IFocusPage, IDetailsSource
{
    // ── The details drawer (#147): the selected session ───────────────────
    private const string SelectHint = "Select a session on the map or in the grid to see its details.";

    private SessionDetailsView? _sessionView;

    public DrawerContent Details { get; private set; } = new("Session details", null, SelectHint);

    public event Action<bool>? DetailsChanged;

    // The selected session, as the latest snapshot has it; the same view while it stays selected,
    // so a refresh keeps its plan and script open.
    private void ShowSession(int sessionId, ProcessNode? node, bool open)
    {
        if (node is null)
        {
            Details = new($"Session {sessionId}", null,
                          $"Session {sessionId} has gone since it was selected: it ended or disconnected. Select another session.");
        }
        else
        {
            _sessionView ??= new SessionDetailsView(id => _repo.GetSessionPlanAsync(id), OpenWaits);
            _sessionView.Show(node, ProcessMap.Snapshot);
            Details = new($"Session {sessionId}", _sessionView, SelectHint);
        }
        DetailsChanged?.Invoke(open);
    }

    // The Waiting view filtered to the session and the sessions it blocks (#145, #178): a link to
    // another place (EvidenceRoutes.Link), so Back returns to the session on the map.
    private Task OpenWaits(int sessionId) =>
        _navigator.GoToAsync("Processes", new FocusFilter(FocusKind.Session, sessionId), "Processes");

    // ── Evidence (#145): a blocking alert's chain, or a session's waits ───
    public void ShowFocus(PageFocus focus)
    {
        switch (focus.Filter)
        {
            case { Kind: FocusKind.BlockingChain }:
                // The chain is drawn on the map or the grid: not on Waiting, if that was last chosen.
                if (_view == ProcessesView.Waiting)
                    ShowView(ProcessesView.Map);
                ProcessMap.FocusChain((int?)focus.Filter.Id);
                break;
            case { Kind: FocusKind.Session, Id: { } id }:
                _session = (int)id;
                ShowView(ProcessesView.Waiting);
                break;
        }
    }

    public void ClearFocus()
    {
        if (_session is not null)
        {
            _session = null;
            ShowWaits();
        }
        if (ProcessMap.IsChainFocused)
            ProcessMap.ClearChainFocus();
    }

    // What the view on screen shows of its rows: the session's waits on Waiting, the chain on the
    // map or the grid. A session's filter is kept while another view is shown, for Show all.
    private bool SessionShown => _session is not null && _view == ProcessesView.Waiting;

    public string? FocusNote => SessionShown
        ? _waits is null ? null
          : WaitsShown.Count == 0 ? $"session {_session} isn't waiting, or blocking anything, now"
          : $"showing session {_session} and the sessions it blocks only"
        : !ProcessMap.IsChainFocused || !ProcessMap.HasSnapshot ? null
        : ProcessMap.FocusedHead is { } head ? $"showing the blocking chain of session {head} only"
        : "nothing is blocked now";

    public string? ShowAllText => SessionShown
        ? _waits is null ? "Show all" : $"Show all {_waits.Count} waiting tasks"
        : ProcessMap.IsChainFocused
            ? ProcessMap.HasSnapshot ? $"Show all {ProcessMap.SessionCount:N0} sessions" : "Show all sessions"
        : _session is not null ? "Show all" : null;

    // Live while auto-refresh is on; the bar's Pause and Resume are its Stop and Start (#140), and
    // pause the view model's timer (#169).
    public PageKind Kind => PageKind.Live;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(_intervalSec);
    public bool CanPause => true;
    public bool IsPaused => ViewModel.IsPaused;
    public void SetPaused(bool paused) => BtnAutoRefresh.IsChecked = !paused;

    private readonly IWaitStatsRepository _repo;
    private readonly ConnectionSettingsService _settings;

    // The page template's verdict (#146): the head blockers and how long they have blocked, or on
    // the Waiting view what the requests wait on (#178).
    internal ProcessesViewModel ViewModel { get; }

    // The active connection's blocking threshold, which colours the verdict as it does the health dot.
    private readonly HealthThreshold _blocking;

    // ── The Waiting view (#178): the requests waiting, as the Active Waits page read them ──
    private ProcessesView _view = ProcessesView.Map;
    private List<ActiveWait>? _waits;
    private int? _session;   // a session from its link (#145): its waits and those of the sessions it blocks
    private bool _switching; // a view button checked in code

    // ?? Auto-refresh: the view model's timer (#169) ????????????????????
    private int _intervalSec   = ConnectionStore.DefaultProcessesRefreshSeconds;
    private bool _restoring    = true;   // no saving while the saved choice is applied, or while leaving

    // Interval choices: 5 s increments up to 120 s
    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray(); // 5,10,15,...,120

    // The top bar: what this page read, what went wrong, and its kind or window (#166).
    private readonly IShellStatus _shell;

    // A session's link to its waits (#145).
    private readonly INavigator _navigator;

    /// <param name="view">The view to open on, from the route (#178): <see cref="ProcessesView.Waiting"/>
    /// for a link to the Active Waits page; null for the view last chosen.</param>
    public ProcessesPage(IWaitStatsRepository repo, ConnectionSettingsService settings, INavigator? navigator = null,
                         IShellStatus? shell = null, string? view = null)
    {
        _shell     = shell ?? CompositionRoot.Get<IShellStatus>();
        _navigator = navigator ?? CompositionRoot.Get<INavigator>();
        _repo     = repo;
        _settings = settings;
        InitializeComponent();

        foreach (var sec in Intervals)
            CmbInterval.Items.Add(new ComboBoxItem
            {
                Content = sec < 60 ? $"{sec}s" : $"{sec / 60}m {sec % 60:00}s",
                Tag     = sec
            });

        // The interval and the Start/Stop choice are remembered (#37).
        var store = settings.Load();

        _blocking = (store.Active is { } active ? store.ThresholdsFor(active) : store.HealthThresholds).Get(HealthIndicator.Blocking);
        ViewModel = new ProcessesViewModel(goToSession: ProcessMap.ShowSession, chainsOnly: ProcessMap.ShowChainsOnly, chainOnMap: ShowChainOnMap);
        ViewModel.Attach(Shell);
        // Stopped until the saved choice starts it, once the page is loaded.
        ViewModel.SetPaused(true);
        TxtCountdown.SetBinding(TextBlock.TextProperty, new Binding(nameof(ViewModel.Countdown)) { Source = ViewModel });
        Shell.Folds = settings;
        CmbInterval.SelectedIndex = Math.Max(0, Array.IndexOf(Intervals, store.ProcessesRefreshSeconds));

        // So is the view, and the grid's grouping (#116); a link may open another view (#178).
        ProcessMap.GroupByLogin = store.ProcessesGroupByLogin;
        ShowView(view is not null && Enum.TryParse<ProcessesView>(view, out var opened) && Enum.IsDefined(opened) ? opened : store.ProcessesView);
        ProcessMap.GroupByLoginChanged += (_, _) => SaveViewChoice();
        ProcessMap.FocusChanged        += (_, _) => _shell.ReportContextChanged(this);
        ProcessMap.SessionSelected     += ShowSession;

        Loaded += (_, _) =>
        {
            if (store.ProcessesAutoRefresh) BtnAutoRefresh.IsChecked = true;
            _restoring = false;
        };

        // Leaving stops the timer (the view model's OnNavigatedFrom); the saved choice is kept.
        Unloaded += (_, _) => _restoring = true;
    }

    private void SaveRefreshChoice()
    {
        if (_restoring) return;
        try
        {
            _settings.SetProcessesRefresh(_intervalSec, BtnAutoRefresh.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Processes: saving the auto-refresh choice failed", ex);
        }
    }

    private void SaveViewChoice()
    {
        if (_restoring) return;
        try
        {
            _settings.SetProcessesView(_view, ProcessMap.GroupByLogin);
        }
        catch (Exception ex)
        {
            AppLog.Error("Processes: saving the view failed", ex);
        }
    }

    /// <summary>The view on screen.</summary>
    internal ProcessesView View => _view;

    // Shows a view, its button checked. The map and the grid draw the snapshot already loaded; the
    // Waiting view reads what it lacks.
    private void ShowView(ProcessesView view)
    {
        _view = view;
        var button = view switch
        {
            ProcessesView.Grid    => BtnGrid,
            ProcessesView.Waiting => BtnWaiting,
            _                     => BtnMap,
        };
        _switching = true;
        try { button.IsChecked = true; }
        finally { _switching = false; }

        var waiting = view == ProcessesView.Waiting;
        ProcessMap.Visibility   = waiting ? Visibility.Collapsed : Visibility.Visible;
        WaitingPanel.Visibility = waiting ? Visibility.Visible   : Visibility.Collapsed;
        if (!waiting)
            ProcessMap.ShowGrid = view == ProcessesView.Grid;
        ShowVerdict();
    }

    private async void View_Checked(object sender, RoutedEventArgs e)
    {
        if (_switching || ProcessMap is null || WaitingPanel is null) return;   // set in code, or while the XAML loads
        var view = BtnWaiting.IsChecked == true ? ProcessesView.Waiting
                 : BtnGrid.IsChecked == true    ? ProcessesView.Grid
                 : ProcessesView.Map;
        ShowView(view);
        SaveViewChoice();
        _shell.ReportContextChanged(this);

        // Opened on the map, the waits weren't read: read them now, rather than at the next refresh.
        if (view == ProcessesView.Waiting && _waits is null && IsLoaded)
            await ViewModel.RereadAsync();
    }

    // The Waiting verdict's "Show the chain on the map": the map, the chains only, the blocker
    // selected. A look at the chain, not a choice of view: the view Processes opens on is kept.
    private void ShowChainOnMap(int blocker)
    {
        ShowView(ProcessesView.Map);
        ProcessMap.ShowChainsOnly();
        ProcessMap.ShowSession(blocker);
        _shell.ReportContextChanged(this);
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        try
        {
            ProcessMap.UpdateNodes(await _repo.GetProcessesAsync());
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the sessions", ex);
            throw;
        }

        // The waits only while they are shown: the map and the grid don't need them.
        if (_view == ProcessesView.Waiting)
        {
            try
            {
                _waits = (await _repo.GetActiveWaitsAsync()).ToList();
            }
            catch (Exception ex)
            {
                ViewModel.ShowError("the waiting tasks", ex);
                throw;
            }
            ShowWaits();
        }
        else
        {
            // Out of date once another view is on screen: read again when Waiting is.
            _waits = null;
        }
        ShowVerdict();
    }

    // The verdict of the view on screen, from what was read last.
    private void ShowVerdict()
    {
        if (ViewModel is null)
            return;   // during InitializeComponent
        if (_view == ProcessesView.Waiting)
        {
            if (_waits is not null)
                ViewModel.ShowWaits(_waits, _blocking);
        }
        else if (ProcessMap.HasSnapshot)
        {
            ViewModel.Show(ProcessMap.Snapshot, _blocking);
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
        WaitingGrid.ItemsSource = WaitsShown;
        _shell.ReportContextChanged(this);
    }

    // ?? UI event handlers ?????????????????????????????????????????????
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is ComboBoxItem item && item.Tag is int sec)
        {
            _intervalSec = sec;
            ViewModel.ReadEvery(TimeSpan.FromSeconds(sec), countdown: true);
            SaveRefreshChoice();
            _shell.ReportContextChanged(this);
        }
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Stop";
        ViewModel.SetPaused(false);
        SaveRefreshChoice();
        _shell.ReportContextChanged(this);
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Start";
        ViewModel.SetPaused(true);
        SaveRefreshChoice();
        _shell.ReportContextChanged(this);
    }
}
