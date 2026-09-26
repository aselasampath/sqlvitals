using SqlVitals.Engine.AgentJobs;

namespace SqlVitals.Engine.Tests.AgentJobs;

public class AgentJobHistoryTests
{
    private static int _instance;

    // A step row (stepId > 0) or the job outcome (stepId 0), starting at hh:mm:ss on 25 September 2026.
    private static JobHistoryRow Row(int stepId, int status, int time, int duration = 10, string? message = null) =>
        new(++_instance, stepId, stepId == 0 ? "(Job outcome)" : $"Step {stepId} name", status,
            20260925, time, duration, message ?? (status == 0 ? "Error 50000 [SQLSTATE 42000]" : "OK"));

    [Fact]
    public void Steps_belong_to_the_next_job_outcome_newest_run_first()
    {
        var runs = AgentJobHistory.GroupRuns(
        [
            Row(1, 1, 010000), Row(2, 1, 010010), Row(0, 1, 010000, duration: 20, message: "The job succeeded."),
            Row(1, 1, 020000), Row(2, 0, 020010), Row(0, 0, 020000, duration: 20, message: "The job failed."),
        ]);

        Assert.Equal(2, runs.Count);
        var failed = runs[0];
        Assert.Equal(JobOutcome.Failed, failed.Outcome);
        Assert.Equal(new DateTime(2026, 9, 25, 2, 0, 0), failed.Start);
        Assert.Equal([1, 2], failed.Steps.Select(s => s.StepId));
        Assert.Equal("0:00:20", failed.DurationText);
        Assert.Equal(JobOutcome.Succeeded, runs[1].Outcome);
    }

    [Fact]
    public void A_failed_run_is_explained_by_its_failing_step()
    {
        var run = AgentJobHistory.GroupRuns(
        [
            Row(1, 1, 020000), Row(2, 0, 020010, message: "Cannot insert duplicate key."), Row(0, 0, 020000, message: "The job failed."),
        ])[0];

        Assert.Equal(2, run.FailedStep?.StepId);
        Assert.Equal("Step 2 (Step 2 name): Cannot insert duplicate key.", run.Detail);
    }

    [Fact]
    public void A_successful_run_shows_the_jobs_own_message()
    {
        var run = AgentJobHistory.GroupRuns([Row(1, 1, 010000), Row(0, 1, 010000, message: "The job succeeded.")])[0];
        Assert.Equal("The job succeeded.", run.Detail);
    }

    [Fact]
    public void Steps_after_the_last_outcome_are_the_run_in_progress()
    {
        var runningSince = new DateTime(2026, 9, 25, 3, 0, 0);
        var runs = AgentJobHistory.GroupRuns(
        [
            Row(1, 1, 020000), Row(0, 1, 020000),
            Row(1, 1, 030000),
        ], runningSince);

        Assert.Equal(2, runs.Count);
        Assert.False(runs[0].HasOutcome);
        Assert.Equal(runningSince, runs[0].Start);
        Assert.Equal("Running now.", runs[0].Message);
        Assert.Equal("Running", runs[0].OutcomeText);
        Assert.Single(runs[0].Steps);
    }

    [Fact]
    public void A_running_job_with_no_step_finished_yet_still_has_its_run()
    {
        var runningSince = new DateTime(2026, 9, 25, 3, 0, 0);
        var runs = AgentJobHistory.GroupRuns([Row(1, 1, 020000), Row(0, 1, 020000)], runningSince);

        Assert.Equal(2, runs.Count);
        Assert.Empty(runs[0].Steps);
        Assert.Equal(runningSince, runs[0].Start);
    }

    [Fact]
    public void Steps_with_no_outcome_while_the_job_isnt_running_were_cut_short()
    {
        var runs = AgentJobHistory.GroupRuns([Row(1, 1, 020000), Row(0, 1, 020000), Row(1, 1, 030000)]);

        Assert.False(runs[0].HasOutcome);
        Assert.Contains("cut short", runs[0].Message);
        Assert.Equal("No outcome", runs[0].OutcomeText);
        Assert.Equal(new DateTime(2026, 9, 25, 3, 0, 0), runs[0].Start);
    }

    [Fact]
    public void Steps_older_than_the_run_they_precede_are_a_run_cut_short_not_its_steps()
    {
        // 01:00 run's step 1 finished, then Agent stopped; the 02:00 run completed normally.
        var runs = AgentJobHistory.GroupRuns([Row(1, 1, 010000), Row(1, 1, 020000), Row(2, 1, 020010), Row(0, 1, 020000)]);

        Assert.Equal(2, runs.Count);
        Assert.Equal(JobOutcome.Succeeded, runs[0].Outcome);
        Assert.Equal([1, 2], runs[0].Steps.Select(s => s.StepId));
        Assert.False(runs[1].HasOutcome);
        Assert.Equal(new DateTime(2026, 9, 25, 1, 0, 0), runs[1].Start);
        Assert.Contains("cut short", runs[1].Message);
    }

    [Fact]
    public void Rows_are_grouped_in_instance_order_whatever_order_they_come_in()
    {
        var rows = new[] { Row(1, 1, 010000), Row(0, 1, 010000), Row(1, 0, 020000), Row(0, 0, 020000) };
        var runs = AgentJobHistory.GroupRuns(rows.Reverse());

        Assert.Equal(JobOutcome.Failed, runs[0].Outcome);
        Assert.Equal(JobOutcome.Succeeded, runs[1].Outcome);
    }

    [Fact]
    public void No_history_is_no_runs() =>
        Assert.Empty(AgentJobHistory.GroupRuns([]));
}
