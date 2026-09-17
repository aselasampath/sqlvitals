using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlPulse.Engine.Repositories;

namespace SqlPulse.Desktop.Pages;

public partial class PlanCacheHealthPage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public PlanCacheHealthPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        var (summary, multiPlan, keyLookups, cardinality, skewed, guides) =
            await _repo.GetPlanCacheHealthAsync();

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

        // ── Cache-erased banner ────────────────────────────────────────
        BannerCacheErased.Visibility = summary.PlanCacheErasedRecently
            ? Visibility.Visible
            : Visibility.Collapsed;

        // ── DataGrids ─────────────────────────────────────────────────
        MultiPlanGrid.ItemsSource  = multiPlan.ToList();
        KeyLookupGrid.ItemsSource  = keyLookups.ToList();
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
