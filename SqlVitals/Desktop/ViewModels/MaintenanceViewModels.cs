using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

// The view models of the Maintenance pages on the template (#150). Backups was a pilot (#146).

/// <summary>Index Health's verdict: the missing indexes asked for, and the unused ones.</summary>
public sealed class IndexHealthViewModel(Action showMissing, Action showUnused) : PageViewModel
{
    public void Show(IReadOnlyList<MissingIndex> missing, IReadOnlyList<UnusedIndex> unused) => Verdict = IndexHealthVerdict.From(missing, unused);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case IndexHealthVerdict.ShowMissing: showMissing(); break;
            case IndexHealthVerdict.ShowUnused:  showUnused();  break;
        }
    }
}

/// <summary>Stale Stats' verdict: the statistics with the most of their table changed.</summary>
public sealed class StaleStatsViewModel(Action showWorst) : PageViewModel
{
    public void Show(IReadOnlyList<StaleStatistic> stats) => Verdict = StaleStatsVerdict.From(stats);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == StaleStatsVerdict.ShowWorst)
            showWorst();
    }
}

/// <summary>Agent Jobs' verdict: failed and long-running jobs, or Agent stopped.</summary>
public sealed class AgentJobsViewModel(Action showJob, Action needsAttention) : PageViewModel
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
