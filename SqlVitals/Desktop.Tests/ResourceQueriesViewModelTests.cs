using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Resource Queries' view model on its own (#186): no page, no STA thread, a fake repository. It
/// reads as many queries as the row limit asks, keeps each tab's selection across a refresh, lists
/// only the queries run since a CPU alert's window, says what it couldn't read once, stops reading
/// when the page is left, and opens a row's plan.
/// </summary>
public sealed class ResourceQueriesViewModelTests
{
    private static readonly DateTime Now = DateTime.Now;

    private static ResourceIntensiveQuery Query(string? obj, string text, long cpu, long reads, DateTime? lastRun = null) =>
        new(text, "SalesDb", obj, 100, reads, reads / 100, cpu, cpu / 100, cpu * 2, cpu / 50, 0, 0,
            lastRun ?? Now.AddMinutes(-5), Now.AddHours(-5), Now, "<plan/>", reads / 100, obj is null ? "Ad-Hoc" : "Stored Proc");

    private static readonly ResourceIntensiveQuery[] Queries =
    [
        Query("dbo.usp_GetOrderHistory", "SELECT o.OrderId FROM dbo.Orders o", 812_000_000, 98_120_000),
        Query(null, "SELECT * FROM dbo.Stock WHERE Sku LIKE @p0", 241_000_000, 410_220_000, Now.AddHours(-2)),
        Query("dbo.usp_PriceBasket", "EXEC dbo.usp_PriceBasket @BasketId", 160_000_000, 12_040_000),
    ];

    private static FakeRepository Repo(ResourceIntensiveQuery[]? queries = null, List<int>? topNs = null)
    {
        var rows = queries ?? Queries;
        return new FakeRepository
        {
            GetResourceIntensiveQueriesAsync = topN =>
            {
                topNs?.Add(topN);
                return Task.FromResult<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
                    (rows.OrderByDescending(q => q.TotalLogicalReads), rows.OrderByDescending(q => q.TotalCPUTime), rows.OrderByDescending(q => q.TotalLogicalReads)));
            },
            GetObjectQueryPlanAsync = _ => Task.FromResult<string?>("<object plan/>"),
        };
    }

    private static (ResourceQueriesViewModel Model, FakeShellStatus Shell, FakeDialogService Dialogs) Connected(FakeRepository repo)
    {
        var dialogs = new FakeDialogService();
        var model   = new ResourceQueriesViewModel(repo, dialogs, TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow));
        var shell   = new FakeShellStatus();
        model.Connect("ResourceQueries", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell, dialogs);
    }

    [Fact]
    public async Task Loaded_FillsEachTab_AndJudgesTheCostliest()
    {
        var (model, shell, _) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Contains("Read Load", shell.Reports);
        Assert.Equal(3, model.ByReads.Count);
        Assert.Equal("dbo.usp_GetOrderHistory", model.ByCpu[0].ObjectName);
        Assert.Equal(3, model.ByHighestReads.Count);
        Assert.Equal("dbo.usp_GetOrderHistory uses the most CPU: 67 % of the top 3 queries'", model.Verdict!.Headline);
        Assert.Null(model.FocusNote);
    }

    [Fact]
    public async Task Empty_ListsNothing()
    {
        var (model, _, _) = Connected(Repo([]));

        await model.OnNavigatedToAsync();

        Assert.Empty(model.ByReads);
        Assert.Empty(model.ByCpu);
        Assert.NotNull(model.Verdict);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell, _) = Connected(new FakeRepository
        {
            GetResourceIntensiveQueriesAsync = _ => Task.FromException<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
                new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the plan cache's query statistics", "Error: Timeout expired."), model.Verdict);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>();
        var repo = new FakeRepository { GetResourceIntensiveQueriesAsync = _ => reading.Task };
        var (model, shell, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetResourceIntensiveQueriesAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task EachTabsSelection_IsKeptAcrossARefresh()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedByReads = model.ByReads[2];
        model.SelectedByCpu   = model.ByCpu[1];

        await model.RefreshAsync();

        Assert.Equal(Queries.OrderByDescending(q => q.TotalLogicalReads).ElementAt(2).QueryText, model.SelectedByReads!.QueryText);
        Assert.Contains(model.SelectedByReads, model.ByReads);
        Assert.Equal(Queries.OrderByDescending(q => q.TotalCPUTime).ElementAt(1).QueryText, model.SelectedByCpu!.QueryText);
        Assert.Null(model.SelectedByHighestReads);
    }

    [Fact]
    public async Task AnotherRowLimit_ReadsAgain_WithIt()
    {
        var topNs = new List<int>();
        var (model, _, _) = Connected(Repo(topNs: topNs));
        model.TopN = 50;   // before the page reads: its first read uses it
        await model.OnNavigatedToAsync();

        model.TopN = 100;
        await model.RereadAsync();

        Assert.Equal([50, 100, 100], topNs);
    }

    [Fact]
    public async Task ACpuAlert_ListsTheQueriesRunSinceItsWindow_UntilShowAll()
    {
        var (model, _, _) = Connected(Repo());
        var since = DateTime.UtcNow.AddMinutes(-20);
        model.FocusOnCpuSince(since);
        await model.OnNavigatedToAsync();

        Assert.Equal((int)ResourceQueriesViewModel.Tab.Cpu, model.SelectedTab);
        Assert.Equal(2, model.ByCpu.Count);
        Assert.Equal($"showing the 2 of the top 3 queries by CPU last run since {since.ToLocalTime():HH:mm}", model.FocusNote);
        Assert.Equal("Show all 3 queries", model.ShowAllText);

        model.ClearFocus();

        Assert.Equal(3, model.ByCpu.Count);
        Assert.Null(model.ShowAllText);
    }

    [Fact]
    public async Task ShowIt_OpensTheTab_OnItsTopQuery()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        var shown = new List<ResourceIntensiveQuery>();
        model.ShowRow += shown.Add;

        await model.ActCommand.ExecuteAsync(new VerdictAction(ResourceQueriesVerdict.ShowReads, "Show it"));

        Assert.Equal((int)ResourceQueriesViewModel.Tab.Reads, model.SelectedTab);
        Assert.Null(model.SelectedByReads!.ObjectName);
        Assert.Equal([model.SelectedByReads], shown);
    }

    [Fact]
    public async Task ViewPlan_EachRowReadsOnItsOwn_AndLeavingDropsAPlanStillBeingRead()
    {
        var repo  = Repo();
        var plan  = new TaskCompletionSource<string?>();
        repo.GetObjectQueryPlanAsync = _ => plan.Task;
        var (model, _, dialogs) = Connected(repo);
        await model.OnNavigatedToAsync();

        var viewing = model.ViewPlanCommand.ExecuteAsync(Queries[0]);
        Assert.True(model.ViewPlanCommand.CanExecute(Queries[2]));   // another row's button still works
        var token = repo.Tokens[nameof(FakeRepository.GetObjectQueryPlanAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        plan.SetResult("<object plan/>");
        await viewing;
        Assert.Empty(dialogs.Plans);
    }

    [Fact]
    public async Task ViewPlan_OpensTheProceduresPlan_OrTheStatements_AndSaysWhenItCant()
    {
        var repo = Repo();
        var (model, _, dialogs) = Connected(repo);
        await model.OnNavigatedToAsync();

        await model.ViewPlanCommand.ExecuteAsync(Queries[0]);
        await model.ViewPlanCommand.ExecuteAsync(Queries[1]);
        repo.GetObjectQueryPlanAsync = _ => Task.FromException<string?>(new InvalidOperationException("Timeout expired."));
        await model.ViewPlanCommand.ExecuteAsync(Queries[2]);

        Assert.Equal(["<object plan/>", "<plan/>"], dialogs.Plans);
        Assert.Equal("Warn: Plan Error: Failed to retrieve execution plan:\nTimeout expired.", dialogs.Shown[^1]);
    }
}
