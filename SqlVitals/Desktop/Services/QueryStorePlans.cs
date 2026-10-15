using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// A Query Store plan in the graphical plan window, as Query Regressions shows one: for Query Store's
/// rows and drawer (#189) and a query's own window. A plan that can't be read says why.
/// </summary>
public static class QueryStorePlans
{
    public static async Task ShowAsync(IQueryRegressionRepository repo, IDialogService dialogs, long planId, long queryId, string? queryText)
    {
        try
        {
            var planXml = await repo.GetQueryStorePlanAsync(planId);
            dialogs.ShowQueryPlan(planXml, queryText, $"Execution plan · Query Store plan {planId} · query {queryId}");
        }
        catch (Exception ex)
        {
            dialogs.Warn($"Failed to retrieve execution plan:\n{ConnectionSettingsService.RedactSecrets(ex.Message)}", "Plan Error");
        }
    }
}
