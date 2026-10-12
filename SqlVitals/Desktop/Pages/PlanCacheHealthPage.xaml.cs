using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

public partial class PlanCacheHealthPage : System.Windows.Controls.Page, ICodeBehindPage, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#149): the worst of what the page checks.
    internal PlanCacheHealthViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the summary's folds are remembered; none in some tests.</param>
    /// <param name="view">The tab to open on, from the route (#178): <see cref="NavMenu.ImplicitConversionsView"/>
    /// for a link to the Implicit Conv. page; null for the first.</param>
    public PlanCacheHealthPage(IWaitStatsRepository repo, ISectionFolds? folds = null, string? view = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new PlanCacheHealthViewModel(showTab: index => Tabs.SelectedIndex = index, showWorstConversion: ShowWorstConversion);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        if (view == NavMenu.ImplicitConversionsView)
            Tabs.SelectedItem = TabImplicitConversions;
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        // The checks, and at the same time the plans converting implicitly for their tab (#178).
        var read        = _repo.GetPlanCacheHealthAsync();
        var conversions = ReadConversionsAsync();
        try
        {
            await read;
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("the plan cache", ex);
            throw;
        }
        var (summary, multiPlan, keyLookups, cardinality, skewed, guides) = read.Result;
        var lookups = keyLookups.ToList();
        var converting = await conversions;
        ViewModel.Show(summary, lookups.Count, converting);
        ConvGrid.ItemsSource = converting ?? [];

        // ── KPI strip ─────────────────────────────────────────────────
        KpiStrip.Children.Clear();

        string cacheStatus     = summary.PlanCacheErasedRecently ? "ERASED" : "OK";
        string cacheColor      = summary.PlanCacheErasedRecently ? "Danger" : "Success";
        string severityColor   = summary.Severity switch
        {
            "CRITICAL" => "Danger",
            "WARNING"  => "Warning",
            _          => "Success"
        };

        AddKpi("Plan Cache",        cacheStatus,                               cacheColor,
               "Whether DBCC FREEPROCCACHE was run recently or the server restarted in the last 24 hours. ERASED means historical plan data may be missing — wait for plans to repopulate before investigating.");
        AddKpi("Total Plans",       $"{summary.TotalCachedPlans:N0}",          "Accent",
               "Total number of plans currently in the procedure cache (sys.dm_exec_cached_plans). Very high counts (> 100k) may indicate hash bucket pressure. Use 'Optimize for Ad-hoc Workloads' to reduce single-use plan accumulation.");
        AddKpi("Multi-Plan Queries",$"{summary.MultiPlanQueryCount:N0}",       summary.MultiPlanQueryCount > 3 ? "Warning" : "Success",
               "Number of query hashes that have more than 5 distinct cached plans. Each extra plan represents a separate compilation — a sign of ad-hoc SQL or forced recompilation. Target: 0.");
        AddKpi("Plan Guides",       $"{summary.PlanGuidesEnabled:N0}",         summary.PlanGuidesEnabled > 0 ? "Warning" : "Success",
               "Number of active plan guides in sys.plan_guides. Plan guides freeze execution plans; they can become stale after schema changes, index changes, or SQL Server upgrades. Review all listed guides.");
        AddKpi("Severity",          summary.Severity,                          severityColor,
               "Overall plan cache health signal: CRITICAL = cache was erased or > 10 multi-plan queries; WARNING = multi-plan queries detected or plan guides present; OK = no issues found.");

        // Folded, the section says it in one line (#149).
        SummarySection.FoldedSummary = string.Join(" · ",
            $"Plan cache {cacheStatus}", $"{summary.TotalCachedPlans:N0} plans", $"{summary.MultiPlanQueryCount:N0} multi-plan queries",
            $"{summary.PlanGuidesEnabled:N0} plan guides", summary.Severity);

        // ── Cache-erased banner ────────────────────────────────────────
        BannerCacheErased.Visibility = summary.PlanCacheErasedRecently
            ? Visibility.Visible
            : Visibility.Collapsed;

        // ── DataGrids ─────────────────────────────────────────────────
        MultiPlanGrid.ItemsSource  = multiPlan.ToList();
        KeyLookupGrid.ItemsSource  = lookups;
        CardinalityGrid.ItemsSource = cardinality.ToList();
        SkewGrid.ItemsSource        = skewed.ToList();
        PlanGuidesGrid.ItemsSource  = guides.ToList();
    }

    // The plans converting implicitly; null when they couldn't be read. A plan XML search can time
    // out on a very large plan cache: the tab says so, and the rest of the page still shows.
    private async Task<List<ImplicitConversion>?> ReadConversionsAsync()
    {
        try
        {
            var rows = (await _repo.GetImplicitConversionsAsync()).ToList();
            TxtConvProblem.Visibility = Visibility.Collapsed;
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("Plan Cache Health: reading the implicit conversions failed", ex);
            TxtConvProblem.Text       = $"Couldn't read the implicit conversions: {ErrorStatus.Format(ex)} Refresh to try again.";
            TxtConvProblem.Visibility = Visibility.Visible;
            return null;
        }
    }

    // The verdict's Show it: the Implicit Conversions tab, its most used plan selected and in view.
    private void ShowWorstConversion()
    {
        Tabs.SelectedItem = TabImplicitConversions;
        if (ConvGrid.ItemsSource is not IEnumerable<ImplicitConversion> rows || rows.OrderByDescending(r => r.ExecutionCount).FirstOrDefault() is not { } worst)
            return;
        ConvGrid.SelectedItem = worst;
        ConvGrid.ScrollIntoView(worst);
        ConvGrid.Focus();
    }

    // ── Helpers ───────────────────────────────────────────────────────
    private void AddKpi(string label, string value, string brushKey, string tooltip)
    {
        var card  = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 10, 10),
            Width  = 180,
        };
        ToolTipService.SetToolTip(card, MakeTooltip(label, tooltip));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue") }.WithForeground(brushKey));
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = sp;
        KpiStrip.Children.Add(card);
    }

    private static ToolTip MakeTooltip(string title, string description)
    {
        var sp = new StackPanel { MaxWidth = 340 };
        sp.Children.Add(new TextBlock { Text = title,       FontWeight = FontWeights.Bold,  TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return new ToolTip { Content = sp };
    }
}
