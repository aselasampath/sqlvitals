using System.Globalization;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Agent Jobs' view model on its own (#186): no page, no STA thread, a fake repository. It reads
/// through its lifecycle, reads the runs of the job selected (and only the last one selected),
/// keeps the job and its run across a refresh, says what it couldn't read, stops reading when the
/// page is left, and reads every 30 s while ticked.
/// </summary>
public sealed class AgentJobsViewModelTests
{
    private static readonly DateTime Now = DateTime.Now;

    private static AgentJob Job(string name, JobOutcome? last, TimeSpan? runningFor = null) =>
        new(Guid.NewGuid(), name, true, "Database Maintenance", "sa", $"{name}, nightly.", last, Now.AddHours(-6), TimeSpan.FromMinutes(12), null,
            30, last == JobOutcome.Failed ? 2 : 0, 28, TimeSpan.FromMinutes(12), runningFor is null ? null : Now - runningFor, runningFor,
            Now.AddHours(18), Now.AddHours(18).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));

    private static readonly IReadOnlyList<AgentJob> Jobs = AgentJobList.Sort(
        [Job("Nightly backup", JobOutcome.Failed), Job("Reindex SalesDb", JobOutcome.Succeeded, TimeSpan.FromMinutes(55)), Job("Purge history", JobOutcome.Succeeded)]);

    private static JobStepRun Step(int id, JobOutcome outcome) => new(id, $"Step {id}", outcome, Now.AddHours(-6), TimeSpan.FromMinutes(3), $"Step {id} said so.");

    // Two runs: the newest failed at step 2.
    private static IReadOnlyList<JobRun> Runs() =>
    [
        new(Now.AddHours(-6), JobOutcome.Failed, TimeSpan.FromMinutes(6), "The job failed.", [Step(1, JobOutcome.Succeeded), Step(2, JobOutcome.Failed)]),
        new(Now.AddDays(-1), JobOutcome.Succeeded, TimeSpan.FromMinutes(5), "The job succeeded.", [Step(1, JobOutcome.Succeeded), Step(2, JobOutcome.Succeeded)]),
    ];

    private static FakeRepository Repo(IReadOnlyList<AgentJob>? jobs = null) => new()
    {
        GetAgentJobsAsync    = () => Task.FromResult(new AgentJobList(jobs ?? Jobs, AgentServiceState.Running, Now, null)),
        GetAgentJobRunsAsync = (_, _) => Task.FromResult(Runs()),
    };

    private static (AgentJobsViewModel Model, FakeShellStatus Shell) Connected(FakeRepository repo, TimeProvider? time = null, FakeUiDispatcher? ui = null)
    {
        var model = new AgentJobsViewModel(repo);
        var shell = new FakeShellStatus();
        model.Connect("AgentJobs", shell, new RefreshScheduler(time ?? TimeProvider.System, ui ?? new FakeUiDispatcher()));
        return (model, shell);
    }

    [Fact]
    public async Task Loaded_ListsTheJobs_TheMostUrgentFirst_AndItsRuns_OnTheFailingStep()
    {
        var (model, shell) = Connected(Repo());

        await model.OnNavigatedToAsync();
        await model.RunsRead;

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal("3 jobs", model.CountText);
        Assert.Same(model.Jobs[0], model.SelectedJob);
        Assert.True(model.HasJob);
        Assert.Equal($"{model.SelectedJob!.Name} · {model.SelectedJob.StatusText}", model.DetailHeader);
        Assert.Equal("2 runs msdb keeps, newest first", model.RunsLabel);
        Assert.Same(model.Runs[0], model.SelectedRun);
        Assert.Equal(2, model.SelectedStep!.StepId);
        Assert.StartsWith("Agent running · read at ", model.SourceSummary);
        Assert.Equal(VerdictLevel.Critical, model.Verdict!.Level);
    }

    [Fact]
    public async Task Empty_SaysThereAreNoJobs_AndNoJobIsSelected()
    {
        var (model, _) = Connected(Repo([]));

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Jobs);
        Assert.False(model.HasJob);
        Assert.True(model.ShowsNoData);
        Assert.Equal("There are no SQL Agent jobs on this server.", model.NoDataText);
        Assert.Equal("Select a job above to see its runs.", model.DetailHeader);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        var (model, shell) = Connected(new FakeRepository
        {
            GetAgentJobsAsync = () => Task.FromException<AgentJobList>(new InvalidOperationException("Timeout expired.")),
        });

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: Timeout expired."], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the SQL Agent jobs", "Error: Timeout expired."), model.Verdict);
    }

    [Fact]
    public async Task AJobsRunsUnread_SaySoUnderIt()
    {
        var repo = Repo();
        repo.GetAgentJobRunsAsync = (_, _) => Task.FromException<IReadOnlyList<JobRun>>(new InvalidOperationException("Timeout expired."));
        var (model, _) = Connected(repo);

        await model.OnNavigatedToAsync();
        await model.RunsRead;

        Assert.Equal("Its runs could not be read: Error: Timeout expired.", model.RunsProblem);
        Assert.True(model.ShowsRunsProblem);
        Assert.Empty(model.Runs);
    }

    [Fact]
    public async Task Leaving_CancelsTheRead_AndTheRunsRead()
    {
        var repo = Repo();
        var runs = new TaskCompletionSource<IReadOnlyList<JobRun>>();
        repo.GetAgentJobRunsAsync = (_, _) => runs.Task;
        var (model, shell) = Connected(repo);
        await model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetAgentJobRunsAsync)];

        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        runs.SetResult(Runs());
        await model.RunsRead;
        Assert.Empty(model.Runs);
        Assert.Equal(["Read Load"], shell.Reports);
    }

    [Fact]
    public async Task Leaving_WhileTheJobsAreRead_SaysNothing()
    {
        var reading = new TaskCompletionSource<AgentJobList>();
        var repo = new FakeRepository { GetAgentJobsAsync = () => reading.Task };
        var (model, shell) = Connected(repo);

        var shown = model.OnNavigatedToAsync();
        var token = repo.Tokens[nameof(FakeRepository.GetAgentJobsAsync)];
        model.OnNavigatedFrom();

        Assert.True(token.IsCancellationRequested);
        reading.SetCanceled(token);
        await shown;
        Assert.Empty(shell.Reports);
        Assert.Null(model.Verdict);
    }

    [Fact]
    public async Task AnotherJob_DropsTheFirstsRuns_AndOnlyTheLastSelectedsRunsLand()
    {
        var repo  = Repo();
        var slow  = new TaskCompletionSource<IReadOnlyList<JobRun>>();
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.RunsRead;

        repo.GetAgentJobRunsAsync = (_, _) => slow.Task;
        model.SelectedJob = model.Jobs[1];
        Assert.Empty(model.Runs);
        Assert.Null(model.SelectedStep);

        repo.GetAgentJobRunsAsync = (_, _) => Task.FromResult<IReadOnlyList<JobRun>>([]);
        model.SelectedJob = model.Jobs[2];
        await model.RunsRead;
        slow.SetResult(Runs());

        Assert.Equal("msdb keeps no runs of this job", model.RunsLabel);
        Assert.Empty(model.Runs);
    }

    [Fact]
    public async Task TheJobAndTheRunAndStepOpenInIt_AreKeptAcrossARefresh_ItsRunsShownWhileReadAgain()
    {
        var repo = Repo();
        var (model, _) = Connected(repo);
        await model.OnNavigatedToAsync();
        await model.RunsRead;
        model.SelectedJob = model.Jobs.Single(j => j.Name == "Purge history");
        await model.RunsRead;
        model.SelectedRun  = model.Runs[1];
        model.SelectedStep = model.Steps[0];

        // A refresh (a timed one every 30 s): its runs stay on screen while msdb is read again.
        var again = new TaskCompletionSource<IReadOnlyList<JobRun>>();
        repo.GetAgentJobRunsAsync = (_, _) => again.Task;
        await model.RefreshAsync();
        Assert.Equal(2, model.Runs.Count);
        again.SetResult(Runs());
        await model.RunsRead;

        Assert.Equal("Purge history", model.SelectedJob!.Name);
        Assert.Equal(Now.AddDays(-1), model.SelectedRun!.Start);
        Assert.Equal(1, model.SelectedStep!.StepId);
    }

    [Fact]
    public async Task ShowItsRuns_SelectsTheJob_PastAFilterThatHidesIt_AndNeedsAttentionFilters()
    {
        var (model, _) = Connected(Repo());
        await model.OnNavigatedToAsync();
        model.FilterIndex = 2;   // running now: hides the failed job
        var shown = new List<AgentJob>();
        model.ShowRow += shown.Add;

        await model.ActCommand.ExecuteAsync(new VerdictAction(AgentJobsVerdict.ShowJob, "Show its runs"));

        Assert.Equal(0, model.FilterIndex);
        Assert.Equal("Nightly backup", model.SelectedJob!.Name);
        Assert.Equal([model.SelectedJob], shown);

        await model.ActCommand.ExecuteAsync(new VerdictAction(AgentJobsVerdict.NeedsAttention, "Show only these"));
        Assert.Equal(AgentJobsViewModel.NeedsAttentionFilter, model.FilterIndex);
        Assert.Equal("2 of 3 jobs", model.CountText);
    }

    [Fact]
    public async Task Ticked_ReadsEvery30s_AndSaysTheContextChanged()
    {
        var time  = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var ui    = new FakeUiDispatcher();
        var reads = 0;
        var repo  = Repo();
        repo.GetAgentJobsAsync = () => { reads++; return Task.FromResult(new AgentJobList([], AgentServiceState.Running, Now, null)); };
        var (model, shell) = Connected(repo, time, ui);
        await model.OnNavigatedToAsync();

        model.AutoRefresh = true;
        Assert.Equal("Context", shell.Reports[^1]);
        time.Advance(AgentJobsViewModel.AutoRefreshInterval);
        ui.RunPosted();

        Assert.Equal(2, reads);
        model.OnNavigatedFrom();
    }
}
