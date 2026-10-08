using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Every page the page factory makes (#183), by its tag in <see cref="PageCatalog"/>. A new page is
/// a view, its view model in <c>ViewModels/Pages</c>, a <see cref="PageCatalog"/> entry and a line
/// here (<c>docs/Architecture.md</c>, "Adding a page").
/// </summary>
public static class PageRegistrations
{
    /// <summary>The pages, and the factory that makes them.</summary>
    public static IServiceCollection AddPages(this IServiceCollection services) =>
        services
            .AddSingleton<IPageFactory, PageFactory>()
            .AddPage("Home",             (_, a) => new HomePage(a.Monitoring, a.Settings), p => p.ViewModel)
            .AddPage("Overview",         (_, a) => new OverviewPage(a.Route.ServerId!.Value, a.Monitoring, a.Settings, a.OverviewVisits!.Open(a.Route.ServerId!.Value)), p => p.ViewModel)
            .AddPage("LiveMetrics",      (_, a) => new LiveMetricsDashboardPage(a.Repo, a.Monitoring, a.ConnectionId, a.History, a.ConnectionName), p => p.ViewModel)
            .AddPage("Alerts",           (s, a) => new AlertsPage(a.Monitoring, a.Settings, a.Route.AlertsConnectionId, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("TopWaits",         (s, a) => new TopWaitsPage(a.Repo, a.Settings, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("ActiveWaits",      (s, a) => new ActiveWaitsPage(a.Repo, a.Settings, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("Processes",        (_, a) => new ProcessesPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("Deadlocks",        (_, a) => new DeadlocksPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("AgentJobs",        (_, a) => new AgentJobsPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("Backups",          (_, a) => new BackupsPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("FileIo",           (_, a) => new FileIoPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("ConfigChecks",     (_, a) => new ConfigChecksPage(a.Repo, a.ConnectionName, a.Settings), p => p.ViewModel)
            .AddPage("SecurityAudit",    (_, a) => new SecurityAuditPage(a.Repo, a.ConnectionName, a.Settings), p => p.ViewModel)
            .AddPage("WaitTrend",        (s, a) => new WaitStatsTrendPage(a.Repo, a.History, a.Settings, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("TempDb",           (_, a) => new TempDbPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("Memory",           (_, a) => new MemoryGrantsPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("QueryStore",       (s, a) => new QueryStorePage(a.Repo, a.Settings, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("QueryRegressions", (s, a) => new QueryRegressionsPage(a.Repo, a.History, a.Settings, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("IndexHealth",      (_, a) => new IndexHealthPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("ResourceQueries",  (_, a) => new ResourceQueriesPage(a.Repo, a.ServerOffset, a.Settings), p => p.ViewModel)
            .AddPage("ImplicitConv",     (_, a) => new ImplicitConversionsPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("PlanCacheHealth",  (_, a) => new PlanCacheHealthPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("StaleStats",       (_, a) => new StaleStatisticsPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("DbStorage",        (_, a) => new DatabaseStoragePage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("AppConnections",   (_, a) => new ApplicationConnectionsPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("Perfmon",          (s, a) => new PerfmonPage(a.Repo, a.History, a.Settings, s.GetRequiredService<INavigator>()), p => p.ViewModel)
            .AddPage("SpTrace",          (_, a) => new SpTracePage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("Export",           (_, a) => new ExportPage(a.Repo, a.Settings), p => p.ViewModel)
            .AddPage("Settings",         (_, a) => new SettingsPage(a.Settings, a.Monitoring, a.Chat!, a.ApplyConnections!, a.Route.Settings, a.SettingsMessage), p => p.ViewModel);

    /// <summary>One page: how to build its view, and its view model.</summary>
    public static IServiceCollection AddPage<TPage>(this IServiceCollection services, string tag,
                                                    Func<IServiceProvider, PageArgs, TPage> view, Func<TPage, PageViewModel> viewModel)
        where TPage : Page =>
        services.AddSingleton(new PageRegistration(tag, typeof(TPage), (s, a) =>
        {
            var page = view(s, a);
            return new PageInstance(page, viewModel(page));
        }));
}
