using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SqlVitals.Installer.Core;

/// <summary>One unit of work in an install or uninstall. <see cref="Rollback"/> undoes whatever Execute did, even partially.</summary>
public interface IInstallStep
{
    /// <summary>Shown on the progress screen, e.g. "Copying application files".</summary>
    string Title { get; }

    /// <summary>Relative share of the progress bar.</summary>
    double Weight { get; }

    void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct);

    void Rollback(InstallContext context);
}

/// <summary>A step failure the user can understand and act on.</summary>
public sealed class InstallStepException : Exception
{
    public InstallStepException(string message, string resolution, Exception? inner = null)
        : base(message, inner)
    {
        Resolution = resolution;
    }

    public string Resolution { get; }
}

public sealed class StepFailure
{
    public StepFailure(string stepTitle, string message, string resolution)
    {
        StepTitle  = stepTitle;
        Message    = message;
        Resolution = resolution;
    }

    public string StepTitle  { get; }
    public string Message    { get; }
    public string Resolution { get; }
}

public enum InstallOutcome
{
    Succeeded,
    /// <summary>The user cancelled (or gave up after a failure) and every change was undone.</summary>
    RolledBack,
    /// <summary>Cancelled, but some changes couldn't be undone — see the log.</summary>
    RollbackIncomplete,
}

/// <summary>
/// Runs steps in order. When one fails the caller decides: retry that step, or cancel — which
/// rolls back the failed step and every completed one in reverse order.
/// </summary>
public sealed class InstallRunner
{
    public event Action<int>?    StepStarted;
    public event Action<int>?    StepCompleted;
    public event Action<double>? ProgressChanged;
    public event Action?         RollingBack;

    /// <param name="onFailure">Return true to retry the failed step, false to cancel and roll back.</param>
    public async Task<InstallOutcome> RunAsync(
        IReadOnlyList<IInstallStep> steps,
        InstallContext context,
        Func<StepFailure, Task<bool>> onFailure,
        CancellationToken ct)
    {
        double totalWeight = 0;
        foreach (var s in steps) totalWeight += s.Weight;
        double doneWeight = 0;

        var completed = new List<IInstallStep>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var baseWeight = doneWeight;
            // Created on the calling (UI) thread so reports are marshalled back to it.
            var progress = new Progress<double>(p =>
                ProgressChanged?.Invoke((baseWeight + step.Weight * Clamp(p)) / totalWeight));

            while (true)
            {
                StepStarted?.Invoke(i);
                SetupLog.Info($"Step: {step.Title}");
                try
                {
                    await Task.Run(() => step.Execute(context, progress, ct), CancellationToken.None);
                    completed.Add(step);
                    doneWeight += step.Weight;
                    ProgressChanged?.Invoke(doneWeight / totalWeight);
                    StepCompleted?.Invoke(i);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    SetupLog.Warn($"Cancelled during: {step.Title}");
                    return await RollbackAsync(completed, step, context);
                }
                catch (Exception ex)
                {
                    SetupLog.Error($"Step failed: {step.Title}", ex);
                    var failure = ErrorMessages.Describe(step.Title, ex, context);

                    if (!ct.IsCancellationRequested && await onFailure(failure) && !ct.IsCancellationRequested)
                    {
                        SetupLog.Info($"Retrying: {step.Title}");
                        continue;
                    }

                    return await RollbackAsync(completed, step, context);
                }
            }
        }

        SetupLog.Info("All steps completed.");
        return InstallOutcome.Succeeded;
    }

    private async Task<InstallOutcome> RollbackAsync(List<IInstallStep> completed, IInstallStep current, InstallContext context)
    {
        RollingBack?.Invoke();
        SetupLog.Info("Rolling back.");

        var toUndo = new List<IInstallStep>(completed) { current };
        toUndo.Reverse();

        var clean = true;
        await Task.Run(() =>
        {
            foreach (var step in toUndo)
            {
                try
                {
                    step.Rollback(context);
                }
                catch (Exception ex)
                {
                    clean = false;
                    SetupLog.Error($"Rollback failed: {step.Title}", ex);
                }
            }
        });

        return clean ? InstallOutcome.RolledBack : InstallOutcome.RollbackIncomplete;
    }

    private static double Clamp(double p) => p < 0 ? 0 : p > 1 ? 1 : p;
}
