using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Processes' verdict (#146): the head blockers and how long they have blocked; on the Waiting view
/// (#178, the Active Waits page before), the requests blocked or what they wait on.
/// </summary>
/// <param name="goToSession">Selects a session on the map or in the grid.</param>
/// <param name="chainsOnly">Shows only the sessions in a blocking chain.</param>
/// <param name="chainOnMap">From the Waiting view: the map, on the chain of the session given.</param>
public sealed class ProcessesViewModel(Action<int> goToSession, Action chainsOnly, Action<int> chainOnMap) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<ProcessNode> classified, HealthThreshold? blocking) =>
        Verdict = ProcessesVerdict.From(classified, blocking);

    /// <summary>The Waiting view's verdict.</summary>
    public void ShowWaits(IReadOnlyList<ActiveWait> waits, HealthThreshold? blocking) =>
        Verdict = ActiveWaitsVerdict.From(waits, blocking);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ProcessesVerdict.GoToHead when action.Id is { } id:     goToSession((int)id); break;
            case ProcessesVerdict.ChainsOnly:                             chainsOnly();          break;
            case ActiveWaitsVerdict.Processes when action.Id is { } id:  chainOnMap((int)id);   break;
        }
    }
}
