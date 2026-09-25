using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.AgentJobs;

public class AgentJobTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 14, 0, 0);

    private static AgentJobRow Row(
        string name = "Nightly ETL",
        bool enabled = true,
        int? lastStatus = 1,
        int lastRunDuration = 500,          // 0:05:00
        int runs = 10, int failed = 0, int succeeded = 10,
        double? averageSeconds = 300,
        DateTime? started = null, DateTime? stopped = null,
        DateTime? activityNext = null, long scheduleNext = 0,
        int enabledSchedules = 1, int agentStartSchedules = 0, int idleSchedules = 0) =>
        new(Guid.NewGuid(), name, enabled, "Data Collector", "sa", null,
            lastStatus, lastStatus is null ? 0 : 20260925, lastStatus is null ? 0 : 20000, lastRunDuration,
            lastStatus is null ? null : "The job succeeded.",
            runs, failed, succeeded, averageSeconds,
            started, stopped, activityNext, scheduleNext,
            enabledSchedules, agentStartSchedules, idleSchedules);

    private static AgentJob Job(AgentJobRow row, AgentServiceState agent = AgentServiceState.Running) =>
        AgentJob.From(row, Now, agent);

    // ── Last run ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_the_last_run_its_duration_and_the_average()
    {
        var job = Job(Row(lastRunDuration: 1005, averageSeconds: 299.6));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(new DateTime(2026, 9, 25, 2, 0, 0), job.LastRunStart);
        Assert.Equal("0:10:05", job.DurationText);
        Assert.Equal("0:05:00", job.AverageText);
        Assert.Equal("×2.0", job.RatioText);
        Assert.False(job.IsRunning);
    }

    [Fact]
    public void A_job_whose_last_run_failed_is_failed()
    {
        var job = Job(Row(lastStatus: 0, failed: 3));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.True(job.LastRunFailed);
        Assert.Equal("3 of 10", job.FailuresText);
    }

    [Fact]
    public void A_job_with_no_history_left_has_never_run()
    {
        var job = Job(Row(lastStatus: null, runs: 0, succeeded: 0, averageSeconds: null));

        Assert.Equal(JobStatus.NeverRun, job.Status);
        Assert.Null(job.LastRunStart);
        Assert.Null(job.LastDuration);
        Assert.Null(job.AverageDuration);
        Assert.Equal("", job.FailuresText);
    }

    [Fact]
    public void Canceled_is_its_own_status() =>
        Assert.Equal(JobStatus.Canceled, Job(Row(lastStatus: 3)).Status);

    // ── Running, and running long ────────────────────────────────────────────

    [Fact]
    public void A_job_started_and_not_stopped_is_running_for_the_time_since_it_started()
    {
        var job = Job(Row(started: Now.AddMinutes(-3)));

        Assert.True(job.IsRunning);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(TimeSpan.FromMinutes(3), job.RunningFor);
        Assert.Equal("0:03:00", job.DurationText);
        Assert.False(job.IsRunningLong);
    }

    [Fact]
    public void A_job_running_longer_than_its_average_is_running_long()
    {
        var job = Job(Row(averageSeconds: 300, started: Now.AddMinutes(-12)));

        Assert.True(job.IsRunningLong);
        Assert.Equal(JobStatus.RunningLong, job.Status);
        Assert.Equal("Running long", job.StatusText);
        Assert.Contains("2.4× its average successful run of 0:05:00", job.StatusDetail);
    }

    [Fact]
    public void A_few_seconds_past_the_average_isnt_running_long()
    {
        // 5 min average, running 5 min 40 s: within the margin.
        Assert.False(Job(Row(averageSeconds: 300, started: Now.AddSeconds(-340))).IsRunningLong);
        // 5 min average, running 6 min 1 s: past it.
        Assert.True(Job(Row(averageSeconds: 300, started: Now.AddSeconds(-361))).IsRunningLong);
    }

    [Fact]
    public void Without_a_successful_run_to_compare_with_a_running_job_isnt_running_long()
    {
        var job = Job(Row(lastStatus: 0, succeeded: 0, failed: 10, averageSeconds: null, started: Now.AddHours(-5)));

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Null(job.AverageDuration);
        Assert.Contains("No successful run in the history", job.StatusDetail);
        // The last finished run still shows as failed.
        Assert.True(job.LastRunFailed);
        Assert.Contains("The run before this one failed.", job.StatusDetail);
    }

    [Fact]
    public void A_stopped_run_isnt_running()
    {
        var job = Job(Row(started: Now.AddMinutes(-30), stopped: Now.AddMinutes(-25)));
        Assert.False(job.IsRunning);
        Assert.Equal("0:05:00", job.DurationText);
    }

    [Theory]
    [InlineData(AgentServiceState.Stopped)]
    [InlineData(AgentServiceState.NotInstalled)]
    public void A_run_left_started_while_Agent_is_stopped_isnt_running(AgentServiceState agent) =>
        Assert.False(Job(Row(started: Now.AddHours(-2)), agent).IsRunning);

    [Fact]
    public void Running_long_comes_first_then_failed_then_running_then_the_rest_by_name()
    {
        var jobs = AgentJobList.Sort(new[]
        {
            Job(Row("b ok")),
            Job(Row("a ok")),
            Job(Row("running", started: Now.AddMinutes(-1))),
            Job(Row("never", lastStatus: null, averageSeconds: null, succeeded: 0)),
            Job(Row("failed", lastStatus: 0)),
            Job(Row("long", started: Now.AddHours(-1))),
        });

        Assert.Equal(["long", "failed", "running", "a ok", "b ok", "never"], jobs.Select(j => j.Name));
    }

    [Fact]
    public void Counts_failed_running_long_and_disabled_jobs()
    {
        var list = new AgentJobList(new[]
        {
            Job(Row(lastStatus: 0)),
            Job(Row(lastStatus: 0, started: Now.AddMinutes(-1))),   // running again after a failure
            Job(Row(started: Now.AddHours(-1))),
            Job(Row(enabled: false)),
        }, AgentServiceState.Running, Now, null);

        Assert.Equal(2, list.FailedCount);
        Assert.Equal(2, list.RunningCount);
        Assert.Equal(1, list.RunningLongCount);
        Assert.Equal(1, list.DisabledCount);
    }

    // ── Next run ─────────────────────────────────────────────────────────────

    [Fact]
    public void Next_run_is_the_earliest_time_still_ahead()
    {
        // sysjobschedules is refreshed only every 20 minutes and can lag behind sysjobactivity.
        var job = Job(Row(activityNext: Now.AddMinutes(10), scheduleNext: 20260925_133000));

        Assert.Equal(Now.AddMinutes(10), job.NextRun);
        Assert.Equal("2026-09-25 14:10:00", job.NextRunText);

        var fromSchedule = Job(Row(activityNext: null, scheduleNext: 20260926_020000));
        Assert.Equal(new DateTime(2026, 9, 26, 2, 0, 0), fromSchedule.NextRun);
    }

    [Fact]
    public void A_next_run_already_past_is_due()
    {
        var job = Job(Row(activityNext: Now.AddMinutes(-15)));

        Assert.Equal(Now.AddMinutes(-15), job.NextRun);
        Assert.EndsWith("(due)", job.NextRunText);

        // Not while it runs: the run is that one.
        Assert.DoesNotContain("due", Job(Row(activityNext: Now.AddMinutes(-15), started: Now.AddMinutes(-15))).NextRunText);
    }

    [Theory]
    [InlineData(false, 1, "Job disabled")]
    [InlineData(true,  0, "Not scheduled")]
    public void A_disabled_or_unscheduled_job_has_no_next_run_whatever_Agent_last_planned(bool enabled, int schedules, string text)
    {
        var job = Job(Row(enabled: enabled, enabledSchedules: schedules, activityNext: Now.AddHours(1)));

        Assert.Null(job.NextRun);
        Assert.Equal(text, job.NextRunText);
    }

    [Theory]
    [InlineData(1, 0, "When Agent starts")]
    [InlineData(0, 1, "When the CPU is idle")]
    [InlineData(0, 0, "No next run")]      // a one-time schedule that has run
    public void Says_why_a_scheduled_job_has_no_next_run_time(int agentStart, int idle, string text)
    {
        var job = Job(Row(agentStartSchedules: agentStart, idleSchedules: idle));

        Assert.Null(job.NextRun);
        Assert.Equal(text, job.NextRunText);
    }

    // ── SQL Agent itself ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(3, 1, "Running", AgentServiceState.Running)]
    [InlineData(3, 1, "Stopped", AgentServiceState.Stopped)]
    [InlineData(3, 1, "Start Pending", AgentServiceState.Unknown)]
    [InlineData(3, 0, null, AgentServiceState.Stopped)]      // Agent turns Agent XPs off as it stops
    [InlineData(8, 1, null, AgentServiceState.Running)]      // Managed Instance: no dm_server_services row
    [InlineData(3, null, null, AgentServiceState.Unknown)]
    [InlineData(4, 0, "Stopped", AgentServiceState.NotInstalled)] // Express
    public void Works_out_whether_Agent_is_running(int edition, int? agentXps, string? service, AgentServiceState expected) =>
        Assert.Equal(expected, AgentJobList.AgentState(edition, agentXps, service));

    [Fact]
    public void A_stopped_Agent_or_Express_is_explained()
    {
        Assert.Contains("isn't running", AgentJobList.AgentProblem(AgentServiceState.Stopped));
        Assert.Contains("Express", AgentJobList.AgentProblem(AgentServiceState.NotInstalled));
        Assert.Null(AgentJobList.AgentProblem(AgentServiceState.Running));
        Assert.Null(AgentJobList.AgentProblem(AgentServiceState.Unknown));
    }

    // ── The query ────────────────────────────────────────────────────────────

    [Fact]
    public void JobsSql_reads_the_job_outcome_rows_and_the_current_Agent_session_only()
    {
        var sql = AgentJobRepository.JobsSql;

        Assert.Contains("h.step_id = 0", sql);
        // The average is of successful runs.
        Assert.Contains("AVG(CASE WHEN h.run_status = 1", sql);
        Assert.Contains("a.session_id = (SELECT MAX(s.session_id) FROM msdb.dbo.syssessions s)", sql);
        // A disabled schedule doesn't run the job.
        Assert.Contains("s.enabled = 1", sql);
    }

    [Fact]
    public void The_edition_check_reads_nothing_Azure_SQL_Database_lacks()
    {
        // It runs first so Azure SQL Database gets its message; sys.configurations is read separately.
        Assert.DoesNotContain("sys.configurations", AgentJobRepository.ServerSql);
        Assert.DoesNotContain("msdb", AgentJobRepository.ServerSql);
        Assert.Contains("EngineEdition", AgentJobRepository.ServerSql);
    }
}
