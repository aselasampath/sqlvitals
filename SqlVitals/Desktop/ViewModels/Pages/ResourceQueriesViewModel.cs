using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Resource Queries (#186): the plan cache's costliest queries by logical reads, by CPU and by the
/// highest reads, as many as the row limit asks, each with its plan. The verdict names the query
/// that uses the most CPU and the one that reads the most (#149). Opened from a CPU alert (#145), the
/// By CPU tab lists only the queries last run since the alert's window started.
/// </summary>
/// <param name="serverOffset">The server's clock from UTC, to compare the plan cache's last
/// execution times with an alert's; this PC's when not known.</param>
public sealed partial class ResourceQueriesViewModel(IResourceQueryRepository repo, IDialogService dialogs, TimeSpan? serverOffset = null)
    : PageViewModel
{
    /// <summary>The tabs, in order.</summary>
    public enum Tab { Reads, Cpu, HighestReads }

    private readonly TimeSpan _serverOffset = serverOffset ?? TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);

    /// <summary>The row limits offered.</summary>
    public IReadOnlyList<int> TopNChoices { get; } = [10, 20, 50, 100, 200];

    /// <summary>How many queries each tab lists; another reads again.</summary>
    [ObservableProperty]
    private int _topN = 20;

    // Once the page has read, a new row limit reads again; before, the first read uses it.
    private bool _read;

    partial void OnTopNChanged(int value)
    {
        if (_read)
            _ = RereadAsync();
    }

    /// <summary>The tab shown: the verdict's actions and a CPU alert pick one.</summary>
    [ObservableProperty]
    private int _selectedTab;

    public GridRows<ResourceIntensiveQuery> ByReads { get; } = [];

    /// <summary>The queries by CPU: only those run since a CPU alert's window started, while it is set.</summary>
    public GridRows<ResourceIntensiveQuery> ByCpu { get; } = [];

    public GridRows<ResourceIntensiveQuery> ByHighestReads { get; } = [];

    // Each tab's selected query, kept across a refresh by its text, database and object.
    [ObservableProperty]
    private ResourceIntensiveQuery? _selectedByReads;

    [ObservableProperty]
    private ResourceIntensiveQuery? _selectedByCpu;

    [ObservableProperty]
    private ResourceIntensiveQuery? _selectedByHighestReads;

    /// <summary>A query to show on the tab selected: the verdict's Show it selected it. The view scrolls to it and opens the drawer.</summary>
    public event Action<ResourceIntensiveQuery>? ShowRow;

    // Every query by CPU read last, before a CPU alert's window filters them.
    private IReadOnlyList<ResourceIntensiveQuery>? _byCpu;

    // A CPU alert's evidence (#145): the By CPU tab, with only the queries last run since the
    // window's start, on the server's clock like the plan cache's times.
    private bool _focused;
    private DateTime? _cpuSinceUtc;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        _read = true;
        var (byReads, byCpu, byHighestReads) = await repo.GetResourceIntensiveQueriesAsync(TopN, cancel);
        var reads = byReads.ToList();
        _byCpu    = byCpu.ToList();
        Verdict   = ResourceQueriesVerdict.From(_byCpu, reads);

        SelectedByReads        = Refill(ByReads, reads, SelectedByReads);
        SelectedByHighestReads = Refill(ByHighestReads, byHighestReads.ToList(), SelectedByHighestReads);
        ShowCpu();
    }

    protected override void ShowLoadError(Exception exception) => ShowError("the plan cache's query statistics", exception);

    // The grid clears its selection as the rows are replaced: the same query is selected again, if it is still there.
    private static ResourceIntensiveQuery? Refill(GridRows<ResourceIntensiveQuery> rows, List<ResourceIntensiveQuery> read, ResourceIntensiveQuery? selected)
    {
        var key = selected is null ? null : Key(selected);
        rows.Replace(read);
        return key is null ? null : read.FirstOrDefault(q => Key(q) == key);
    }

    private static (string?, string?, string?)? Key(ResourceIntensiveQuery q) => (q.QueryText, q.DatabaseName, q.ObjectName);

    private List<ResourceIntensiveQuery> CpuShown =>
        _cpuSinceUtc is { } sinceUtc
            ? (_byCpu ?? []).Where(q => q.LastExecutionTime >= DateTime.SpecifyKind(sinceUtc + _serverOffset, DateTimeKind.Unspecified)).ToList()
            : (_byCpu ?? []).ToList();

    private void ShowCpu()
    {
        if (_byCpu is null)
            return;
        SelectedByCpu = Refill(ByCpu, CpuShown, SelectedByCpu);
        ReportContextChanged();
    }

    // ── A CPU alert's evidence (#145) ─────────────────────────────────────

    /// <summary>Lists only the queries by CPU last run since <paramref name="fromUtc"/>, on the By CPU tab; null lists them all again.</summary>
    public void FocusOnCpuSince(DateTime? fromUtc)
    {
        _focused     = true;
        _cpuSinceUtc = fromUtc;
        SelectedTab  = (int)Tab.Cpu;
        ShowCpu();
    }

    /// <summary>Show all: every query by CPU again.</summary>
    public void ClearFocus()
    {
        _cpuSinceUtc = null;
        ShowCpu();
    }

    public string? FocusNote => !_focused || _byCpu is null ? null
        : _cpuSinceUtc is { } since
            ? $"showing the {CpuShown.Count} of the top {_byCpu.Count} queries by CPU last run since {since.ToLocalTime():HH:mm}"
            : "showing the queries that used the most CPU";

    public string? ShowAllText => _cpuSinceUtc is null ? null : _byCpu is null ? "Show all" : $"Show all {_byCpu.Count} queries";

    // ── The verdict's actions ─────────────────────────────────────────────

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ResourceQueriesVerdict.ShowCpu:   ShowTop(Tab.Cpu, ByCpu, q => q.TotalCPUTime);         break;
            case ResourceQueriesVerdict.ShowReads: ShowTop(Tab.Reads, ByReads, q => q.TotalLogicalReads); break;
        }
    }

    // The tab, its top row selected, in view, and in the drawer.
    private void ShowTop(Tab tab, GridRows<ResourceIntensiveQuery> rows, Func<ResourceIntensiveQuery, long> by)
    {
        SelectedTab = (int)tab;
        if (rows.OrderByDescending(by).FirstOrDefault() is not { } top)
            return;
        if (tab == Tab.Cpu)
            SelectedByCpu = top;
        else
            SelectedByReads = top;
        ShowRow?.Invoke(top);
    }

    // ── Plans ─────────────────────────────────────────────────────────────

    /// <summary>The procedure's plan when the row names one, else the statement's cached plan.</summary>
    public async Task<string?> PlanOfAsync(ResourceIntensiveQuery query, CancellationToken cancel = default) =>
        !string.IsNullOrEmpty(query.ObjectName)
            ? await repo.GetObjectQueryPlanAsync(query.ObjectName, cancel) ?? query.QueryPlan
            : query.QueryPlan;

    // Cancels the plans being read for View Plan: the page left.
    private Action? _cancelPlans;

    /// <summary>
    /// A row's View Plan: its plan in a window. Each row's button reads on its own, as before; a
    /// plan still being read when the page is left is dropped, not opened over another page.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ViewPlanAsync(ResourceIntensiveQuery? query)
    {
        if (query is null)
            return;
        using var read = new CancellationTokenSource();
        Action cancel = read.Cancel;
        _cancelPlans += cancel;
        try
        {
            var plan = await PlanOfAsync(query, read.Token);
            if (!read.IsCancellationRequested)
                dialogs.ShowQueryPlan(plan, query.QueryText);
        }
        catch (Exception) when (read.IsCancellationRequested)
        {
            // Left: nothing to show.
        }
        catch (Exception ex)
        {
            AppLog.Error("Resource Queries: reading a plan failed", ex);
            dialogs.Warn($"Failed to retrieve execution plan:\n{ex.Message}", "Plan Error");
        }
        finally
        {
            _cancelPlans -= cancel;
        }
    }

    protected override void OnLeaving()
    {
        _cancelPlans?.Invoke();
        _cancelPlans = null;
    }
}
