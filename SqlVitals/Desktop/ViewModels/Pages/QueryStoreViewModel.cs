using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Query Store's verdict (#146): the queries that got slower in the period against the 7 days before.</summary>
/// <param name="openQuery">Opens the query's own window, which reads with the page's period.</param>
public sealed class QueryStoreViewModel(Action<long> openQuery, INavigator navigator) : CodeBehindViewModel
{
    public void Show(QueryStoreStatus status, IReadOnlyList<QueryRegression>? regressions, string comparison, RegressionCriteria criteria,
                     string? notComparedWhy = null) =>
        Verdict = QueryStoreVerdict.From(status, regressions, comparison, criteria, notComparedWhy);

    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case QueryStoreVerdict.OpenQuery when action.Id is { } id: openQuery(id); break;
            case QueryStoreVerdict.Regressions:                       return navigator.GoToAsync("QueryRegressions");
        }
        return Task.CompletedTask;
    }
}
