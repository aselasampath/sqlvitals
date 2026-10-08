using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Agent Jobs' verdict (#150): failed and long-running jobs, or Agent stopped.</summary>
public sealed class AgentJobsViewModel(Action showJob, Action needsAttention) : CodeBehindViewModel
{
    public void Show(AgentJobList list) => Verdict = AgentJobsVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case AgentJobsVerdict.ShowJob:        showJob();        break;
            case AgentJobsVerdict.NeedsAttention: needsAttention(); break;
        }
    }
}
