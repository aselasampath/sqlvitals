using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Memory (#150, #187; the Memory Grants page until #178): the server's memory figures, a trend of
/// them while Resume (▶) in the top bar runs it, the top memory clerks and the memory grants. Read
/// once and on Refresh; while trending, the timer reads only the figures. The verdict names the
/// queries waiting for memory, or says how memory is used. A memory grants alert opens it on the
/// grants still waiting (#145), until Show all.
/// </summary>
public sealed partial class MemoryViewModel : PageViewModel
{
    /// <summary>How many readings the trend keeps.</summary>
    internal const int MaxSamples = 60;

    /// <summary>The tabs: the memory clerks, and the memory grants.</summary>
    internal const int ClerksTab = 0, GrantsTab = 1;

    private readonly IMemoryRepository _repo;

    // The grants read last; a focus only filters them again.
    private IReadOnlyList<MemoryGrant>? _grants;

    // A memory grants alert's evidence (#145): the grants still waiting, until Show all.
    private bool _pendingOnly;

    // The latest reading, for the trend's folded line.
    private MemorySnapshot? _last;

    public MemoryViewModel(IMemoryRepository repo)
    {
        _repo = repo;
        // Not trending until Resume (▶) in the top bar.
        SetPaused(true);
        ShowsCountdown  = true;
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        ShowTrendSummary();
        PropertyChanged += OnOwnPropertyChanged;
    }

    // Paused or resumed from the top bar: the trend's line and the bar follow.
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IsPaused))
            return;
        ShowTrendSummary();
        ReportContextChanged();
    }

    // ── The trend's interval ──────────────────────────────────────────────

    /// <summary>How often the figures are read while trending, in seconds.</summary>
    public IReadOnlyList<int> IntervalChoices { get; } = [5, 10, 30, 60];

    public IReadOnlyList<string> IntervalNames { get; } = ["5s", "10s", "30s", "60s"];

    [ObservableProperty]
    private int _intervalIndex = 1;

    public int IntervalSeconds => IntervalChoices[Math.Clamp(IntervalIndex, 0, IntervalChoices.Count - 1)];

    // Another interval: the wait for the next read starts again.
    partial void OnIntervalIndexChanged(int value)
    {
        RefreshInterval = TimeSpan.FromSeconds(IntervalSeconds);
        ShowTrendSummary();
        ReportContextChanged();
    }

    // ── The figures and the trend ─────────────────────────────────────────

    /// <summary>The memory figures, formatted; "--" until read.</summary>
    [ObservableProperty]
    private MemoryFigures _figures = MemoryFigures.None;

    /// <summary>Folded, the figures say it in one line (#150).</summary>
    [ObservableProperty]
    private string? _figuresSummary;

    /// <summary>Folded, the trend says it in one line (#150): the latest figures, and whether it is moving.</summary>
    [ObservableProperty]
    private string? _trendSummary;

    /// <summary>The readings the trend draws, oldest first; at most <see cref="MaxSamples"/>.</summary>
    public IReadOnlyList<MemorySnapshot> Samples => _samples;

    private readonly List<MemorySnapshot> _samples = [];

    /// <summary>A reading was added to <see cref="Samples"/> (and the oldest dropped past <see cref="MaxSamples"/>): the chart draws it.</summary>
    public event Action<MemorySnapshot>? Sampled;

    /// <summary>Nothing read yet: the chart says how to start the trend.</summary>
    [ObservableProperty]
    private bool _showsNoSamples = true;

    // ── The clerks and the grants ─────────────────────────────────────────

    /// <summary>The tab shown: <see cref="ClerksTab"/> or <see cref="GrantsTab"/>.</summary>
    [ObservableProperty]
    private int _tabIndex = ClerksTab;

    /// <summary>The 15 clerks holding the most memory, the most first.</summary>
    public GridRows<MemoryClerk> Clerks { get; } = [];

    /// <summary>The grants, the largest first; only the waiting ones while an alert's focus is on.</summary>
    public GridRows<MemoryGrant> Grants { get; } = [];

    /// <summary>The grant selected; kept across a refresh by its session and request.</summary>
    [ObservableProperty]
    private MemoryGrant? _selectedGrant;

    /// <summary>A grant to scroll into view: the verdict's Show the waiting grants selected it.</summary>
    public event Action<MemoryGrant>? ShowRow;

    // ── Reading ───────────────────────────────────────────────────────────

    protected override Task LoadAsync(CancellationToken cancel) => LoadAsync(PageRead.Refresh, cancel);

    // The timer reads only the figures for the trend; the grants and clerks are read on Refresh.
    protected override async Task LoadAsync(PageRead read, CancellationToken cancel)
    {
        _readingTimed = read == PageRead.Timed;
        if (_readingTimed)
        {
            var reading = await _repo.GetMemorySnapshotAsync(cancel);
            ShowFigures(reading);
            Verdict = MemoryVerdict.From(reading, null);   // the counter says whether queries wait
            return;
        }

        var snapshotRead = _repo.GetMemorySnapshotAsync(cancel);
        var grantsRead   = _repo.GetMemoryGrantsAsync(cancel);
        await Task.WhenAll(snapshotRead, grantsRead);
        var snapshot = await snapshotRead;
        var (grants, clerks, _) = await grantsRead;

        ShowFigures(snapshot);
        Clerks.Replace(clerks.OrderByDescending(c => c.TotalPagesMB).Take(15));
        _grants = grants.ToList();
        ShowGrants();
        Verdict = MemoryVerdict.From(snapshot, _grants);
    }

    // Which read is running: a failed trend reading is said in the top bar only, and the verdict
    // stays on the figures still shown, as it did before #187.
    private bool _readingTimed;

    protected override void ShowLoadError(Exception exception)
    {
        if (!_readingTimed)
            ShowError("the memory figures", exception);
    }

    private void ShowFigures(MemorySnapshot s)
    {
        Figures        = MemoryFigures.From(s);
        FiguresSummary = $"SQL Server {Format(s.TotalServerMemoryKB)} of {Format(s.TargetServerMemoryKB)} target · " +
                         $"buffer pool {Format(s.DatabaseCacheMemoryKB)} · plan cache {Format(s.SqlCacheMemoryKB)} · {s.SystemMemoryState}";

        _samples.Add(s);
        if (_samples.Count > MaxSamples)
            _samples.RemoveAt(0);
        _last          = s;
        ShowsNoSamples = false;
        ShowTrendSummary();
        Sampled?.Invoke(s);
    }

    private void ShowTrendSummary()
    {
        static double Gb(long kb) => Math.Round(kb / 1_048_576.0, 2);
        var reading = _last is { } s ? $"SQL Server {Gb(s.TotalServerMemoryKB):0.##} GB, grants {Gb(s.GrantedWorkspaceMemoryKB):0.##} GB · " : "";
        TrendSummary = reading + (!IsPaused ? $"every {IntervalSeconds} s" : "paused: Resume (▶) in the top bar trends it");
    }

    private void ShowGrants()
    {
        if (_grants is null)
            return;
        var selected = SelectedGrant;
        Grants.Replace((_pendingOnly ? _grants.Where(g => g.IsPending) : _grants).OrderByDescending(g => g.GrantedMemoryKB));
        SelectedGrant = selected is null ? null : Grants.FirstOrDefault(g => g.SessionId == selected.SessionId && g.RequestId == selected.RequestId);
        ReportContextChanged();
    }

    /// <summary>"512 KB", "88 MB", "12.5 GB".</summary>
    internal static string Format(long kb) =>
        kb >= 1_048_576 ? $"{kb / 1_048_576.0:N1} GB" :
        kb >= 1_024     ? $"{kb / 1_024:N0} MB"       :
                          $"{kb:N0} KB";

    // ── The verdict's actions ─────────────────────────────────────────────

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case MemoryVerdict.ShowWaiting: ShowWaiting();         break;
            case MemoryVerdict.Clerks:      TabIndex = ClerksTab;  break;
        }
    }

    // The verdict's Show the waiting grants: the Memory Grants tab, the longest waiter read selected.
    private void ShowWaiting()
    {
        TabIndex = GrantsTab;
        if (Grants.Where(g => g.IsPending).MaxBy(g => g.WaitForGrantMs) is not { } longest)
            return;
        SelectedGrant = longest;
        ShowRow?.Invoke(longest);
    }

    // ── A memory grants alert's evidence (#145) ───────────────────────────

    /// <inheritdoc cref="IFocusPage.ShowFocus"/>
    public void ShowFocus(PageFocus focus)
    {
        _pendingOnly = focus.Filter.Kind == FocusKind.PendingGrants;
        ShowGrants();
    }

    /// <inheritdoc cref="IFocusPage.ClearFocus"/>
    public void ClearFocus()
    {
        _pendingOnly = false;
        ShowGrants();
    }

    /// <inheritdoc cref="IFocusPage.FocusNote"/>
    public string? FocusNote => !_pendingOnly || _grants is null ? null
        : PendingCount switch
        {
            0     => "no query is waiting for memory now",
            1     => "showing the 1 grant waiting for memory only",
            var n => $"showing the {n} grants waiting for memory only",
        };

    /// <inheritdoc cref="IFocusPage.ShowAllText"/>
    public string? ShowAllText => !_pendingOnly ? null : _grants is null ? "Show all" : $"Show all {_grants.Count} grants";

    private int PendingCount => _grants?.Count(g => g.IsPending) ?? 0;

    /// <summary>The memory figures as the strip shows them.</summary>
    /// <param name="StateTone">The brush for the system memory state: "Success" for available, "Danger" for low, "Warning" otherwise; empty until read.</param>
    public sealed record MemoryFigures(string Physical, string Total, string Target, string Buffer, string PlanCache,
                                       string AvailablePhysical, string PageFile, string AvailablePageFile,
                                       string State, string StateTone, string Model)
    {
        public static readonly MemoryFigures None = new("--", "--", "--", "--", "--", "--", "--", "--", "--", "", "--");

        public static MemoryFigures From(MemorySnapshot s) => new(
            Format(s.PhysicalMemoryKB), Format(s.TotalServerMemoryKB), Format(s.TargetServerMemoryKB),
            Format(s.DatabaseCacheMemoryKB), Format(s.SqlCacheMemoryKB), Format(s.AvailablePhysicalKB),
            Format(s.TotalPageFileKB), Format(s.AvailablePageFileKB), s.SystemMemoryState,
            s.SystemMemoryState switch
            {
                var v when v.StartsWith("Available", StringComparison.OrdinalIgnoreCase) => "Success",
                var v when v.Contains("Low", StringComparison.OrdinalIgnoreCase)         => "Danger",
                _                                                                        => "Warning",
            },
            s.MemoryModel);
    }
}
