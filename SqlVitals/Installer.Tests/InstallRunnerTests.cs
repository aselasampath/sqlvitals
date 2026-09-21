using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Tests;

public class InstallRunnerTests
{
    private sealed class FakeStep : IInstallStep
    {
        private readonly List<string> _log;
        private int _failuresLeft;
        private readonly Exception? _error;
        private readonly Action? _onExecute;

        public FakeStep(string title, List<string> log, int failures = 0, Exception? error = null, Action? onExecute = null)
        {
            Title = title;
            _log = log;
            _failuresLeft = failures;
            _error = error;
            _onExecute = onExecute;
        }

        public string Title  { get; }
        public double Weight => 1;

        public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
        {
            _log.Add("run " + Title);
            _onExecute?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (_failuresLeft-- > 0)
                throw _error ?? new IOException("boom");
        }

        public void Rollback(InstallContext context) => _log.Add("undo " + Title);
    }

    private static InstallContext Context() =>
        new(InstallMode.Install, Path.Combine(Path.GetTempPath(), "sqlvitals-runner"), InstallScope.User);

    [Fact]
    public async Task Runs_every_step_in_order()
    {
        var log = new List<string>();
        var outcome = await new InstallRunner().RunAsync(
            new[] { new FakeStep("a", log), new FakeStep("b", log) }, Context(),
            _ => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(InstallOutcome.Succeeded, outcome);
        Assert.Equal(new[] { "run a", "run b" }, log);
    }

    [Fact]
    public async Task Retry_runs_the_failed_step_again_and_continues()
    {
        var log = new List<string>();
        StepFailure? seen = null;

        var outcome = await new InstallRunner().RunAsync(
            new[] { new FakeStep("a", log), new FakeStep("b", log, failures: 1), new FakeStep("c", log) }, Context(),
            f => { seen = f; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(InstallOutcome.Succeeded, outcome);
        Assert.Equal(new[] { "run a", "run b", "run b", "run c" }, log);
        Assert.Equal("b", seen!.StepTitle);
    }

    [Fact]
    public async Task Cancelling_after_a_failure_rolls_back_the_failed_step_and_completed_ones_in_reverse()
    {
        var log = new List<string>();

        var outcome = await new InstallRunner().RunAsync(
            new[] { new FakeStep("a", log), new FakeStep("b", log), new FakeStep("c", log, failures: 1), new FakeStep("d", log) },
            Context(), _ => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(InstallOutcome.RolledBack, outcome);
        Assert.Equal(new[] { "run a", "run b", "run c", "undo c", "undo b", "undo a" }, log);
    }

    [Fact]
    public async Task Cancellation_token_stops_the_run_and_rolls_back()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();

        var outcome = await new InstallRunner().RunAsync(
            new[] { new FakeStep("a", log), new FakeStep("b", log, onExecute: cts.Cancel), new FakeStep("c", log) },
            Context(), _ => Task.FromResult(true), cts.Token);

        Assert.Equal(InstallOutcome.RolledBack, outcome);
        Assert.Equal(new[] { "run a", "run b", "undo b", "undo a" }, log);
    }

    [Fact]
    public async Task Failure_passes_a_plain_language_message_and_resolution()
    {
        var log = new List<string>();
        StepFailure? seen = null;

        await new InstallRunner().RunAsync(
            new[] { new FakeStep("Copy", log, failures: 1, error: new InstallStepException("It broke.", "Do this.")) },
            Context(), f => { seen = f; return Task.FromResult(false); }, CancellationToken.None);

        Assert.Equal("It broke.", seen!.Message);
        Assert.Equal("Do this.", seen.Resolution);
    }
}

public class ErrorMessagesTests
{
    private static readonly InstallContext Context =
        new(InstallMode.Install, @"C:\Users\me\AppData\Local\Programs\SqlVitals", InstallScope.User);

    [Fact]
    public void File_in_use_tells_the_user_what_to_close()
    {
        var f = ErrorMessages.Describe("Copy", new IOException("in use", unchecked((int)0x80070020)), Context);
        Assert.Contains("in use", f.Message);
        Assert.Contains("Close SqlVitals", f.Resolution);
    }

    [Fact]
    public void Disk_full_names_the_install_folder()
    {
        var f = ErrorMessages.Describe("Copy", new IOException("full", unchecked((int)0x80070070)), Context);
        Assert.Contains(Context.InstallDir, f.Message);
        Assert.Contains("Free up", f.Resolution);
    }

    [Fact]
    public void Access_denied_and_damaged_package_have_specific_advice()
    {
        Assert.Contains("permission", ErrorMessages.Describe("x", new UnauthorizedAccessException(), Context).Message);
        Assert.Contains("download", ErrorMessages.Describe("x", new InvalidDataException(), Context).Resolution,
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_resolution_points_to_the_log()
    {
        var f = ErrorMessages.Describe("x", new InvalidOperationException("odd"), Context);
        Assert.Contains(SetupLog.Path, f.Resolution);
        Assert.Contains("odd", f.Message);
    }
}
