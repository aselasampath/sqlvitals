using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The app stops its queries when they are no longer wanted (#167): a page left, monitoring
/// stopped; a lock it waited too long for skips a sample; a cancelled read is never an error;
/// and a missing permission shows the GRANT that fixes it.
/// </summary>
public sealed class CancellationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeShellStatus _shell = new();
    private MonitoringManager? _monitoring;

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
            .AddSingleton<IClipboard>(new FakeClipboard())
            .AddSingleton(TimeProvider.System)
            .AddSingleton(settings)
            .AddSingleton(_monitoring)
            .AddSingleton<IUiDispatcher>(new FakeUiDispatcher())
            .AddSingleton(new OverviewVisits(settings))
            .AddSingleton(new ChatNotificationService(new HttpWebhookSender(), "DBA-PC01", _ => { }))
            .AddSingleton<Func<IWaitStatsRepository, ExportService>>(repository => new ExportService(repository))
            .AddPages()
            .BuildServiceProvider()
            .GetRequiredService<IPageFactory>();
    }

    private static LiveMetricSnapshot Snapshot() =>
        new(DateTime.UtcNow, 100, 0, 0, 10, 0, 0, SqlCpuUtilizationPct: 12,
            PageLifeExpectancy: 5_000, MemoryGrantsPending: 0, BufferCacheHitRatio: 99, 0, 0, 0, 0, 0, 0, 0, WaitingTasks: 4);

    private static WaitStatsException Failed(SqlFailure failure) =>
        new("LiveMetricsRepository", "GetLiveMetricsAsync", new InvalidOperationException("from the server"), failure, "vitals");

    // ── A page left ──────────────────────────────────────────────────────

    // Query Store on, with nothing in it: its settings and its tabs read.
    private static FakeRepository QueryStoreOn() => new()
    {
        GetQueryStoreStatusAsync = () => Task.FromResult(new QueryStoreStatus("SalesDb",
            new QueryStoreHealth("SalesDb", "READ_WRITE", "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, "AUTO", "AUTO", null,
                                 200, 1482, 1630, 0, DateTime.UtcNow.AddDays(-20), DateTime.Now),
            QueryStoreFeatures.None)),
        GetQueryStoreTopQueriesAsync = (_, _, _, _) => Task.FromResult(new QueryStoreRows<QueryStoreQueryRow>([])),
    };

    [Fact]
    public void LeavingAPage_CancelsWhatItsViewModelReadsBesideItsLoad() => UiThread.Run(() =>
    {
        // Query Store reads a tab when it is opened, outside its load: the page's lifetime ends it (#167).
        var reading = new TaskCompletionSource<QueryStoreRows<QueryStoreVariationRow>>();
        var repo    = QueryStoreOn();
        repo.GetQueryStoreHighVariationAsync = (_, _, _, _) => reading.Task;
        var made    = Factory().Create("QueryStore", new PageArgs(new Route("QueryStore"), repo));
        var model   = (QueryStoreViewModel)made.ViewModel;
        UiThread.Wait(model.OnNavigatedToAsync());

        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;
        var token = repo.Tokens[nameof(FakeRepository.GetQueryStoreHighVariationAsync)];
        Assert.False(token.IsCancellationRequested);

        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        UiThread.Wait(model.TabLoaded);
        // Left, and cancelled: nothing about it is said.
        Assert.DoesNotContain(_shell.Reports, report => report.StartsWith("Error", StringComparison.Ordinal));
    });

    [Fact]
    public void LeavingAPage_CancelsWhatItsViewModelIsReading() => UiThread.Run(() =>
    {
        var reading = new TaskCompletionSource<IEnumerable<StaleStatistic>>();
        var repo    = new FakeRepository { GetStaleStatisticsAsync = () => reading.Task };
        var made    = Factory().Create("StaleStats", new PageArgs(new Route("StaleStats"), repo));

        var shown = made.ViewModel.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetStaleStatisticsAsync)];
        Assert.False(token.IsCancellationRequested);

        made.ViewModel.OnNavigatedFrom();

        // Its own token, linked to the page's lifetime (#184): either cancels the query.
        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        UiThread.Wait(shown);
        Assert.Empty(_shell.Reports);
        Assert.Null(made.ViewModel.Verdict);
    });

    [Fact]
    public void APageShownAgain_ReadsWithANewToken() => UiThread.Run(() =>
    {
        // A read beside the load (a tab opened, with its own token since #117) is bound to the
        // page's lifetime too: leaving cancels it while it runs.
        var repo    = QueryStoreOn();
        var pending = new TaskCompletionSource<QueryStoreRows<QueryStoreVariationRow>>();
        repo.GetQueryStoreHighVariationAsync = (_, _, _, _) => pending.Task;
        var made  = Factory().Create("QueryStore", new PageArgs(new Route("QueryStore"), repo));
        var model = (QueryStoreViewModel)made.ViewModel;

        UiThread.Wait(model.OnNavigatedToAsync());
        model.SelectedTab = (int)QueryStoreViewModel.Tab.Variation;
        var first = repo.Tokens[nameof(FakeRepository.GetQueryStoreHighVariationAsync)];
        model.OnNavigatedFrom();
        Assert.True(first.IsCancellationRequested);
        pending.SetCanceled(first);
        UiThread.Wait(model.TabLoaded);

        repo.GetQueryStoreHighVariationAsync = (_, _, _, _) => Task.FromException<QueryStoreRows<QueryStoreVariationRow>>(new InvalidOperationException("Not now."));
        UiThread.Wait(model.OnNavigatedToAsync());   // reads the tab on screen again
        Assert.False(repo.Tokens[nameof(FakeRepository.GetQueryStoreHighVariationAsync)].IsCancellationRequested);
    });

    [Fact]
    public async Task ALifetimeRepository_CancelsACallInFlight_WhenItsLifetimeEnds()
    {
        var pending = new TaskCompletionSource<IEnumerable<ActiveWait>>();
        var inner = new FakeRepository
        {
            GetActiveWaitsAsync = () => pending.Task,
            StopTraceAsync      = () => Task.FromResult(TraceSessionStatus.Stopped("SqlVitals_SpTrace")),
        };
        using var lifetime = new PageLifetime();
        var repo = new LifetimeRepository(inner, () => lifetime.Token);
        using var own = new CancellationTokenSource();

        var reading = repo.GetActiveWaitsAsync(own.Token);
        var token   = inner.Tokens[nameof(FakeRepository.GetActiveWaitsAsync)];
        await repo.StopTraceAsync();
        lifetime.End();

        // A call with its own token is cancelled by either; its own isn't touched.
        Assert.True(token.IsCancellationRequested);
        Assert.False(own.IsCancellationRequested);
        pending.SetCanceled(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        // Stopping a trace is cleanup: the page leaving doesn't stop it.
        Assert.False(inner.Tokens[nameof(FakeRepository.StopTraceAsync)].CanBeCanceled);
    }

    [Fact]
    public async Task ALifetimeRepository_CallWithoutAToken_GetsTheLifetimes()
    {
        var inner = new FakeRepository { GetActiveWaitsAsync = () => Task.FromResult<IEnumerable<ActiveWait>>([]) };
        using var lifetime = new PageLifetime();

        await new LifetimeRepository(inner, () => lifetime.Token).GetActiveWaitsAsync();

        Assert.Equal(lifetime.Token, inner.Tokens[nameof(FakeRepository.GetActiveWaitsAsync)]);
    }

    // ── Monitoring ───────────────────────────────────────────────────────

    // The token the sample is being read with: the read starts on the thread pool (#171).
    private static CancellationToken ReadToken(FakeRepository repo)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        CancellationToken token;
        while (!repo.Tokens.TryGetValue(nameof(FakeRepository.GetLiveMetricsAsync), out token))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "The sample was never read.");
            Thread.Sleep(5);
        }
        return token;
    }

    [Fact]
    public void PausingMonitoring_CancelsTheSampleBeingRead_AndRecordsNoFailure() => UiThread.Run(() =>
    {
        var reading = new TaskCompletionSource<LiveMetricSnapshot>();
        var repo    = new FakeRepository { GetLiveMetricsAsync = () => reading.Task };
        using var session = new MonitoringSession(Guid.NewGuid(), repo, "fp");
        session.Start();

        var sample = session.CollectNowAsync();
        var token  = ReadToken(repo);
        session.Pause();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        Assert.ThrowsAny<OperationCanceledException>(() => UiThread.Wait(sample));
        Assert.Null(session.LastError);
        Assert.NotEqual(Engine.Monitoring.HealthLevel.Unavailable, session.Health);
    });

    [Fact]
    public void DisposingMonitoring_CancelsTheSampleBeingRead() => UiThread.Run(() =>
    {
        var repo    = new FakeRepository { GetLiveMetricsAsync = () => new TaskCompletionSource<LiveMetricSnapshot>().Task };
        var session = new MonitoringSession(Guid.NewGuid(), repo, "fp");

        _ = session.CollectNowAsync();
        var token = ReadToken(repo);
        session.Dispose();

        Assert.True(token.IsCancellationRequested);
    });

    [Fact]
    public void ALockTimeout_SkipsTheSample_WithoutAFailure() => UiThread.Run(() =>
    {
        var answers = new Queue<Func<Task<LiveMetricSnapshot>>>(
        [
            () => Task.FromResult(Snapshot()),
            () => Task.FromException<LiveMetricSnapshot>(Failed(SqlFailure.Classify(SqlFailure.LockTimeoutError, "Lock request time out period exceeded."))),
        ]);
        var repo = new FakeRepository { GetLiveMetricsAsync = () => answers.Dequeue()() };
        using var session = new MonitoringSession(Guid.NewGuid(), repo, "fp");

        UiThread.Wait(session.CollectNowAsync());
        var health = session.Health;
        UiThread.Wait(session.CollectNowAsync());

        Assert.Equal(health, session.Health);
        Assert.Null(session.LastError);
        Assert.Single(session.Samples);
        Assert.Contains("sample was skipped: it waited 2 s for a lock another session holds", session.HealthText, StringComparison.Ordinal);
    });

    [Fact]
    public void AnyOtherFailure_StillCounts() => UiThread.Run(() =>
    {
        var repo = new FakeRepository
        {
            GetLiveMetricsAsync = () => Task.FromException<LiveMetricSnapshot>(Failed(SqlFailure.Classify(SqlFailure.CommandTimeoutError, "Timeout expired."))),
        };
        using var session = new MonitoringSession(Guid.NewGuid(), repo, "fp");

        Assert.Throws<WaitStatsException>(() => UiThread.Wait(session.CollectNowAsync()));

        Assert.Equal(Engine.Monitoring.HealthLevel.Unavailable, session.Health);
    });

    // ── What a page says when it can't read ──────────────────────────────

    [Fact]
    public void AMissingPermission_IsExplained_WithTheGrantThatFixesIt()
    {
        var model = new TempDbViewModel(new FakeRepository());
        var error = Failed(SqlFailure.Classify(300, "VIEW SERVER STATE permission was denied on object 'server', database 'master'."));

        model.ShowError("the TempDB figures", error);

        Assert.Equal(
            PageVerdict.Unreadable("the TempDB figures",
                                   "Error [LiveMetricsRepository.GetLiveMetricsAsync]: The login lacks VIEW SERVER STATE.",
                                   "USE [master];\nGRANT VIEW SERVER STATE TO [vitals];"),
            model.Verdict);
        Assert.Equal("Error [LiveMetricsRepository.GetLiveMetricsAsync]: The login lacks VIEW SERVER STATE. A DBA can grant it once:\nUSE [master];\nGRANT VIEW SERVER STATE TO [vitals];\nthen Refresh to read again.",
                     model.Verdict!.Why);
    }

    [Fact]
    public void ACancelledRead_LeavesTheVerdictAlone()
    {
        var model = new TempDbViewModel(new FakeRepository());
        var before = model.Verdict;

        model.ShowError("the TempDB figures", new OperationCanceledException());

        Assert.Same(before, model.Verdict);
    }

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a file still open: the temp folder keeps it */ }
    }
}
