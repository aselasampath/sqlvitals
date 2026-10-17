using System.Globalization;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;
using static SqlVitals.Desktop.Tests.TemplateEverywhereTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Index Health's view model on its own (#187): no page, no STA thread, a fake repository. It reads
/// the four lists, keeps each grid's row selected across a refresh, checks once whether the server
/// rebuilds online, reads the fragmentation again on its own (cancelling a read of it still
/// running, and when the page is left), says what's wrong with the criteria, scripts what is
/// selected or shown, and says once what it couldn't read.
/// </summary>
public sealed class IndexHealthViewModelTests
{
    private static readonly DateTime Now = DateTime.Now;

    private static IndexFragmentation Frag(string index, double pct) =>
        new("dbo", "Orders", index, "NONCLUSTERED INDEX", pct, 120_000, 71.2, 48_000_000, "SalesDb", pct >= 30 ? "Rebuild" : "Reorganize", Now);

    private static UnusedIndex Unused(string index, bool unique = false) =>
        new("SalesDb", "dbo", "Orders", index, "NONCLUSTERED", unique, false, false, 0, 0, 0, 912_000, null, null, Now, 0, 48_000_000, Now);

    private static FakeRepository Repo() => new()
    {
        GetIndexHealthAsync = () => Task.FromResult<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>((
            [Missing("OrderLines", 54_000, "WARNING", 3_100), Missing("Orders", 310_000, "CRITICAL", 18_400), Missing("Notes", 900, "INFO", 12)],
            [Unused("IX_Orders_Legacy"), Unused("UQ_Orders_Ref", unique: true)])),
        GetIndexUsagePatternsAsync = () => Task.FromResult<IEnumerable<IndexUsagePattern>>(
            [new("Orders", "PK_Orders", "CLUSTERED", 4_000_000, 120, 0, 900_000, Now, Now, null, Now, "Healthy", "SalesDb", Now)]),
        SupportsOnlineIndexRebuildAsync = () => Task.FromResult(true),
        GetIndexFragmentationAsync = (_, _) => Task.FromResult<IEnumerable<IndexFragmentation>>([Frag("IX_Orders_Date", 41.5), Frag("IX_Orders_Cust", 18)]),
    };

    private static (IndexHealthViewModel Model, FakeShellStatus Shell, FakeDialogService Dialogs) Connected(FakeRepository repo)
    {
        var dialogs = new FakeDialogService();
        var model   = new IndexHealthViewModel(repo, repo, dialogs);
        var shell   = new FakeShellStatus();
        model.Connect("IndexHealth", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell, dialogs);
    }

    [Fact]
    public async Task Loaded_ListsEachTab_CriticalFirst_AndOffersOnlineRebuilds()
    {
        var (model, shell, _) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(["Orders", "OrderLines", "Notes"], model.Missing.Select(m => m.TableName));
        Assert.Equal(2, model.Unused.Count);
        Assert.Single(model.Usage);
        Assert.Equal(2, model.Fragmentation.Count);
        Assert.StartsWith("2 indexes with ≥ 10% fragmentation and ≥ 1,000 pages · updated ", model.FragmentationStatus);
        Assert.False(model.FragmentationFailed);
        Assert.True(model.CanRebuildOnline);
        Assert.True(model.OnlineRebuild);
        Assert.Equal((VerdictLevel.Warning, "3 missing indexes suggested, 1 critical; the most valuable on SalesDb.dbo.Orders"),
                     (model.Verdict!.Level, model.Verdict.Headline));
    }

    [Fact]
    public async Task TheEditionIsAskedOnce_AndAFailureAsksAgainNextTime()
    {
        var asked = 0;
        var repo  = Repo();
        repo.SupportsOnlineIndexRebuildAsync = () => ++asked == 1 ? Task.FromException<bool>(new InvalidOperationException("no")) : Task.FromResult(false);
        var (model, _, _) = Connected(repo);

        await model.OnNavigatedToAsync();
        Assert.False(model.CanRebuildOnline);
        Assert.StartsWith("Could not read the server edition", model.OnlineRebuildToolTip);

        await model.RefreshAsync();
        await model.RefreshAsync();
        Assert.Equal(2, asked);
        Assert.StartsWith("This server's edition does not support online index rebuilds", model.OnlineRebuildToolTip);
    }

    [Fact]
    public async Task ARefresh_KeepsEachGridsRowSelected()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedMissing       = model.Missing.Single(m => m.TableName == "Notes");
        model.SelectedUnused        = model.Unused[1];
        model.SelectedUsage         = model.Usage[0];
        model.SelectedFragmentation = model.Fragmentation[1];

        await model.RefreshAsync();

        Assert.Contains(model.SelectedMissing!, model.Missing);
        Assert.Equal("Notes", model.SelectedMissing!.TableName);
        Assert.Equal("UQ_Orders_Ref", model.SelectedUnused!.IndexName);
        Assert.Equal("PK_Orders", model.SelectedUsage!.IndexName);
        Assert.Equal("IX_Orders_Cust", model.SelectedFragmentation!.IndexName);
    }

    [Fact]
    public async Task Empty_HasNothingToScript()
    {
        var (model, _, dialogs) = Connected(new FakeRepository
        {
            GetIndexHealthAsync = () => Task.FromResult<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>(([], [])),
            GetIndexUsagePatternsAsync = () => Task.FromResult<IEnumerable<IndexUsagePattern>>([]),
            SupportsOnlineIndexRebuildAsync = () => Task.FromResult(false),
            GetIndexFragmentationAsync = (_, _) => Task.FromResult<IEnumerable<IndexFragmentation>>([]),
        });

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Missing);
        Assert.StartsWith("0 indexes with ", model.FragmentationStatus);
        Assert.Equal(VerdictLevel.Ok, model.Verdict!.Level);
        model.CreateScriptCommand.Execute(null);
        Assert.Empty(dialogs.Scripts);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var repo = Repo();
        repo.GetIndexHealthAsync = () => Task.FromException<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>(new InvalidOperationException("Timeout expired."));
        var (model, shell, _) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the index statistics", "Error: Timeout expired."), model.Verdict);
    }

    [Fact]
    public async Task AFragmentationFailure_IsSaidInItsStatus_AndInTheTopBar()
    {
        var repo = Repo();
        repo.GetIndexFragmentationAsync = (_, _) => Task.FromException<IEnumerable<IndexFragmentation>>(new InvalidOperationException("Timeout expired."));
        var (model, shell, _) = Connected(repo);

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.True(model.FragmentationFailed);
        Assert.Equal("Error: Timeout expired. — the grid still shows the previous results.", model.FragmentationStatus);
        Assert.False(model.ReadingFragmentation);
        // The missing and unused indexes were read: the verdict judges them, as a good read does.
        var (good, _, _) = Connected(Repo());
        await good.OnNavigatedToAsync();
        Assert.Equal(good.Verdict, model.Verdict);
        Assert.Equal(3, model.Missing.Count);
    }

    [Fact]
    public async Task TheFragmentation_ReadsAgainOnItsOwn_WithTheCriteriaTyped()
    {
        var asked = new List<(double, long)>();
        var repo  = Repo();
        repo.GetIndexFragmentationAsync = (pct, pages) => { asked.Add((pct, pages)); return Task.FromResult<IEnumerable<IndexFragmentation>>([Frag("IX_Orders_Date", 41.5)]); };
        var (model, shell, _) = Connected(repo);
        await model.OnNavigatedToAsync();

        model.MinFragText  = "30";
        model.MinPagesText = (5000).ToString("N0", CultureInfo.CurrentCulture);
        await model.RefreshFragmentationCommand.ExecuteAsync(null);

        Assert.Equal([(10d, 1000L), (30d, 5000L)], asked);
        Assert.Single(model.Fragmentation);
        Assert.Equal(["Read Load"], shell.Reports);   // its own read isn't the page's
        // The loading bar says what it reads, in its own words (#117).
        Assert.Equal(["Load", "Load done", "Checking fragmentation", "Checking fragmentation done"], shell.Readings);

        // A box that can't be used: said, its box named, and nothing read.
        string? invalid = null;
        model.InvalidField += f => invalid = f;
        model.MinFragText = "200";
        await model.RefreshFragmentationCommand.ExecuteAsync(null);
        Assert.Equal(2, asked.Count);
        Assert.True(model.FragmentationFailed);
        Assert.Equal("Min Frag % must be a number greater than 0 and at most 100.", model.FragmentationStatus);
        Assert.Equal(IndexHealthViewModel.MinFragField, invalid);

        // Refresh falls back to the defaults, and puts them in the boxes.
        await model.RefreshAsync();
        Assert.Equal((10d, 1000L), asked[^1]);
        Assert.Equal("10", model.MinFragText);
    }

    [Fact]
    public async Task ItsOwnRead_IsCancelled_ByARefresh_AndByLeaving()
    {
        var reads = new List<TaskCompletionSource<IEnumerable<IndexFragmentation>>>();
        var repo  = Repo();
        repo.GetIndexFragmentationAsync = (_, _) =>
        {
            var done = new TaskCompletionSource<IEnumerable<IndexFragmentation>>();
            reads.Add(done);
            return done.Task;
        };
        var (model, _, _) = Connected(repo);
        var shown = model.OnNavigatedToAsync();
        reads[0].SetResult([Frag("IX_A", 50)]);
        await shown;

        // Its own read, still running, while a Refresh reads it again: only the Refresh's lands.
        var own = model.RefreshFragmentationCommand.ExecuteAsync(null);
        Assert.True(model.ReadingFragmentation);
        Assert.False(model.RefreshFragmentationCommand.CanExecute(null));
        var ownToken = repo.Tokens[nameof(FakeRepository.GetIndexFragmentationAsync)];
        var refresh = model.RefreshAsync();
        Assert.True(ownToken.IsCancellationRequested);
        reads[2].SetResult([Frag("IX_Refreshed", 50)]);
        await refresh;
        reads[1].SetResult([Frag("IX_Stale", 50)]);
        await own;
        Assert.Equal(["IX_Refreshed"], model.Fragmentation.Select(f => f.IndexName));
        Assert.False(model.ReadingFragmentation);

        // Left while its own read runs: cancelled, and nothing lands.
        var again = model.RefreshFragmentationCommand.ExecuteAsync(null);
        var token = repo.Tokens[nameof(FakeRepository.GetIndexFragmentationAsync)];
        model.OnNavigatedFrom();
        Assert.True(token.IsCancellationRequested);
        reads[3].SetCanceled(token);
        await again;
        Assert.Equal(["IX_Refreshed"], model.Fragmentation.Select(f => f.IndexName));
    }

    [Fact]
    public async Task StoppingItsOwnRead_StopsOnlyThat_KeepsTheRows_AndSaysSo()
    {
        var reads = new List<TaskCompletionSource<IEnumerable<IndexFragmentation>>>();
        var repo  = Repo();
        repo.GetIndexFragmentationAsync = (_, _) =>
        {
            var done = new TaskCompletionSource<IEnumerable<IndexFragmentation>>();
            reads.Add(done);
            return done.Task;
        };
        var (model, shell, _) = Connected(repo);
        var shown = model.OnNavigatedToAsync();
        reads[0].SetResult([Frag("IX_A", 50)]);
        await shown;

        var own   = model.RefreshFragmentationCommand.ExecuteAsync(null);
        var token = repo.Tokens[nameof(FakeRepository.GetIndexFragmentationAsync)];
        shell.Stops[^1]!();
        Assert.True(token.IsCancellationRequested);
        reads[1].SetCanceled(token);
        await own;

        Assert.Equal("Stopped — the grid still shows the previous results.", model.FragmentationStatus);
        Assert.False(model.FragmentationFailed);
        Assert.False(model.ReadingFragmentation);
        Assert.Equal(["IX_A"], model.Fragmentation.Select(f => f.IndexName));
        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal("Checking fragmentation ended", shell.Readings[^1]);

        // The next check reads as usual.
        var next = model.RefreshFragmentationCommand.ExecuteAsync(null);
        reads[2].SetResult([Frag("IX_B", 50)]);
        await next;
        Assert.StartsWith("1 index with ", model.FragmentationStatus);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>();
        var repo = Repo();
        repo.GetIndexHealthAsync = () => reading.Task;
        var (model, shell, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetIndexHealthAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task TheScripts_CoverTheRowsSelected_OrThoseShown_InTheirOrder()
    {
        var (model, _, dialogs) = Connected(Repo());
        await model.OnNavigatedToAsync();

        // None selected: every row shown, in the grid's order.
        model.MissingScope.Update([], 2, filtered: true);
        Assert.Equal("Generate CREATE Script (2 shown)", model.MissingScope.ButtonText);
        model.CreateScriptCommand.Execute(new[] { model.Missing[2], model.Missing[0] });
        Assert.StartsWith("Review before running. 2 index(es) will be created.", dialogs.Scripts[^1].Notice);
        Assert.True(dialogs.Scripts[^1].Text.IndexOf("Notes", StringComparison.Ordinal) < dialogs.Scripts[^1].Text.IndexOf("[Orders]", StringComparison.Ordinal));

        // Selected: those only. A unique index is left commented out.
        model.UnusedScope.Update([model.Unused[1]], 2, filtered: false);
        Assert.Equal("Generate DROP Script (1 selected)", model.UnusedScope.ButtonText);
        model.DropScriptCommand.Execute(model.Unused);
        Assert.StartsWith("Review before running. 0 index(es) will be dropped; 1 unique index(es) are commented out.", dialogs.Scripts[^1].Notice);

        model.FragmentationScope.Update([], 2, filtered: false);
        Assert.Equal("Generate Maintenance Script (all)", model.FragmentationScope.ButtonText);
        Assert.True(model.FragmentationScope.HasRows);
        model.MaintenanceScriptCommand.Execute(null);
        Assert.StartsWith("Review before running. 1 index(es) will be rebuilt and 1 reorganized, rebuilds using ONLINE = ON.", dialogs.Scripts[^1].Notice);
        Assert.Equal(3, dialogs.Scripts.Count);
    }

    [Fact]
    public async Task TheMaintenanceScript_SaysWhatsWrongWithItsThresholds()
    {
        var (model, _, dialogs) = Connected(Repo());
        await model.OnNavigatedToAsync();
        string? invalid = null;
        model.InvalidField += f => invalid = f;

        model.ReorganizeText = "40";   // above the rebuild threshold
        model.MaintenanceScriptCommand.Execute(null);

        Assert.Empty(dialogs.Scripts);
        Assert.True(model.FragmentationFailed);
        Assert.Equal("Rebuild from % must be at least the reorganize threshold — a rebuild is the heavier fix.", model.FragmentationStatus);
        Assert.Equal(IndexHealthViewModel.RebuildField, invalid);
    }

    [Fact]
    public async Task ShowIt_OpensTheMissingTab_OnTheMostValuable()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        MissingIndex? shown = null;
        model.ShowRow += m => shown = m;
        model.TabIndex = IndexHealthViewModel.FragmentationTab;

        await model.ActCommand.ExecuteAsync(new VerdictAction(IndexHealthVerdict.ShowMissing, "Show it"));

        Assert.Equal(IndexHealthViewModel.MissingTab, model.TabIndex);
        Assert.Equal("Orders", model.SelectedMissing!.TableName);
        Assert.Same(model.SelectedMissing, shown);

        await model.ActCommand.ExecuteAsync(new VerdictAction(IndexHealthVerdict.ShowUnused, "Unused"));
        Assert.Equal(IndexHealthViewModel.UnusedTab, model.TabIndex);
    }
}
