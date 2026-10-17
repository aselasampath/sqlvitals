using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Plan Cache Health (#185): the summary as cards, then on tabs the queries with many cached plans,
/// key lookups in hot plans, live cardinality misestimates and skewed parallelism, plan guides, and
/// the plans converting implicitly (the Implicit Conv. page until #178). The verdict is the worst
/// of them (#149).
/// </summary>
/// <param name="view">The tab to open on, from the route (#178): <see cref="NavMenu.ImplicitConversionsView"/>
/// for a link to the Implicit Conv. page; null for the first.</param>
public sealed partial class PlanCacheHealthViewModel(IPlanCacheHealthRepository repo, IResourceQueryRepository queries, string? view = null)
    : PageViewModel
{
    /// <summary>The tab shown; the verdict's actions and an old Implicit Conv. link pick one.</summary>
    [ObservableProperty]
    private int _selectedTab = view == NavMenu.ImplicitConversionsView ? PlanCacheHealthVerdict.ImplicitConversionsTab : PlanCacheHealthVerdict.MultiPlanTab;

    /// <summary>The summary: whether the cache was erased, its plans, multi-plan queries, plan guides and the severity.</summary>
    [ObservableProperty]
    private IReadOnlyList<KpiCard> _summary = [];

    /// <summary>Folded, the summary says it in one line (#149).</summary>
    [ObservableProperty]
    private string? _foldedSummary;

    /// <summary>The plan cache was erased recently: the banner says the history may be missing.</summary>
    [ObservableProperty]
    private bool _cacheErased;

    public GridRows<MultiPlanQuery> MultiPlanQueries { get; } = [];

    public GridRows<KeyLookupQuery> KeyLookups { get; } = [];

    public GridRows<CardinalityMisestimate> Misestimates { get; } = [];

    public GridRows<SkewedParallelQuery> SkewedQueries { get; } = [];

    public GridRows<PlanGuide> PlanGuides { get; } = [];

    /// <summary>The plans converting implicitly; empty when they couldn't be read.</summary>
    public GridRows<ImplicitConversion> Conversions { get; } = [];

    /// <summary>The selected plan converting implicitly; kept across a refresh by its database, object, text and size.</summary>
    [ObservableProperty]
    private ImplicitConversion? _selectedConversion;

    /// <summary>Why the conversions' tab is empty: their read failed and the rest of the page didn't (#178).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsConversionsProblem))]
    private string? _conversionsProblem;

    public bool ShowsConversionsProblem => ConversionsProblem is not null;

    /// <summary>A row to scroll into view: the verdict's Show it selected it.</summary>
    public event Action<object>? ShowRow;

    // The plan cache's largest queries have a longer limit of their own.
    protected override TimeSpan? LongestQuery => ExtendedQueries;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        // The checks, and at the same time the plans converting implicitly for their tab (#178).
        // When the checks fail the page shows nothing new, so the conversions' search, the slow
        // one, is stopped, and waited for, rather than left running on the server.
        using var both  = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var converting  = ReadConversionsAsync(both.Token);
        (PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>, IEnumerable<CardinalityMisestimate>,
         IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>) health;
        try
        {
            health = await repo.GetPlanCacheHealthAsync(cancel);
        }
        catch
        {
            await both.CancelAsync();
            try { await converting; }
            catch (OperationCanceledException) { }
            throw;
        }
        var (summary, multiPlan, keyLookups, cardinality, skewed, guides) = health;
        var (conversions, problem) = await converting;
        var lookups = keyLookups.ToList();

        Verdict = PlanCacheHealthVerdict.From(summary, lookups.Count, conversions);
        ShowSummary(summary);
        MultiPlanQueries.Replace(multiPlan);
        KeyLookups.Replace(lookups);
        Misestimates.Replace(cardinality);
        SkewedQueries.Replace(skewed);
        PlanGuides.Replace(guides);

        var selected = SelectedConversion is { } row ? Key(row) : null;
        Conversions.Replace(conversions ?? []);
        SelectedConversion = selected is { } key ? Conversions.FirstOrDefault(c => Key(c) == key) : null;
        ConversionsProblem = problem;
    }

    // The plan has no handle here: its database, object, text and size tell it from another plan of the same text.
    private static (string?, string?, string?, long)? Key(ImplicitConversion c) => (c.DatabaseName, c.ObjectName, c.QueryText, c.PlanSizeKB);

    // The plans converting implicitly, or why they couldn't be read. A plan XML search can time out
    // on a very large plan cache: the tab says so, and the rest of the page still shows.
    private async Task<(List<ImplicitConversion>? Rows, string? Problem)> ReadConversionsAsync(CancellationToken cancel)
    {
        try
        {
            return ((await queries.GetImplicitConversionsAsync(cancel)).ToList(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("Plan Cache Health: reading the implicit conversions failed", ex);
            return (null, $"Couldn't read the implicit conversions: {ErrorStatus.Format(ex)} Refresh to try again.");
        }
    }

    private void ShowSummary(PlanCacheHealthSummary summary)
    {
        var cacheStatus = summary.PlanCacheErasedRecently ? "ERASED" : "OK";
        var severity    = summary.Severity switch
        {
            "CRITICAL" => KpiCard.Tones.Danger,
            "WARNING"  => KpiCard.Tones.Warning,
            _          => KpiCard.Tones.Success,
        };

        CacheErased = summary.PlanCacheErasedRecently;
        Summary =
        [
            new("Plan Cache",         cacheStatus,                             summary.PlanCacheErasedRecently ? KpiCard.Tones.Danger : KpiCard.Tones.Success,
                "Whether DBCC FREEPROCCACHE was run recently or the server restarted in the last 24 hours. ERASED means historical plan data may be missing — wait for plans to repopulate before investigating."),
            new("Total Plans",        $"{summary.TotalCachedPlans:N0}",        KpiCard.Tones.Accent,
                "Total number of plans currently in the procedure cache (sys.dm_exec_cached_plans). Very high counts (> 100k) may indicate hash bucket pressure. Use 'Optimize for Ad-hoc Workloads' to reduce single-use plan accumulation."),
            new("Multi-Plan Queries", $"{summary.MultiPlanQueryCount:N0}",     summary.MultiPlanQueryCount > 3 ? KpiCard.Tones.Warning : KpiCard.Tones.Success,
                "Number of query hashes that have more than 5 distinct cached plans. Each extra plan represents a separate compilation — a sign of ad-hoc SQL or forced recompilation. Target: 0."),
            new("Plan Guides",        $"{summary.PlanGuidesEnabled:N0}",       summary.PlanGuidesEnabled > 0 ? KpiCard.Tones.Warning : KpiCard.Tones.Success,
                "Number of active plan guides in sys.plan_guides. Plan guides freeze execution plans; they can become stale after schema changes, index changes, or SQL Server upgrades. Review all listed guides."),
            new("Severity",           summary.Severity,                        severity,
                "Overall plan cache health signal: CRITICAL = cache was erased or > 10 multi-plan queries; WARNING = multi-plan queries detected or plan guides present; OK = no issues found."),
        ];
        FoldedSummary = string.Join(" · ",
            $"Plan cache {cacheStatus}", $"{summary.TotalCachedPlans:N0} plans", $"{summary.MultiPlanQueryCount:N0} multi-plan queries",
            $"{summary.PlanGuidesEnabled:N0} plan guides", summary.Severity);
    }

    protected override void ShowLoadError(Exception exception) => ShowError("the plan cache", exception);

    protected override void OnAction(VerdictAction action)
    {
        switch (action)
        {
            case { Key: PlanCacheHealthVerdict.ShowTab, Id: { } tab }:
                SelectedTab = (int)tab;
                break;
            case { Key: PlanCacheHealthVerdict.ShowWorstConversion }:
                ShowWorstConversion();
                break;
        }
    }

    // The verdict's Show it: the Implicit Conversions tab, its most used plan selected and in view.
    private void ShowWorstConversion()
    {
        SelectedTab = PlanCacheHealthVerdict.ImplicitConversionsTab;
        if (Conversions.MaxBy(c => c.ExecutionCount) is not { } worst)
            return;
        SelectedConversion = worst;
        ShowRow?.Invoke(worst);
    }
}
