using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Repositories;

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
            .AddPage("Home",             (s, a) => new HomePage(new HomeViewModel(Monitoring(s), Settings(s), Starter(s), Ui(s), Time(s))), p => p.ViewModel)
            .AddPage("Overview",         (s, a) => new OverviewPage(new OverviewViewModel(a.Route.ServerId!.Value, Monitoring(s), Settings(s),
                                                                    s.GetRequiredService<OverviewVisits>().Open(a.Route.ServerId!.Value),
                                                                    Starter(s), Ui(s), s.GetRequiredService<IClipboard>(), time: Time(s))), p => p.ViewModel)
            .AddPage("LiveMetrics",      (s, a) => new LiveMetricsDashboardPage(new LiveMetricsDashboardViewModel(a.Repo, Monitoring(s), a.ConnectionId,
                                                                                History(s, a), a.ConnectionName, Navigator(s), Starter(s))), p => p.ViewModel)
            .AddPage("Alerts",           (s, a) => new AlertsPage(new AlertsViewModel(Monitoring(s), Settings(s), Navigator(s), a.Route.AlertsConnectionId, Time(s)),
                                                                  Settings(s)), p => p.ViewModel)
            .AddPage("Waits",            (s, a) => new WaitsPage(new WaitsViewModel(a.Repo, History(s, a), Navigator(s), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("Processes",        (s, a) => new ProcessesPage(new ProcessesViewModel(a.Repo, a.Repo, Settings(s), Navigator(s), a.Route.View), Settings(s)), p => p.ViewModel)
            .AddPage("Deadlocks",        (s, a) => new DeadlocksPage(new DeadlocksViewModel(a.Repo, Dialogs(s), s.GetRequiredService<IFileDialogs>(),
                                                                                    s.GetRequiredService<IClipboard>(), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("AgentJobs",        (s, a) => new AgentJobsPage(new AgentJobsViewModel(a.Repo, Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("Backups",          (s, a) => new BackupsPage(new BackupsViewModel(a.Repo, Settings(s), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("FileIo",           (s, a) => new FileIoPage(new FileIoViewModel(a.Repo, Settings(s)), Settings(s)), p => p.ViewModel)
            .AddPage("ConfigChecks",     (s, a) => new ConfigChecksPage(new ConfigChecksViewModel(a.Repo, Dialogs(s), a.ConnectionName, Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("SecurityAudit",    (s, a) => new SecurityAuditPage(new SecurityAuditViewModel(a.Repo, Dialogs(s), a.ConnectionName, Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("TempDb",           (s, a) => new TempDbPage(new TempDbViewModel(a.Repo), Settings(s)), p => p.ViewModel)
            .AddPage("Memory",           (s, a) => new MemoryPage(new MemoryViewModel(a.Repo), Settings(s)), p => p.ViewModel)
            .AddPage("QueryStore",       (s, a) => new QueryStorePage(new QueryStoreViewModel(a.Repo, Settings(s), Navigator(s), Dialogs(s),
                                                                                  s.GetRequiredService<IClipboard>(), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("QueryRegressions", (s, a) => new QueryRegressionsPage(new QueryRegressionsViewModel(a.Repo, History(s, a), Settings(s), Navigator(s),
                                                                                                  Dialogs(s), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("IndexHealth",      (s, a) => new IndexHealthPage(new IndexHealthViewModel(a.Repo, a.Repo, Dialogs(s), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("ResourceQueries",  (s, a) => new ResourceQueriesPage(new ResourceQueriesViewModel(a.Repo, Dialogs(s), a.ServerOffset), Settings(s)), p => p.ViewModel)
            .AddPage("PlanCacheHealth",  (s, a) => new PlanCacheHealthPage(new PlanCacheHealthViewModel(a.Repo, a.Repo, a.Route.View), Settings(s)), p => p.ViewModel)
            .AddPage("StaleStats",       (s, a) => new StaleStatisticsPage(new StaleStatisticsViewModel(a.Repo, Dialogs(s), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("DbStorage",        (s, a) => new DatabaseStoragePage(new DatabaseStorageViewModel(a.Repo), Settings(s)), p => p.ViewModel)
            .AddPage("AppConnections",   (s, a) => new ApplicationConnectionsPage(new ApplicationConnectionsViewModel(a.Repo), Settings(s)), p => p.ViewModel)
            .AddPage("Perfmon",          (s, a) => new PerfmonPage(new PerfmonViewModel(a.Repo, History(s, a), Navigator(s), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("SpTrace",          (s, a) => new SpTracePage(new SpTraceViewModel(a.Repo, a.Repo, Dialogs(s)), Settings(s)), p => p.ViewModel)
            .AddPage("Export",           (s, a) => new ExportPage(new ExportViewModel(s.GetRequiredService<Func<IWaitStatsRepository, ExportService>>()(a.Repo), Dialogs(s),
                                                                                s.GetRequiredService<IFileDialogs>(), Time(s)), Settings(s)), p => p.ViewModel)
            .AddPage("Settings",         (s, a) => new SettingsPage(Settings(s), Monitoring(s), s.GetRequiredService<ChatNotificationService>(),
                                                                    a.ApplyConnections!, a.Route.Settings, a.SettingsMessage,
                                                                    s.GetRequiredService<CollectorLink>(), s.GetService<PermissionChecker>(),
                                                                    s.GetRequiredService<IWatchStarter>(), s.GetRequiredService<IClipboard>()), p => p.ViewModel);

    // What the pages share, from the container (#166).
    private static ConnectionSettingsService Settings(IServiceProvider s) => s.GetRequiredService<ConnectionSettingsService>();
    private static MonitoringManager Monitoring(IServiceProvider s) => s.GetRequiredService<MonitoringManager>();
    private static INavigator Navigator(IServiceProvider s) => s.GetRequiredService<INavigator>();
    private static IDialogService Dialogs(IServiceProvider s) => s.GetRequiredService<IDialogService>();
    private static TimeProvider Time(IServiceProvider s) => s.GetRequiredService<TimeProvider>();
    private static IUiDispatcher Ui(IServiceProvider s) => s.GetRequiredService<IUiDispatcher>();
    private static IWatchStarter Starter(IServiceProvider s) => s.GetRequiredService<IWatchStarter>();

    // The open connection's history of samples, on pages that chart it.
    private static ConnectionHistory? History(IServiceProvider s, PageArgs a) => Monitoring(s).HistoryFor(a.ConnectionId);

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
