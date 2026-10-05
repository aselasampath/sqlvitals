using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Pages;

public partial class PlanCacheHealthPage : System.Windows.Controls.Page, IRefreshable, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly IWaitStatsRepository _repo;

    // The page template's verdict (#149): the worst of what the page checks.
    internal PlanCacheHealthViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the summary's folds are remembered; none in some tests.</param>
    public PlanCacheHealthPage(IWaitStatsRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new PlanCacheHealthViewModel(showTab: index => Tabs.SelectedIndex = index);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var read = _repo.GetPlanCacheHealthAsync();
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
        ViewModel.Show(summary, lookups.Count);

        // ── KPI strip ─────────────────────────────────────────────────
        KpiStrip.Children.Clear();

        string cacheStatus     = summary.PlanCacheErasedRecently ? "ERASED" : "OK";
        string cacheColor      = summary.PlanCacheErasedRecently ? "#EF4444" : "#22C55E";
        string severityColor   = summary.Severity switch
        {
            "CRITICAL" => "#EF4444",
            "WARNING"  => "#FBBF24",
            _          => "#22C55E"
        };

        AddKpi("Plan Cache",        cacheStatus,                               cacheColor,
               "Whether DBCC FREEPROCCACHE was run recently or the server restarted in the last 24 hours. ERASED means historical plan data may be missing — wait for plans to repopulate before investigating.");
        AddKpi("Total Plans",       $"{summary.TotalCachedPlans:N0}",          "#7C3AED",
               "Total number of plans currently in the procedure cache (sys.dm_exec_cached_plans). Very high counts (> 100k) may indicate hash bucket pressure. Use 'Optimize for Ad-hoc Workloads' to reduce single-use plan accumulation.");
        AddKpi("Multi-Plan Queries",$"{summary.MultiPlanQueryCount:N0}",       summary.MultiPlanQueryCount > 3 ? "#FBBF24" : "#22C55E",
               "Number of query hashes that have more than 5 distinct cached plans. Each extra plan represents a separate compilation — a sign of ad-hoc SQL or forced recompilation. Target: 0.");
        AddKpi("Plan Guides",       $"{summary.PlanGuidesEnabled:N0}",         summary.PlanGuidesEnabled > 0 ? "#FBBF24" : "#22C55E",
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

    // ── Helpers ───────────────────────────────────────────────────────
    private void AddKpi(string label, string value, string hexColor, string tooltip)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        var card  = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 10, 10),
            Width  = 180,
        };
        ToolTipService.SetToolTip(card, MakeTooltip(label, tooltip));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue"), Foreground = brush });
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
