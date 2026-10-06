using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Active Waits' verdict (#149): the requests blocked, or what they wait on.</summary>
public sealed class ActiveWaitsViewModel(Action<int> openProcesses) : PageViewModel
{
    public void Show(IReadOnlyList<ActiveWait> waits, HealthThreshold? blocking) => Verdict = ActiveWaitsVerdict.From(waits, blocking);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == ActiveWaitsVerdict.Processes)
            openProcesses((int)(action.Id ?? 0));
    }
}
