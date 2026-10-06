using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Stale Stats' verdict (#150): the statistics with the most of their table changed.</summary>
public sealed class StaleStatisticsViewModel(Action showWorst) : PageViewModel
{
    public void Show(IReadOnlyList<StaleStatistic> stats) => Verdict = StaleStatsVerdict.From(stats);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == StaleStatsVerdict.ShowWorst)
            showWorst();
    }
}
