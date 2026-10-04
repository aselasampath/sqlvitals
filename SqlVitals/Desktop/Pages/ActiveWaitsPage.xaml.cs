using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages;

public partial class ActiveWaitsPage : System.Windows.Controls.Page, IRefreshable, IPageContext, IFocusPage
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;

    // ── A session from Processes (#145): its waits and those of the sessions it blocks ──
    private int? _session;
    private List<ActiveWait>? _waits;

    public ActiveWaitsPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        _waits = (await _repo.GetActiveWaitsAsync()).ToList();
        ShowWaits();
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
