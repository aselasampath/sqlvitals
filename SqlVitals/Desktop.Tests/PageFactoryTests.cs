using System.IO;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The page factory (#183): every page in <see cref="PageCatalog"/> comes from the container with its
/// view model as its <c>DataContext</c>, and a code-behind page loads through its view model.
/// </summary>
public sealed class PageFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShellStatus _shell = new();
    private MonitoringManager? _monitoring;

    // The pages' services as the app registers them (#166), on a temp folder and fakes.
    private IPageFactory Factory()
    {
        Directory.CreateDirectory(_dir);
        var settings = new ConnectionSettingsService(_dir);
        _monitoring ??= new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                              (_, _) => new FakeRepository());
        return new ServiceCollection()
            .AddSingleton<INavigator>(new FakeNavigator())
            .AddSingleton<IShellStatus>(_shell)
            .AddSingleton<IRefreshScheduler>(new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()))
            .AddSingleton<IDialogService>(new FakeDialogService())
            .AddSingleton<IFileDialogs>(new FakeFileDialogs())
            .AddSingleton<IThemeService>(new FakeThemeService())
            .AddSingleton(TimeProvider.System)
            .AddSingleton(settings)
            .AddSingleton(_monitoring)
            .AddSingleton<IUiDispatcher>(new FakeUiDispatcher())
            .AddSingleton(new OverviewVisits(settings))
            .AddSingleton(new ChatNotificationService(new HttpWebhookSender(), "DBA-PC01", _ => { }))
            .AddSingleton<IWatchStarter>(new DirectWatchStarter(_monitoring))
            .AddSingleton<IClipboard>(new FakeClipboard())
            .AddSingleton(new CollectorLink(new FakeCollectorControl(_dir), settings, new FakeUiDispatcher(), TimeProvider.System))
            .AddSingleton<Func<IWaitStatsRepository, ExportService>>(repository => new ExportService(repository))
            .AddPages()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true })
            .GetRequiredService<IPageFactory>();
    }

    private static PageArgs Args(string tag, IWaitStatsRepository? repo = null) =>
        new(new Route(tag, ServerId: tag == "Overview" ? Guid.NewGuid() : null), repo ?? new FakeRepository())
        {
            ConnectionName   = "PROD-SQL01",
            ApplyConnections = _ => Task.CompletedTask,
        };

    [Fact]
    public void TheFactory_MakesEveryPageInTheCatalog()
    {
        var factory = Factory();

        Assert.Equal(PageCatalog.Types.Keys.Order(), factory.Tags.Order());
    }

    public static TheoryData<string> Tags => [.. PageCatalog.Types.Keys];

    // #169: every page in the catalog refreshes through its view model, the one refresh path, and
    // says how it went once. Its server can't be reached here, so most fail, and that's said once too.
    [Theory]
    [MemberData(nameof(Tags))]
    public void EachPage_RefreshesThroughItsViewModel_AndReportsOnce(string tag) => UiThread.Run(() =>
    {
        var made = Factory().Create(tag, Args(tag, new FakeRepository { Unreachable = true }));
        UiThread.Wait(made.ViewModel.OnNavigatedToAsync());
        var before = _shell.Reports.Count;

        UiThread.Wait(made.ViewModel.RefreshAsync());

        var refreshes = _shell.Reports.Skip(before)
            .Select((report, i) => (Report: report, From: _shell.From[before + i]))
            .Where(r => r.Report == "Read Refresh" || r.Report.StartsWith("Error Refresh", StringComparison.Ordinal))
            .ToList();
        var (_, from) = Assert.Single(refreshes);
        Assert.Same(made.ViewModel, from);
        Assert.False(made.ViewModel.IsBusy);
        made.ViewModel.OnNavigatedFrom();
    });

    [Theory]
    [MemberData(nameof(Tags))]
    public void EachPage_ComesWithItsViewModel_AsItsDataContext(string tag) => UiThread.Run(() =>
    {
        var made = Factory().Create(tag, Args(tag));

        Assert.IsType(PageCatalog.Types[tag], made.View);
        Assert.Same(made.ViewModel, made.View.DataContext);
        Assert.Equal(tag, made.ViewModel.Tag);
        // The view model is the page's own: the one its verdict is bound to.
        Assert.Same(made.ViewModel, made.View.GetType().GetProperty("ViewModel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                        .GetValue(made.View));
        // Until its wave of #180, a page that reads in its code-behind loads through it.
        if (made.View is ICodeBehindPage)
            Assert.IsAssignableFrom<CodeBehindViewModel>(made.ViewModel);
    });

    [Fact]
    public void AnUnknownTag_SaysWhereToAddIt()
    {
        var error = Assert.Throws<ArgumentException>(() => Factory().Create("Nowhere", Args("Nowhere")));

        Assert.Contains("PageRegistrations", error.Message);
    }

    [Fact]
    public void ACodeBehindPage_LoadsThroughItsViewModel_AndAFailureIsReportedOnce() => UiThread.Run(() =>
    {
        var error = new InvalidOperationException("Timeout expired.");
        var repo  = new FakeRepository
        {
            GetPerfmonCountersAsync = () => Task.FromException<IEnumerable<PerfmonCounter>>(error),
        };
        var made = Factory().Create("Perfmon", Args("Perfmon", repo));

        UiThread.Wait(made.ViewModel.OnNavigatedToAsync());

        // Perfmon tells the bar its kind as it opens; the failure is reported once.
        Assert.Equal(["Error Load: Timeout expired."], _shell.Reports.Where(r => r != "Context"));
        Assert.Same(made.ViewModel, _shell.From[_shell.Reports.IndexOf("Error Load: Timeout expired.")]);
        // The page says what it couldn't read, as before.
        var verdict = ((PageShell)made.View.FindName("Shell")).Verdict!;
        Assert.Equal(VerdictLevel.Unknown, verdict.Level);
        Assert.Equal("Error: Timeout expired. Refresh to try again.", verdict.Why);
        Assert.Same(verdict, made.ViewModel.Verdict);
    });

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a file still open: the temp folder keeps it */ }
    }
}
