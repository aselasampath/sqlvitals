using SqlVitals.Engine.Alerts;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Opens pages (#182), so a view model's actions go to another page without the window. Each page
/// is named by its tag in <see cref="PageCatalog"/>; an unknown tag throws. Every move goes through
/// the navigator of #139, so Back returns.
/// </summary>
public interface INavigator
{
    /// <summary>Opens a page as its sidebar button would.</summary>
    /// <param name="serverId">The server a page is about (#141): an Overview's, or the connection a
    /// server's page queries, made active first; null for the active connection.</param>
    Task GoToAsync(string tag, Guid? serverId = null);

    /// <summary>
    /// Opens another page of the same server with a filter (#145): a session on Processes, a query
    /// on Query Store. Its banner says where it came from.
    /// </summary>
    /// <param name="fromPage">The page the link is on, as the banner names it: "Active Waits".</param>
    Task GoToAsync(string tag, FocusFilter filter, string fromPage);

    /// <summary>
    /// An alert's evidence (#145): its page on its server, filtered to the problem and set to its
    /// time; Alerts when its connection has been removed.
    /// </summary>
    /// <param name="source">Why, in the activity log: <see cref="SqlVitals.Engine.Activity.ActivitySources"/>.</param>
    Task InvestigateAsync(Alert alert, string source);

    /// <summary>Back to the page before, as the top bar's Back does.</summary>
    Task BackAsync();

    /// <summary>Forward again after Back.</summary>
    Task ForwardAsync();
}
