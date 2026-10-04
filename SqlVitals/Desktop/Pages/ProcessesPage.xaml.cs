using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ProcessesPage : Page, IRefreshable, IPageContext, IFocusPage, IDetailsSource
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
            _sessionView ??= new SessionDetailsView(_repo.GetSessionPlanAsync, OpenActiveWaits);
            _sessionView.Show(node, ProcessMap.Snapshot);
            Details = new($"Session {sessionId}", _sessionView, SelectHint);
        }
        DetailsChanged?.Invoke(open);
    }

    // Active Waits filtered to the session and the sessions it blocks (#145).
    private async void OpenActiveWaits(int sessionId)
    {
        if (Window.GetWindow(this) is MainWindow main)
            await main.OpenLinkAsync("ActiveWaits", new FocusFilter(FocusKind.Session, sessionId), "Processes");
    }

    // ── A blocking alert's evidence (#145): one chain, until Show all ─────
    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter.Kind == FocusKind.BlockingChain)
            ProcessMap.FocusChain((int?)focus.Filter.Id);
    }

    public void ClearFocus() => ProcessMap.ClearChainFocus();

    public string? FocusNote => !ProcessMap.IsChainFocused || !ProcessMap.HasSnapshot ? null
        : ProcessMap.FocusedHead is { } head ? $"showing the blocking chain of session {head} only"
        : "nothing is blocked now";

    public string? ShowAllText => !ProcessMap.IsChainFocused ? null
        : ProcessMap.HasSnapshot ? $"Show all {ProcessMap.SessionCount:N0} sessions"
        : "Show all sessions";

    // Live while auto-refresh is on; the bar's Pause and Resume are its Stop and Start (#140).
    public PageKind Kind => PageKind.Live;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(_intervalSec);
    public bool CanPause => true;
    public bool IsPaused => BtnAutoRefresh.IsChecked != true;
    public void SetPaused(bool paused) => BtnAutoRefresh.IsChecked = !paused;

    private readonly IWaitStatsRepository _repo;
    private readonly ConnectionSettingsService _settings;

    // The page template's verdict (#146): the head blockers and how long they have blocked.
    internal ProcessesViewModel ViewModel { get; }

    // The active connection's blocking threshold, which colours the verdict as it does the health dot.
    private readonly HealthThreshold _blocking;

    // ?? Auto-refresh ??????????????????????????????????????????????????
    private readonly DispatcherTimer _timer = new();
    private int _intervalSec   = ConnectionStore.DefaultProcessesRefreshSeconds;
    private int _remainingSec  = 0;
    private bool _refreshing   = false;
    private bool _restoring    = true;   // no saving while the saved choice is applied, or while leaving

    // Interval choices: 5 s increments up to 120 s
    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray(); // 5,10,15,...,120

    public ProcessesPage(IWaitStatsRepository repo, ConnectionSettingsService settings)
    {
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
        ViewModel = new ProcessesViewModel(goToSession: ProcessMap.ShowSession, chainsOnly: ProcessMap.ShowChainsOnly);
        ViewModel.Attach(Shell);
        Shell.Folds = settings;
        CmbInterval.SelectedIndex = Math.Max(0, Array.IndexOf(Intervals, store.ProcessesRefreshSeconds));

        // So is the view, and the grid's grouping (#116).
        ProcessMap.GroupByLogin = store.ProcessesGroupByLogin;
        if (store.ProcessesView == ProcessesView.Grid) BtnGrid.IsChecked = true;
        ProcessMap.GroupByLoginChanged += (_, _) => SaveViewChoice();
        ProcessMap.FocusChanged        += (_, _) => MainWindow.ReportContextChanged(this);
        ProcessMap.SessionSelected     += ShowSession;

        // Wire timer (ticks every second for the countdown display)
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick    += Timer_Tick;

        Loaded += (_, _) =>
        {
            if (store.ProcessesAutoRefresh) BtnAutoRefresh.IsChecked = true;
            _restoring = false;
        };

        // Stop timer when page is unloaded (navigation away), keeping the saved choice
        Unloaded += (_, _) =>
        {
            _restoring = true;
            StopTimer();
        };
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
            _settings.SetProcessesView(ProcessMap.ShowGrid ? ProcessesView.Grid : ProcessesView.Map, ProcessMap.GroupByLogin);
        }
        catch (Exception ex)
        {
            AppLog.Error("Processes: saving the view failed", ex);
        }
    }

    // Both views draw the snapshot already loaded: switching doesn't query the server.
    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (ProcessMap is null) return;   // while the XAML loads
        ProcessMap.ShowGrid = BtnGrid.IsChecked == true;
        SaveViewChoice();
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
        ViewModel.Show(ProcessMap.Snapshot, _blocking);
    }

    // ?? Timer logic ???????????????????????????????????????????????????
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
        if (BtnAutoRefresh.IsChecked == true)
            BtnAutoRefresh.IsChecked = false;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSec--;
        UpdateCountdown();

        if (_remainingSec > 0 || _refreshing) return;

        _refreshing = true;
        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            MainWindow.ReportBackgroundError(this, "Processes", ex);
        }
        finally
        {
            _refreshing   = false;
            _remainingSec = _intervalSec;   // reset countdown
            UpdateCountdown();
        }
    }

    private void UpdateCountdown()
    {
        TxtCountdown.Text = _remainingSec >= 60
            ? $"{_remainingSec / 60}:{_remainingSec % 60:00}"
            : $"{_remainingSec}s";
    }

    // ?? UI event handlers ?????????????????????????????????????????????
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is ComboBoxItem item && item.Tag is int sec)
        {
            _intervalSec  = sec;
            _remainingSec = sec;
            UpdateCountdown();
            SaveRefreshChoice();
            MainWindow.ReportContextChanged(this);
        }
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Stop";
        StartTimer();
        SaveRefreshChoice();
        MainWindow.ReportContextChanged(this);
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Start";
        _timer.Stop();
        TxtCountdown.Text = "--";
        SaveRefreshChoice();
        MainWindow.ReportContextChanged(this);
    }
}
