using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Query Regressions' view model on its own (#187): no page, no STA thread, a fake repository. It
/// checks Query Store (or the monitoring history), says why a check can't be made, saves the
/// criteria typed, checks again when what is compared changes, keeps the query selected across a
/// check, opens a query's plans (dropping one still being read when the page is left), and says
/// once what it couldn't read.
/// </summary>
public sealed class QueryRegressionsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static FakeRepository Repo() => (FakeRepository)TemplateRolloutTests.RegressionsRepo();

    private sealed record Parts(QueryRegressionsViewModel Model, FakeShellStatus Shell, FakeDialogService Dialogs, FakeNavigator Navigator,
                                ConnectionSettingsService Settings);

    private Parts Connected(FakeRepository repo)
    {
        var (settings, dialogs, navigator) = (new ConnectionSettingsService(_dir), new FakeDialogService(), new FakeNavigator());
        var model = new QueryRegressionsViewModel(repo, null, settings, navigator, dialogs);
        var shell = new FakeShellStatus();
        model.Connect("QueryRegressions", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return new(model, shell, dialogs, navigator, settings);
    }

    // A check started by a change runs after the one in progress: waits for both.
    private static async Task Settled(QueryRegressionsViewModel model)
    {
        for (var i = 0; i < 200 && model.IsBusy; i++)
            await Task.Delay(10);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Loaded_FromQueryStore_ListsTheRegressedQueries_TheCostliestFirst()
    {
        var (model, shell, _, _, _) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Context", "Read Load"], shell.Reports);
        Assert.Equal(["4183", "2210"], model.Regressions.Select(r => r.QueryKey));
        Assert.Equal("2 regressed queries", model.CountText);
        Assert.True(model.FromQueryStore);
        Assert.Equal(("Last 24 hours", "the 7 days before"), (model.HistoryWindow, model.CompareWith));
        Assert.NotNull(model.DataTo);
        Assert.Contains("Query Store for SalesDb", model.ReadNote);
        Assert.Contains("Query Store · more than 50 % slower, at least", model.CriteriaSummary);
        Assert.False(model.ShowsNoData);
        Assert.Equal(VerdictLevel.Warning, model.Verdict!.Level);
    }

    [Fact]
    public async Task CriteriaThatCantBeUsed_SayWhy_AndNothingIsChecked()
    {
        var repo = Repo();
        var (model, _, _, _, _) = Connected(repo);
        model.ThresholdText = "lots";

        await model.OnNavigatedToAsync();

        Assert.True(model.ShowsCriteriaError);
        Assert.Empty(repo.Tokens);
        Assert.Empty(model.Regressions);

        model.ThresholdText = "150";   // 2210 went up by 105 %, 4183 by 310 %
        await model.CheckCommand.ExecuteAsync(null);
        Assert.False(model.ShowsCriteriaError);
        Assert.Equal("1 regressed query", model.CountText);
    }

    [Fact]
    public async Task CriteriaTyped_AreSaved_ForTheNextTime()
    {
        var (model, _, _, _, settings) = Connected(Repo());
        model.ThresholdText     = "80";
        model.MinExecutionsText = "5";

        await model.OnNavigatedToAsync();

        Assert.Equal(new RegressionCriteria(80, 5), settings.Load().RegressionCriteria);
        var again = new QueryRegressionsViewModel(Repo(), null, settings, new FakeNavigator(), new FakeDialogService());
        Assert.Equal(("80", "5"), (again.ThresholdText, again.MinExecutionsText));
    }

    [Fact]
    public async Task AnotherChoice_ChecksAgain_OnceThePageHasChecked()
    {
        var repo = Repo();
        var reads = 0;
        var stats = repo.GetQueryStoreRegressionStatsAsync!;
        repo.GetQueryStoreRegressionStatsAsync = w => { reads++; return stats(w); };
        var (model, _, _, _, _) = Connected(repo);

        model.BaselineIndex = 1;   // before the first check: it uses it
        Assert.Equal(0, reads);
        await model.OnNavigatedToAsync();
        Assert.Equal("the same time last week", model.CompareWith);

        // The monitoring history, which a connection that isn't saved hasn't got: not compared, and why.
        model.SourceIndex = 2;
        await Settled(model);
        Assert.Equal(1, reads);
        Assert.False(model.FromQueryStore);
        Assert.Empty(model.Regressions);
        Assert.Equal(ConnectionHistory.UnavailableReason, model.NoDataText);
    }

    [Fact]
    public async Task QueryStoreOff_SaysSo_WhenItIsTheSourceChosen()
    {
        var repo = Repo();
        repo.GetQueryStoreRegressionStatsAsync = _ => Task.FromResult(new QueryStoreRegressionStats("SalesDb", "OFF", false, []));
        var (model, _, _, _, _) = Connected(repo);
        model.SourceIndex = 1;   // Query Store

        await model.OnNavigatedToAsync();

        Assert.Equal("Query Store is off for SalesDb. Choose Auto or Monitoring history to check SqlVitals' monitoring history instead.", model.NoDataText);
        Assert.True(model.ShowsNoData);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var repo = Repo();
        repo.GetQueryStoreRegressionStatsAsync = _ => Task.FromException<QueryStoreRegressionStats>(new InvalidOperationException("Timeout expired."));
        var (model, shell, _, _, _) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the regression check", "Error: Timeout expired."), model.Verdict);
        Assert.Equal("", model.ReadNote);   // not "Checking: …"
    }

    [Fact]
    public async Task ACheck_KeepsTheQuerySelected()
    {
        var (model, _, _, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedRegression = model.Regressions[1];

        await model.RefreshAsync();

        Assert.Equal("2210", model.SelectedRegression!.QueryKey);
        Assert.Contains(model.SelectedRegression, model.Regressions);
    }

    [Fact]
    public async Task Leaving_CancelsTheCheck_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<QueryStoreRegressionStats>();
        var repo = Repo();
        repo.GetQueryStoreRegressionStatsAsync = _ => reading.Task;
        var (model, shell, _, _, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetQueryStoreRegressionStatsAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task APlan_OpensInAWindow_NowOrBefore()
    {
        var repo = Repo();
        var asked = new List<long>();
        var plan = repo.GetQueryStorePlanAsync!;
        repo.GetQueryStorePlanAsync = id => { asked.Add(id); return plan(id); };
        var (model, _, dialogs, _, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        var worst = model.Regressions[0];

        Assert.Equal(["Plan used most now (plan 902)", "Plan used most before (plan 311)"], model.PlansOf(worst).Select(p => p.Heading));
        await model.ViewRecentPlanCommand.ExecuteAsync(worst);
        await model.ViewBaselinePlanCommand.ExecuteAsync(worst);

        Assert.Equal([902L, 311L], asked);
        Assert.Equal(["Plan: Execution plan used most now (Query Store plan 902) · query 4183",
                      "Plan: Execution plan used most before (Query Store plan 311) · query 4183"], dialogs.Shown);
    }

    [Fact]
    public async Task APlanThatFails_SaysSo_AndOneStillReadWhenThePageIsLeft_IsDropped()
    {
        var repo = Repo();
        var (model, _, dialogs, _, _) = Connected(repo);
        await model.OnNavigatedToAsync();

        repo.GetQueryStorePlanAsync = _ => Task.FromException<string?>(new InvalidOperationException("Login failed; Password=secret"));
        await model.ViewRecentPlanCommand.ExecuteAsync(model.Regressions[0]);
        Assert.StartsWith("Warn: Plan Error: Failed to retrieve execution plan:", Assert.Single(dialogs.Shown));
        Assert.DoesNotContain("secret", dialogs.Shown[0]);

        var reading = new TaskCompletionSource<string?>();
        repo.GetQueryStorePlanAsync = _ => reading.Task;
        var viewing = model.ViewRecentPlanCommand.ExecuteAsync(model.Regressions[0]);
        var token = repo.Tokens[nameof(FakeRepository.GetQueryStorePlanAsync)];
        model.OnNavigatedFrom();
        Assert.True(token.IsCancellationRequested);
        reading.SetResult("<plan/>");
        await viewing;
        Assert.Single(dialogs.Shown);
    }

    [Fact]
    public async Task ShowItsPlans_SelectsTheWorstQuery_AndAQueryIdOpensQueryStore()
    {
        var (model, _, _, navigator, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        QueryRegression? shown = null;
        model.ShowRow += r => shown = r;
        model.SelectedRegression = model.Regressions[1];

        await model.ActCommand.ExecuteAsync(new VerdictAction(QueryRegressionsVerdict.ShowPlans, "Show its plans"));
        Assert.Equal("4183", model.SelectedRegression!.QueryKey);
        Assert.Same(model.SelectedRegression, shown);

        await model.OpenQueryCommand.ExecuteAsync(model.Regressions[1]);
        Assert.Equal(["QueryStore ⟵ Query Regressions: Query 2210"], navigator.Moves);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
