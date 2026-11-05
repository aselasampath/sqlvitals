using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Live SP Trace, bound to <see cref="SpTraceViewModel"/> (#149, #188): the code-behind fills the
/// details drawer with the call selected, keeps the newest call or the slowest in view, and paints
/// the trace button and the mode badge in their theme colours.
/// </summary>
public partial class SpTracePage : Page, IPageContext, IDetailsSource
{
    // ── The details drawer (#149): the selected call ─────────────────────────
    // Its figures, its statement with Copy, and its procedure's cached plan, drawn here or in a
    // window as View Plan opened it. The pane below the calls moved here.
    private const string SelectHint = "Select a call to see its statement, its figures and its procedure's plan.";

    private RowDetailsView? _detailsView;

    public DrawerContent Details { get; private set; } = new("Call details", null, SelectHint);

    public event Action<bool>? DetailsChanged;

    internal RowDetailsView? DetailsView => _detailsView;

    // The call the drawer shows: a poll that keeps it selected doesn't build the drawer again,
    // which would scroll its statement back and close the plan opened.
    private SpTraceCall? _shown;

    private void ShowCall(object row, bool open)
    {
        if (row is not SpTraceCall shown)
            return;
        if (ReferenceEquals(shown, _shown) && !open)
            return;
        Follow(shown);
        var call = shown.Event;
        List<DrawerPlan> plans = [];
        if (call.ObjectName is { Length: > 0 } objectName)
            plans.Add(new("Cached plan of the procedure", () => ViewModel.PlanOfAsync(objectName), $"Execution plan · {objectName}"));
        _detailsView ??= new RowDetailsView();
        _detailsView.Show(RowDetails.Rows(call, shown.Nesting), string.IsNullOrEmpty(call.ObjectName) ? "Statement" : $"Statement — {call.ObjectName}",
                          call.Statement, plans, isQuery: true);
        Details = new(string.IsNullOrEmpty(call.ObjectName) ? "Call" : call.ObjectName, _detailsView, SelectHint);
        DetailsChanged?.Invoke(open);
    }

    // The call shown, followed: its caller can come in on a later poll (#238), and the drawer's
    // rows say it then, leaving its statement and plan as they are.
    private void Follow(SpTraceCall? call)
    {
        _shown?.PropertyChanged -= OnShownChanged;
        _shown = call;
        _shown?.PropertyChanged += OnShownChanged;
    }

    private void OnShownChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpTraceCall.Nesting) && _shown is { } call)
            _detailsView?.ShowRows(RowDetails.Rows(call.Event, call.Nesting));
    }

    // Cleared: the drawer says to select a call again.
    private void ClearDetail()
    {
        Follow(null);
        Details = new("Call details", null, SelectHint);
        DetailsChanged?.Invoke(false);
    }

    // The trace's events as they arrive, once started (#140); Start and Stop stay on the page.
    public PageKind Kind => PageKind.Live;

    // Polled every few seconds on the view model's timer (#169) while the trace runs; stopped, nothing moves.
    public TimeSpan? LiveInterval => ViewModel.PollInterval;

    public bool IsPaused => !ViewModel.IsPolling;

    internal SpTraceViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public SpTracePage(SpTraceViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        DrawerParts.FollowSelection(CallsGrid, ShowCall);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.ScrollTo += CallsGrid.ScrollIntoView;
        ViewModel.ShowRow  += call =>
        {
            CallsGrid.ScrollIntoView(call);
            ShowCall(call, open: true);
        };
        ViewModel.Cleared += ClearDetail;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SpTraceViewModel.IsTracing) or nameof(SpTraceViewModel.ModeTone))
                Paint();
        };
        Paint();
    }

    // Start is green, Stop amber; the mode badge in its tone: the colour, its outline and a wash of it behind.
    private void Paint()
    {
        var trace = ViewModel.IsTracing ? "Warning" : "Success";
        BtnTrace.SetResourceReference(Control.ForegroundProperty, trace);
        BtnTrace.SetResourceReference(Control.BorderBrushProperty, trace);
        BtnTrace.Background = Wash(trace);

        var mode = ViewModel.ModeTone;
        TxtModeBadge.SetResourceReference(TextBlock.ForegroundProperty, mode);
        ModeBadge.SetResourceReference(Border.BorderBrushProperty, mode);
        ModeBadge.Background = Wash(mode);
    }

    private Brush Wash(string brushKey) => ThemeResources.Wash((Color)FindResource(brushKey + "Color"), 0x1A);
}
