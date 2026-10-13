using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Config Checks' view model on its own (#186): no page, no STA thread, a fake repository. It
/// reads through its lifecycle, filters what it read, keeps the check selected across a refresh,
/// says what it couldn't read once, stops reading when the page is left, and scripts the warnings.
/// </summary>
public sealed class ConfigChecksViewModelTests
{
    private static readonly ConfigCheck[] Checks = ConfigCheckList.Sort(
    [
        new(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, "0 (all 16 cores)", "8",
            "With MAXDOP 0 one query can take every core.", "EXEC sp_configure 'max degree of parallelism', 8;"),
        new(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, "2,147,483,647 MB (unlimited)", "28,672 MB",
            "Unlimited, SQL Server can take memory Windows needs.", "EXEC sp_configure 'max server memory (MB)', 28672;"),
        new(ConfigCheckKind.CostThreshold, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, "50", "50 or more", "Fine."),
        new(ConfigCheckKind.AutoShrink, "All 12 databases", ConfigCheckStatus.Pass, "OFF", "OFF", "Fine."),
    ]).ToArray();

    private static FakeRepository Repo(ConfigCheck[]? checks = null) =>
        new() { GetConfigurationChecksAsync = () => Task.FromResult(new ConfigCheckList(checks ?? Checks, null)) };

    private static (ConfigChecksViewModel Model, FakeShellStatus Shell, FakeDialogService Dialogs) Connected(FakeRepository repo)
    {
        var dialogs = new FakeDialogService();
        var model   = new ConfigChecksViewModel(repo, dialogs, "PROD-SQL01");
        var shell   = new FakeShellStatus();
        model.Connect("ConfigChecks", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell, dialogs);
    }

    [Fact]
    public async Task Loaded_ListsEveryCheck_WarningsFirst_TheFirstSelected()
    {
        var (model, shell, _) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal(4, model.Checks.Count);
        Assert.Equal("4 results", model.CountText);
        Assert.Same(model.Checks[0], model.SelectedCheck);
        Assert.Equal((VerdictLevel.Warning, "2 of 4 checks fail; the riskiest: Max server memory (Server)"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.StartsWith("Checked at ", model.ReadNote);
        Assert.False(model.ShowsNoData);
        Assert.True(model.ScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFilter_ListsWhatItKeeps_AndSaysWhyWhenNothing()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();

        model.FilterIndex = ConfigChecksViewModel.WarningsFilter;
        Assert.Equal("2 of 4 results", model.CountText);
        Assert.All(model.Checks, c => Assert.True(c.IsWarning));

        model.FilterIndex = 4;   // not checked: none
        Assert.Empty(model.Checks);
        Assert.Null(model.SelectedCheck);
        Assert.True(model.ShowsNoData);
        Assert.Equal("Every check could be made.", model.NoDataText);
    }

    [Fact]
    public async Task Empty_SaysSo_AndHasNothingToScript()
    {
        var (model, _, _) = Connected(Repo([]));

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Checks);
        Assert.True(model.ShowsNoData);
        Assert.False(model.ScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell, _) = Connected(new FakeRepository
        {
            GetConfigurationChecksAsync = () => Task.FromException<ConfigCheckList>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the server's configuration", "Error: Timeout expired."), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ARefreshThatFails_KeepsWhatWasRead()
    {
        var repo = Repo();
        var (model, shell, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        var verdict = model.Verdict;

        repo.GetConfigurationChecksAsync = () => Task.FromException<ConfigCheckList>(new InvalidOperationException("Timeout expired."));
        await model.RefreshAsync();

        Assert.Equal("Error Refresh: Timeout expired.", shell.Reports[^1]);
        Assert.Same(verdict, model.Verdict);
        Assert.Equal(4, model.Checks.Count);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<ConfigCheckList>();
        var repo = new FakeRepository { GetConfigurationChecksAsync = () => reading.Task };
        var (model, shell, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetConfigurationChecksAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
        Assert.Empty(model.Checks);
    }

    [Fact]
    public async Task TheCheckSelected_IsKeptAcrossARefresh_ByItsKindAndScope()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedCheck = model.Checks.Single(c => c.Kind == ConfigCheckKind.CostThreshold);

        await model.RefreshAsync();

        Assert.Equal(ConfigCheckKind.CostThreshold, model.SelectedCheck!.Kind);
        Assert.Contains(model.SelectedCheck, model.Checks);
    }

    [Fact]
    public async Task ShowIt_SelectsTheRiskiest_PastAFilterThatHidesIt()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.FilterIndex = 3;   // database settings: hides it
        var shown = new List<ConfigCheck>();
        model.ShowRow += shown.Add;

        await model.ActCommand.ExecuteAsync(new VerdictAction(ConfigChecksVerdict.ShowCheck, "Show it"));

        Assert.Equal(ConfigChecksViewModel.WarningsFilter, model.FilterIndex);
        Assert.Equal(ConfigCheckKind.MaxServerMemory, model.SelectedCheck!.Kind);
        Assert.Equal([model.SelectedCheck], shown);
    }

    [Fact]
    public async Task ScriptTheWarnings_ShowsOneScript_ForReview()
    {
        var (model, _, dialogs) = Connected(Repo());
        await model.OnNavigatedToAsync();

        model.ScriptCommand.Execute(null);

        var script = Assert.Single(dialogs.Scripts);
        Assert.Equal("Configuration Fixes", script.Heading);
        Assert.StartsWith("Review before running. 2 changes, one per warning.", script.Notice);
        Assert.Contains("PROD-SQL01", script.Text);
        Assert.Contains("EXEC sp_configure 'max server memory (MB)', 28672;", script.Text);
        Assert.StartsWith("ConfigurationFixes_", script.FileName);
    }
}
