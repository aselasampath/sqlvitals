using SqlVitals.Desktop.Pages;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// The page each route tag opens (#140). MainWindow builds the pages; this says which type each
/// is, so a test can check every page in <see cref="NavMenu"/> declares its kind through
/// <see cref="IPageContext"/>.
/// </summary>
public static class PageCatalog
{
    public static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>
    {
        ["Home"]             = typeof(HomePage),
        ["Overview"]         = typeof(OverviewPage),
        ["LiveMetrics"]      = typeof(LiveMetricsDashboardPage),
        ["Alerts"]           = typeof(AlertsPage),
        ["TopWaits"]         = typeof(TopWaitsPage),
        ["ActiveWaits"]      = typeof(ActiveWaitsPage),
        ["Processes"]        = typeof(ProcessesPage),
        ["Deadlocks"]        = typeof(DeadlocksPage),
        ["AgentJobs"]        = typeof(AgentJobsPage),
        ["Backups"]          = typeof(BackupsPage),
        ["FileIo"]           = typeof(FileIoPage),
        ["ConfigChecks"]     = typeof(ConfigChecksPage),
        ["SecurityAudit"]    = typeof(SecurityAuditPage),
        ["WaitTrend"]        = typeof(WaitStatsTrendPage),
        ["TempDb"]           = typeof(TempDbPage),
        ["Memory"]           = typeof(MemoryGrantsPage),
        ["QueryStore"]       = typeof(QueryStorePage),
        ["QueryRegressions"] = typeof(QueryRegressionsPage),
        ["IndexHealth"]      = typeof(IndexHealthPage),
        ["ResourceQueries"]  = typeof(ResourceQueriesPage),
        ["ImplicitConv"]     = typeof(ImplicitConversionsPage),
        ["PlanCacheHealth"]  = typeof(PlanCacheHealthPage),
        ["StaleStats"]       = typeof(StaleStatisticsPage),
        ["DbStorage"]        = typeof(DatabaseStoragePage),
        ["AppConnections"]   = typeof(ApplicationConnectionsPage),
        ["Perfmon"]          = typeof(PerfmonPage),
        ["SpTrace"]          = typeof(SpTracePage),
        ["Export"]           = typeof(ExportPage),
        ["Settings"]         = typeof(SettingsPage),
    };
}
