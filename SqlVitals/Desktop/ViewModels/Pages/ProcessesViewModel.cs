using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Processes' verdict (#146): the head blockers and how long they have blocked.</summary>
public sealed class ProcessesViewModel(Action<int> goToSession, Action chainsOnly) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<ProcessNode> classified, HealthThreshold? blocking) =>
        Verdict = ProcessesVerdict.From(classified, blocking);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ProcessesVerdict.GoToHead when action.Id is { } id: goToSession((int)id); break;
            case ProcessesVerdict.ChainsOnly:                         chainsOnly();          break;
        }
    }
}
