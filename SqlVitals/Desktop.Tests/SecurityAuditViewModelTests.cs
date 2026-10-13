using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Security Audit's view model on its own (#186): no page, no STA thread, a fake repository. It
/// reads through its lifecycle, filters what it read, keeps the finding selected across a refresh,
/// says what it couldn't read once, stops reading when the page is left, and scripts the risks.
/// </summary>
public sealed class SecurityAuditViewModelTests
{
    private static readonly SecurityFinding[] Findings = SecurityFindingList.Sort(
    [
        new(SecurityCheckKind.SysadminMember, SecurityFinding.ServerScope, @"CORP\svc_reports", SecurityFindingStatus.Risk, "Windows login · sysadmin",
            "A service account in sysadmin can do anything if it is compromised.", @"ALTER SERVER ROLE sysadmin DROP MEMBER [CORP\svc_reports];"),
        new(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, "sa", SecurityFindingStatus.Risk, "SQL login · enabled",
            "sa is the first login an attacker tries.", "ALTER LOGIN [sa] DISABLE;"),
        new(SecurityCheckKind.Trustworthy, "SalesDb", "SalesDb", SecurityFindingStatus.Pass, "OFF", "Fine."),
        new(SecurityCheckKind.OrphanedUser, "HrDb", "old_app", SecurityFindingStatus.Review, "SQL user · no login", "Nobody can log in as it."),
    ]).ToArray();

    private static FakeRepository Repo(SecurityFinding[]? findings = null) =>
        new() { GetSecurityAuditAsync = () => Task.FromResult(new SecurityFindingList(findings ?? Findings, null)) };

    private static (SecurityAuditViewModel Model, FakeShellStatus Shell, FakeDialogService Dialogs) Connected(FakeRepository repo)
    {
        var dialogs = new FakeDialogService();
        var model   = new SecurityAuditViewModel(repo, dialogs, "PROD-SQL01");
        var shell   = new FakeShellStatus();
        model.Connect("SecurityAudit", shell, new RefreshScheduler(TimeProvider.System, new FakeUiDispatcher()));
        return (model, shell, dialogs);
    }

    [Fact]
    public async Task Loaded_ListsEveryFinding_RisksFirst_TheFirstSelected()
    {
        var (model, shell, _) = Connected(Repo());

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal("4 findings", model.CountText);
        Assert.True(model.Findings[0].IsRisk);
        Assert.Same(model.Findings[0], model.SelectedFinding);
        Assert.Equal((VerdictLevel.Warning, "2 risks found; the highest: sa login (SQL login · enabled)"), (model.Verdict!.Level, model.Verdict.Headline));
        Assert.StartsWith("Audited at ", model.ReadNote);
        Assert.True(model.ScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task Empty_SaysSo_AndHasNothingToScript()
    {
        var (model, _, _) = Connected(Repo([]));

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Findings);
        Assert.True(model.ShowsNoData);
        Assert.Equal("There are no findings to show.", model.NoDataText);
        Assert.False(model.ScriptCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell, _) = Connected(new FakeRepository
        {
            GetSecurityAuditAsync = () => Task.FromException<SecurityFindingList>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the logins, databases and users", "Error: Timeout expired."), model.Verdict);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndSaysNothing()
    {
        var reading = new TaskCompletionSource<SecurityFindingList>();
        var repo = new FakeRepository { GetSecurityAuditAsync = () => reading.Task };
        var (model, shell, _) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetSecurityAuditAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task TheFindingSelected_IsKeptAcrossARefresh_ByItsKindScopeAndSubject()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.SelectedFinding = model.Findings.Single(f => f.Subject == "old_app");

        await model.RefreshAsync();

        Assert.Equal("old_app", model.SelectedFinding!.Subject);
        Assert.Contains(model.SelectedFinding, model.Findings);
    }

    [Fact]
    public async Task ShowIt_SelectsTheHighestRisk_PastAFilterThatHidesIt()
    {
        var (model, _, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.FilterIndex = 6;   // orphaned users: hides it
        var shown = new List<SecurityFinding>();
        model.ShowRow += shown.Add;

        await model.ActCommand.ExecuteAsync(new VerdictAction(SecurityAuditVerdict.ShowFinding, "Show it"));

        Assert.Equal(SecurityAuditViewModel.RisksFilter, model.FilterIndex);
        Assert.Equal(SecurityCheckKind.SaLogin, model.SelectedFinding!.Kind);
        Assert.Equal([model.SelectedFinding], shown);
    }

    [Fact]
    public async Task ScriptTheRisks_ShowsOneScript_ForReview()
    {
        var (model, _, dialogs) = Connected(Repo());
        await model.OnNavigatedToAsync();

        await model.ActCommand.ExecuteAsync(new VerdictAction(SecurityAuditVerdict.Script, "Script the risks"));

        var script = Assert.Single(dialogs.Scripts);
        Assert.Equal("Security Fixes", script.Heading);
        Assert.StartsWith("Review before running. 2 changes, one per risk.", script.Notice);
        Assert.Contains("ALTER LOGIN [sa] DISABLE;", script.Text);
    }
}
