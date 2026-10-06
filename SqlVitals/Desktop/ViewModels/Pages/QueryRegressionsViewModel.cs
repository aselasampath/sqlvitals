using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Query Regressions' verdict (#149): the queries that got slower, the worst first.</summary>
public sealed class QueryRegressionsViewModel(Action showPlans, Action<long> openQuery) : PageViewModel
{
    public void Show(IReadOnlyList<QueryRegression> rows, bool compared, string comparison, string? notCompared, RegressionCriteria criteria) =>
        Verdict = QueryRegressionsVerdict.From(rows, compared, comparison, notCompared, criteria);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case QueryRegressionsVerdict.ShowPlans:                       showPlans();   break;
            case QueryRegressionsVerdict.OpenQuery when action.Id is { } id: openQuery(id); break;
        }
    }
}
