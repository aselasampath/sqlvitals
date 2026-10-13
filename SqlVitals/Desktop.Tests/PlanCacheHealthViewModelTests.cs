using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Plan Cache Health's view model on its own (#185): no page, no STA thread, a fake repository. It
/// reads the checks and the implicit conversions together, shows the checks when only the
/// conversions fail, says what it couldn't read once, stops reading when the page is left, and
/// keeps the conversion selected across a refresh.
/// </summary>
public sealed class PlanCacheHealthViewModelTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 30, 0);

    private static readonly ImplicitConversion[] Conversions =
    [
        new("SalesDb", "", 3_410, 40, "SELECT * FROM dbo.Orders WHERE OrderRef = @p0", Now),
        new("SalesDb", "usp_FindCustomer", 48_120, 96, "SELECT CustomerId FROM dbo.Customers WHERE Email = @email", Now),
        new("HR", "usp_GetEmployee", 120, 64, "SELECT * FROM dbo.Employees WHERE BadgeNo = @badge", Now),
    ];

    private static (PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>, IEnumerable<CardinalityMisestimate>,
                    IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>) Health(PlanCacheHealthSummary? summary = null) =>
        (summary ?? new PlanCacheHealthSummary(false, 4, 18_420, 0, 2, "WARNING"),
         [new(42, 1_204, "0x8F3A1B2C4D5E6F70", "SELECT * FROM dbo.Orders WHERE OrderId = 1001", "SalesDb", "WARNING")],
         [new("Key Lookup", 81_200, 9_410_000, 115.9, "SalesDb", "usp_GetOrderHistory", "SELECT o.OrderId FROM dbo.Orders o WHERE CustomerId = @c")],
         [], [], []);

    private static FakeRepository Repo(PlanCacheHealthSummary? summary = null) => new()
    {
        GetPlanCacheHealthAsync     = () => Task.FromResult(Health(summary)),
        GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>(Conversions),
    };

    private static (PlanCacheHealthViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo, string? view = null)
    {
        var model = new PlanCacheHealthViewModel(repo, repo, view);
        var shell = new FakeShellStatus();
        model.Connect("PlanCacheHealth", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell);
    }

    [Fact]
    public async Task Loaded_ShowsTheSummaryAsCards_EveryCheck_AndTheConversions()
    {
        var (model, shell) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(["Plan Cache", "Total Plans", "Multi-Plan Queries", "Plan Guides", "Severity"], model.Summary.Select(c => c.Label));
        Assert.Equal(["OK", $"{18_420:N0}", "4", "0", "WARNING"], model.Summary.Select(c => c.Value));
        Assert.Equal([KpiCard.Tones.Success, KpiCard.Tones.Accent, KpiCard.Tones.Warning, KpiCard.Tones.Success, KpiCard.Tones.Warning],
                     model.Summary.Select(c => c.Tone));
        Assert.Equal($"Plan cache OK · {18_420:N0} plans · 4 multi-plan queries · 0 plan guides · WARNING", model.FoldedSummary);
        Assert.False(model.CacheErased);
        Assert.Single(model.MultiPlanQueries);
        Assert.Single(model.KeyLookups);
        Assert.Empty(model.Misestimates);
        Assert.Equal(3, model.Conversions.Count);
        Assert.False(model.ShowsConversionsProblem);
        Assert.Equal((VerdictLevel.Warning, "4 queries have more than 5 cached plans each"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.Null(model.Status);
    }

    [Fact]
    public async Task ACacheErased_ShowsTheBanner_AndTheCardsInDanger()
    {
        var (model, _) = Connected(Repo(new PlanCacheHealthSummary(true, 12, 900, 0, 0, "CRITICAL")));

        await model.OnNavigatedToAsync();

        Assert.True(model.CacheErased);
        Assert.Equal(("ERASED", KpiCard.Tones.Danger), (model.Summary[0].Value, model.Summary[0].Tone));
        Assert.Equal(KpiCard.Tones.Danger, model.Summary[^1].Tone);
    }

    [Fact]
    public async Task Empty_ShowsNoRows_AndAHealthyCache()
    {
        var (model, _) = Connected(new FakeRepository
        {
            GetPlanCacheHealthAsync = () => Task.FromResult<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                              IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>((
                new PlanCacheHealthSummary(false, 0, 120, 0, 0, "OK"), [], [], [], [], [])),
            GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>([]),
        });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.MultiPlanQueries);
        Assert.Empty(model.Conversions);
        Assert.Equal((VerdictLevel.Ok, "The plan cache looks healthy"), (model.Verdict!.Level, model.Verdict.Headline));
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var repo = Repo();
        repo.GetPlanCacheHealthAsync = () => Task.FromException<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                                   IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>(
            new InvalidOperationException("Timeout expired."));
        var (model, shell) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the plan cache", "Error: Timeout expired."), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task TheChecksFailing_StopTheConversionsSearch_AndWaitForIt_BeforeSayingSo()
    {
        // The plan XML search is the slow one: left running, nothing could stop it once the read ended.
        var converting = new TaskCompletionSource<IEnumerable<ImplicitConversion>>();
        var repo = Repo();
        repo.GetPlanCacheHealthAsync = () => Task.FromException<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                                   IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>(
            new InvalidOperationException("Timeout expired."));
        repo.GetImplicitConversionsAsync = () => converting.Task;
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();

        var token = repo.Tokens[nameof(FakeRepository.GetImplicitConversionsAsync)];
        Assert.True(token.IsCancellationRequested);
        Assert.False(shown.IsCompleted);
        converting.SetCanceled(token);
        await shown;
        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the plan cache", "Error: Timeout expired."), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task TwoPlansOfTheSameText_TheOneSelectedIsKept()
    {
        // The same ad-hoc text cached twice (other SET options): told apart by their size.
        ImplicitConversion[] twice =
        [
            new("SalesDb", null, 900, 40, "SELECT * FROM dbo.Orders WHERE OrderRef = @p0", Now),
            new("SalesDb", null, 300, 56, "SELECT * FROM dbo.Orders WHERE OrderRef = @p0", Now),
        ];
        var repo = Repo();
        repo.GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>(twice.Select(c => c with { }).ToList());
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedConversion = model.Conversions[1];

        await model.RefreshAsync();

        Assert.Equal(56, model.SelectedConversion!.PlanSizeKB);
    }

    [Fact]
    public async Task TheConversionsUnread_TheirTabSaysSo_AndTheRestShows_UntilARefreshReadsThem()
    {
        // A plan XML search can time out on a very large cache: the checks are still shown, and nothing is reported.
        var repo = Repo(new PlanCacheHealthSummary(false, 0, 18_420, 0, 0, "OK"));
        repo.GetImplicitConversionsAsync = () => Task.FromException<IEnumerable<ImplicitConversion>>(new InvalidOperationException("Timeout expired."));
        var (model, shell) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal("No parameterisation problems; 1 busy plan does key lookups", model.Verdict!.Headline);
        Assert.Equal(5, model.Summary.Count);
        Assert.Empty(model.Conversions);
        Assert.True(model.ShowsConversionsProblem);
        Assert.Equal("Couldn't read the implicit conversions: Error: Timeout expired. Refresh to try again.", model.ConversionsProblem);

        repo.GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>(Conversions);
        await model.RefreshAsync();

        Assert.False(model.ShowsConversionsProblem);
        Assert.Equal(3, model.Conversions.Count);
    }

    [Fact]
    public async Task TheChecksAndTheConversions_AreReadAtTheSameTime()
    {
        var health = new TaskCompletionSource<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                               IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>();
        var repo = Repo();
        repo.GetPlanCacheHealthAsync = () => health.Task;
        var (model, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();

        Assert.True(repo.Tokens.ContainsKey(nameof(FakeRepository.GetImplicitConversionsAsync)));
        health.SetResult(Health());
        await shown;
        Assert.Equal(3, model.Conversions.Count);
    }

    [Fact]
    public async Task Leaving_CancelsBothReads_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>();
        var converting = new TaskCompletionSource<IEnumerable<ImplicitConversion>>();
        var repo = new FakeRepository { GetPlanCacheHealthAsync = () => reading.Task, GetImplicitConversionsAsync = () => converting.Task };
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var tokens = new[] { repo.Tokens[nameof(FakeRepository.GetPlanCacheHealthAsync)], repo.Tokens[nameof(FakeRepository.GetImplicitConversionsAsync)] };
        model.OnNavigatedFrom();

        Assert.All(tokens, token => Assert.True(token.IsCancellationRequested));
        converting.SetCanceled(tokens[1]);
        reading.SetCanceled(tokens[0]);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
        Assert.False(model.ShowsConversionsProblem);
        Assert.Empty(model.Conversions);
    }

    [Fact]
    public async Task TheConversionSelected_IsKeptAcrossARefresh()
    {
        var reads = 0;
        var repo = Repo();
        // Each read is new rows: the same plans, run more.
        repo.GetImplicitConversionsAsync = () => Task.FromResult<IEnumerable<ImplicitConversion>>(
            Conversions.Select(c => c with { ExecutionCount = c.ExecutionCount + ++reads }).ToList());
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        model.SelectedConversion = model.Conversions.Single(c => c.ObjectName == "usp_GetEmployee");
        var rows = model.Conversions;

        await model.RefreshAsync();

        Assert.Same(rows, model.Conversions);
        Assert.Equal("usp_GetEmployee", model.SelectedConversion!.ObjectName);
        Assert.Contains(model.SelectedConversion, model.Conversions);
    }

    [Fact]
    public async Task ShowATab_OpensIt()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedTab = PlanCacheHealthVerdict.PlanGuidesTab;

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == PlanCacheHealthVerdict.ShowTab));

        Assert.Equal(PlanCacheHealthVerdict.MultiPlanTab, model.SelectedTab);
    }

    [Fact]
    public async Task ShowIt_OpensTheConversionsTab_SelectsTheMostUsed_AndAsksTheViewToShowIt()
    {
        // Nothing worse than the plans converting implicitly: the verdict is about them.
        var repo = Repo();
        repo.GetPlanCacheHealthAsync = () => Task.FromResult<(PlanCacheHealthSummary, IEnumerable<MultiPlanQuery>, IEnumerable<KeyLookupQuery>,
                                                               IEnumerable<CardinalityMisestimate>, IEnumerable<SkewedParallelQuery>, IEnumerable<PlanGuide>)>((
            new PlanCacheHealthSummary(false, 0, 18_420, 0, 0, "OK"), [], [], [], [], []));
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        object? shown = null;
        model.ShowRow += row => shown = row;

        await model.ActCommand.ExecuteAsync(model.Verdict!.Actions.Single(a => a.Key == PlanCacheHealthVerdict.ShowWorstConversion));

        Assert.Equal(PlanCacheHealthVerdict.ImplicitConversionsTab, model.SelectedTab);
        Assert.Equal("usp_FindCustomer", model.SelectedConversion!.ObjectName);
        Assert.Same(model.SelectedConversion, shown);
    }

    [Fact]
    public void AnOldImplicitConvLink_OpensOnTheConversionsTab()
    {
        Assert.Equal(PlanCacheHealthVerdict.ImplicitConversionsTab, Connected(Repo(), NavMenu.ImplicitConversionsView).Model.SelectedTab);
        Assert.Equal(PlanCacheHealthVerdict.MultiPlanTab, Connected(Repo()).Model.SelectedTab);
    }
}
