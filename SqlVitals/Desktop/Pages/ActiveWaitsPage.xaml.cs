using System.Windows;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Pages;

public partial class ActiveWaitsPage : System.Windows.Controls.Page, IRefreshable, IPageContext, IFocusPage
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#149): the requests blocked and the longest wait, or what they wait on.
    internal ActiveWaitsViewModel ViewModel { get; }

    // The active connection's blocking threshold, which colours the verdict as it does the health dot.
    private readonly HealthThreshold? _blocking;

    // ── A session from Processes (#145): its waits and those of the sessions it blocks ──
    private int? _session;
    private List<ActiveWait>? _waits;

    /// <param name="settings">Where the verdict's fold is remembered, and the blocking threshold; none in some tests.</param>
    public ActiveWaitsPage(IWaitStatsRepository repo, ConnectionSettingsService? settings = null)
    {
        _repo = repo;
        InitializeComponent();

        if (settings is not null)
        {
            var store = settings.Load();
            _blocking = (store.Active is { } active ? store.ThresholdsFor(active) : store.HealthThresholds).Get(HealthIndicator.Blocking);
        }
        ViewModel = new ActiveWaitsViewModel(openProcesses: _ => OpenProcesses());
        ViewModel.Attach(Shell);
        Shell.Folds = settings;
    }

    public async System.Threading.Tasks.Task RefreshAsync()
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
        ViewModel.Show(_waits, _blocking);
        ShowWaits();
    }

    // Processes on the chain waiting longest (#145).
    private async void OpenProcesses()
    {
        if (Window.GetWindow(this) is MainWindow main)
            await main.OpenLinkAsync("Processes", new FocusFilter(FocusKind.BlockingChain), "Active Waits");
    }

    public void ShowFocus(PageFocus focus)
    {
        if (focus.Filter is not { Kind: FocusKind.Session, Id: { } id })
            return;
        _session = (int)id;
        ShowWaits();
    }

    public void ClearFocus()
    {
        _session = null;
        ShowWaits();
    }

    public string? FocusNote => _session is not { } id || _waits is null ? null
        : WaitsShown.Count == 0 ? $"session {id} isn't waiting, or blocking anything, now"
        : $"showing session {id} and the sessions it blocks only";

    public string? ShowAllText => _session is null ? null : _waits is null ? "Show all" : $"Show all {_waits.Count} waiting tasks";

    private List<ActiveWait> WaitsShown =>
        _session is { } id
            ? (_waits ?? []).Where(w => w.SessionId == id || w.BlockingSessionId == id).ToList()
            : _waits ?? [];

    private void ShowWaits()
    {
        if (_waits is null)
            return;
        ActiveGrid.ItemsSource = WaitsShown;
        MainWindow.ReportContextChanged(this);
    }
}
