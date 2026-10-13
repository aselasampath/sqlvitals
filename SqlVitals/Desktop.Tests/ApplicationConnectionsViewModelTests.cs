using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// App Connections' view model on its own (#185): no page, no STA thread, a fake repository. It
/// reads through its lifecycle, says what it couldn't read once, stops reading when the page is
/// left, and keeps the application selected across a refresh.
/// </summary>
public sealed class ApplicationConnectionsViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 30, 0);

    private static ApplicationConnection App(string name, int connections, int active = 0, long cpu = 1_000) =>
        new(name, connections, active, cpu, connections * 96L, cpu * 40, cpu / 10, cpu / 20, Now.AddHours(-6), Now.AddSeconds(-30), Now);

    private static (ApplicationConnectionsViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo)
    {
        var model = new ApplicationConnectionsViewModel(repo);
        var shell = new FakeShellStatus();
        model.Connect("AppConnections", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell);
    }

    private static FakeRepository Repo(params ApplicationConnection[] apps) =>
        new() { GetApplicationConnectionsAsync = () => Task.FromResult<IEnumerable<ApplicationConnection>>(apps) };

    [Fact]
    public async Task Loaded_ShowsEveryApplication_TheCountsAsCards_AndTheFoldedLines()
    {
        var (model, shell) = Connected(Repo(App("BillingService", 64, 2, 912_000), App("OrderService", 120, 6, 1_840_000),
                                            App("Reporting (SSRS)", 22, 1, 2_310_000), App("SSMS", 4, 1, 12_000)));

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(4, model.Applications.Count);
        Assert.Equal(["Applications", "Total Connections", "Active Requests", "Total CPU (ms)"], model.Counts.Select(c => c.Label));
        Assert.Equal(["4", "210", "10", $"{5_074_000:N0}"], model.Counts.Select(c => c.Value));
        Assert.Equal([KpiCard.Tones.Accent, KpiCard.Tones.Info, KpiCard.Tones.Success, KpiCard.Tones.Warning], model.Counts.Select(c => c.Tone));
        Assert.Equal("4 application(s)  •  210 total connections", model.Summary);
        Assert.Equal("4 application(s) · 210 connections · 10 active requests", model.CountsSummary);
        Assert.Equal("OrderService 120 · BillingService 64 · Reporting (SSRS) 22", model.ChartSummary);
        // The chart: the most connections first.
        Assert.Equal(["OrderService", "BillingService", "Reporting (SSRS)", "SSMS"], model.ChartApplications.Select(a => a.ApplicationName));
        Assert.Equal("4 applications hold 210 connections; OrderService the most, 120", model.Verdict!.Headline);
        Assert.Null(model.Status);
    }

    [Fact]
    public async Task TheChart_ShowsTheFifteenHoldingTheMost()
    {
        var apps = Enumerable.Range(1, 20).Select(i => App($"App{i:00}", i)).ToArray();
        var (model, _) = Connected(Repo(apps));

        await model.OnNavigatedToAsync();

        Assert.Equal(20, model.Applications.Count);
        Assert.Equal(ApplicationConnectionsViewModel.Charted, model.ChartApplications.Count);
        Assert.Equal("App20", model.ChartApplications[0].ApplicationName);
        Assert.Equal("App06", model.ChartApplications[^1].ApplicationName);
    }

    [Fact]
    public async Task Empty_SaysNoOneIsConnected_WithNoCardsAndNothingCharted()
    {
        var reads = 0;
        var (model, _) = Connected(new FakeRepository
        {
            GetApplicationConnectionsAsync = () => Task.FromResult<IEnumerable<ApplicationConnection>>(++reads == 1 ? [App("OrderService", 12)] : []),
        });
        await model.OnNavigatedToAsync();

        await model.RefreshAsync();

        Assert.Empty(model.Applications);
        Assert.Empty(model.Counts);
        // The chart is cleared, rather than show the last read.
        Assert.Empty(model.ChartApplications);
        Assert.Equal("No active user connections", model.Summary);
        Assert.Equal("No active user connections", model.CountsSummary);
        Assert.Equal("Nothing to chart", model.ChartSummary);
        Assert.NotNull(model.Verdict);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell) = Connected(new FakeRepository
        {
            GetApplicationConnectionsAsync = () => Task.FromException<IEnumerable<ApplicationConnection>>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the connections", "Error: Timeout expired."), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<IEnumerable<ApplicationConnection>>();
        var repo = new FakeRepository { GetApplicationConnectionsAsync = () => reading.Task };
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetApplicationConnectionsAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
        Assert.Empty(model.Applications);
    }

    [Fact]
    public async Task TheApplicationSelected_IsKeptAcrossARefresh_ByItsName()
    {
        var reads = 0;
        var (model, _) = Connected(new FakeRepository
        {
            // Each read is new rows: the same applications, holding more.
            GetApplicationConnectionsAsync = () => Task.FromResult<IEnumerable<ApplicationConnection>>(
                [App("OrderService", 120 + ++reads), App("BillingService", 64)]),
        });
        await model.OnNavigatedToAsync();
        model.SelectedApplication = model.Applications.Single(a => a.ApplicationName == "BillingService");
        var rows = model.Applications;

        await model.RefreshAsync();

        Assert.Same(rows, model.Applications);
        Assert.Equal("BillingService", model.SelectedApplication!.ApplicationName);
        Assert.Contains(model.SelectedApplication, model.Applications);
    }

    [Fact]
    public async Task AnApplicationGone_IsNoLongerSelected()
    {
        var reads = 0;
        var (model, _) = Connected(new FakeRepository
        {
            GetApplicationConnectionsAsync = () => Task.FromResult<IEnumerable<ApplicationConnection>>(
                ++reads == 1 ? [App("OrderService", 120), App("BillingService", 64)] : [App("OrderService", 120)]),
        });
        await model.OnNavigatedToAsync();
        model.SelectedApplication = model.Applications.Single(a => a.ApplicationName == "BillingService");

        await model.RefreshAsync();

        Assert.Null(model.SelectedApplication);
    }
}
