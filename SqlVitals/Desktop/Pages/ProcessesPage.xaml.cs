using System.ComponentModel;
using System.Windows.Controls;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Processes, bound to <see cref="ProcessesViewModel"/> (#186). The map and the grid are
/// <see cref="ProcessMapControl"/>, which draws the view model's snapshot; the code-behind is the
/// glue to it: the map or the grid, its grouping, a blocking chain's focus (#145), the session it
/// selects in the details drawer (#147), and the verdict's actions that act on it.
/// </summary>
public partial class ProcessesPage : Page, IPageContext, IFocusPage, IDetailsSource
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
        // Another session chosen: the alert's selection is over, and the banner says so no more.
        if (_transactionSession is { } named && ProcessMap.RevealedTransaction is { } revealed && revealed.Named == named
            && sessionId != (revealed.Holder ?? named))
        {
            _transactionSession = null;
            ViewModel.MapContextChanged();
        }

        if (node is null)
        {
            Details = new($"Session {sessionId}", null,
                          $"Session {sessionId} has gone since it was selected: it ended or disconnected. Select another session.");
        }
        else
        {
            _sessionView ??= new SessionDetailsView(ViewModel.SessionPlanAsync, ViewModel.OpenWaitsAsync);
            _sessionView.Show(node, ProcessMap.Snapshot);
            Details = new($"Session {sessionId}", _sessionView, SelectHint);
        }
        DetailsChanged?.Invoke(open);
    }

    // ── Evidence (#145): a blocking alert's chain is the map's, a session's waits the view model's ──
    public void ShowFocus(PageFocus focus)
    {
        _transactionSession = null;
        switch (focus.Filter)
        {
            case { Kind: FocusKind.BlockingChain }:
                ViewModel.ShowChainView();
                ProcessMap.FocusChain((int?)focus.Filter.Id);
                break;
            case { Kind: FocusKind.Session, Id: { } id }:
                ViewModel.FocusOnSession((int)id);
                break;
            // An open transaction (#252): its session on the map or the grid, with its details. A
            // sleeping session waits on nothing, so Waiting would show nothing of it.
            case { Kind: FocusKind.Transaction, Id: { } id }:
                _transactionSession = (int)id;
                ViewModel.ShowChainView();
                if (ProcessMap.IsChainFocused)
                    ProcessMap.ClearChainFocus();
                ProcessMap.RevealTransaction((int)id);
                break;
        }
    }

    public void ClearFocus()
    {
        _transactionSession = null;
        ViewModel.ClearSession();
        if (ProcessMap.IsChainFocused)
            ProcessMap.ClearChainFocus();
    }

    // The session an open transaction alert opened the page on (#252); nothing is hidden for it.
    // Forgotten once another session is chosen.
    private int? _transactionSession;

    // What the banner says of it, once the snapshot has been read: null before.
    private string? TransactionNote(int named) => ProcessMap.RevealedTransaction switch
    {
        { Named: var n } when n != named      => null,
        { Holder: { } holder } when holder == named => $"session {named} selected, with its details",
        { Holder: { } holder }                => $"session {named} holds no transaction now: session {holder} selected, which holds the oldest open",
        { }                                   => $"session {named} holds no transaction now, and no other session holds one",
        null                                  => null,
    };

    // What the view on screen shows of its rows: the session's waits on Waiting, the chain on the
    // map or the grid, the session holding an open transaction selected among the rest. A
    // session's filter is kept while another view is shown, for Show all.
    public string? FocusNote => ViewModel.SessionShown
        ? ViewModel.SessionNote
        : _transactionSession is { } held ? TransactionNote(held)
        : !ProcessMap.IsChainFocused || !ProcessMap.HasSnapshot ? null
        : ProcessMap.FocusedHead is { } head ? $"showing the blocking chain of session {head} only"
        : "nothing is blocked now";

    public string? ShowAllText => ViewModel.SessionShown
        ? ViewModel.SessionShowAllText
        : ProcessMap.IsChainFocused
            ? ProcessMap.HasSnapshot ? $"Show all {ProcessMap.SessionCount:N0} sessions" : "Show all sessions"
        : ViewModel.HasSession ? "Show all" : null;

    // Live while auto-refresh is on; the bar's Pause and Resume are its Stop and Start (#140), and
    // pause the view model's timer (#169).
    public PageKind Kind => PageKind.Live;
    public TimeSpan? LiveInterval => TimeSpan.FromSeconds(ViewModel.IntervalSeconds);
    public bool CanPause => true;
    public bool IsPaused => ViewModel.IsPaused;
    public void SetPaused(bool paused) => ViewModel.AutoRefresh = !paused;

    internal ProcessesViewModel ViewModel { get; }

    /// <summary>The view on screen.</summary>
    internal ProcessesView View => ViewModel.View;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public ProcessesPage(ProcessesViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;

        ProcessMap.GroupByLogin = ViewModel.GroupByLogin;
        ShowMapOrGrid();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessesViewModel.View))
                ShowMapOrGrid();
        };
        ViewModel.GoToSession          += ProcessMap.RevealSession;
        ViewModel.ChainsOnly           += ProcessMap.ShowChainsOnly;
        ProcessMap.GroupByLoginChanged += (_, _) => ViewModel.GroupByLogin = ProcessMap.GroupByLogin;
        ProcessMap.FocusChanged        += (_, _) => ViewModel.MapContextChanged();
        ProcessMap.SessionSelected     += ShowSession;
    }

    // The map or the grid; under Waiting, the one last shown is kept.
    private void ShowMapOrGrid()
    {
        if (ViewModel.View != ProcessesView.Waiting)
            ProcessMap.ShowGrid = ViewModel.View == ProcessesView.Grid;
    }
}
