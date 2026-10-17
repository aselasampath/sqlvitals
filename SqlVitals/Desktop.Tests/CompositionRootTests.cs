using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Shell;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The composition root (#166): the app's own services come from the container, one each, and
/// the shell's view model (#191) and the main window with them; its <c>ILogger&lt;T&gt;</c> write the log file as <see cref="AppLog"/> did.
/// </summary>
/// <remarks>
/// Monitoring and the history aren't resolved here: the real ones open %AppData%\SqlVitals\history.db.
/// The page factory's tests build the pages over the same registrations with fakes.
/// </remarks>
public sealed class CompositionRootTests
{
    public static TheoryData<Type> AppServices =>
    [
        typeof(IConfiguration),
        typeof(IRepositoryFactory),
        typeof(ConnectionSettingsService),
        typeof(HistoryWriter),
        typeof(MonitoringManager),
        typeof(IWebhookSender),
        typeof(ChatNotificationService),
        typeof(OverviewVisits),
        typeof(Func<IWaitStatsRepository, ExportService>),
        typeof(ILoggerFactory),
        typeof(ShellViewModel),
        typeof(MainWindow),
    ];

    [Theory]
    [MemberData(nameof(AppServices))]
    public void EachAppService_IsRegistered_OnceForTheApp(Type service)
    {
        var registrations = new ServiceCollection().AddAppLog().AddUiServices().AddAppServices().AddPages().AddShell().AddSingleton<MainWindow>();

        var registration = Assert.Single(registrations, d => d.ServiceType == service);
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
    }

    [Fact]
    public void TheAppsContainer_IsCheckedAsItIsBuilt_TheMainWindowWithIt()
    {
        // ValidateOnBuild: every constructor's services, the main window's included, are registered.
        // Only the app adds the window (#191): the container itself doesn't refer to it.
        var services = CompositionRoot.Build(s => s.AddSingleton<MainWindow>());

        Assert.True(services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(MainWindow)));
        Assert.False(CompositionRoot.Build().GetRequiredService<IServiceProviderIsService>().IsService(typeof(MainWindow)));
    }

    [Fact]
    public void TheRepositoryFactory_ReadsTheShippedFile()
    {
        var factory = CompositionRoot.Build().GetRequiredService<IRepositoryFactory>();

        var target = factory.TargetFor(null, commandTimeoutSeconds: 0);
        Assert.Equal(new SqlTarget(""), target);
        Assert.IsType<WaitStatsRepository>(factory.Create(target));
    }

    [Fact]
    public void Export_IsMadeForTheConnectionsRepository()
    {
        var export = CompositionRoot.Build().GetRequiredService<Func<IWaitStatsRepository, ExportService>>();

        Assert.NotSame(export(new Fakes.FakeRepository()), export(new Fakes.FakeRepository()));
    }

    [Fact]
    public void ALogger_FromTheContainer_WritesTheFileAsBefore_Redacted()
    {
        var log    = CompositionRoot.Build().GetRequiredService<ILogger<CompositionRootTests>>();
        var marker = Guid.NewGuid().ToString("N");

        log.Error($"{marker} posting to https://hooks.slack.com/services/T0001/B0002/abc failed {{json}}",
                  new InvalidOperationException("boom"));
        log.Info($"{marker} done");

        var lines = File.ReadAllLines(Path.Combine(FileLog.Directory, $"SqlVitals-{DateTime.Now:yyyyMMdd}.log"))
                        .SkipWhile(line => !line.Contains(marker, StringComparison.Ordinal))
                        .ToList();
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} ERROR " + marker + @" posting to \[webhook URL removed\] failed \{json\}$", lines[0]);
        Assert.Equal("System.InvalidOperationException: boom", lines[1]);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} INFO " + marker + " done$", lines.First(l => l.EndsWith(marker + " done", StringComparison.Ordinal)));
    }

    [Fact]
    public void AppLog_AndTheContainer_ShareOneLoggerFactory() =>
        Assert.Same(AppLog.Factory, CompositionRoot.Build().GetRequiredService<ILoggerFactory>());
}
