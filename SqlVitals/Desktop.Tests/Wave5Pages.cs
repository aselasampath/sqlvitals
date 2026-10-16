using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The largest pages, of the fifth wave of #180 (#189), each built on its view model as the page
/// factory builds it, for the tests that drive the whole page. A test reads through
/// <c>page.ViewModel.RefreshAsync()</c>, which reports a failure rather than throwing.
/// </summary>
internal static class Wave5Pages
{
    public static QueryStorePage QueryStore(IWaitStatsRepository repo, ConnectionSettingsService settings, INavigator? navigator = null,
                                            IDialogService? dialogs = null, IClipboard? clipboard = null) =>
        new(new QueryStoreViewModel(repo, settings, navigator ?? new FakeNavigator(), dialogs ?? new FakeDialogService(),
                                    clipboard ?? new FakeClipboard()), settings);

    /// <summary>Live charts; without a monitoring manager, over an ad-hoc session of <paramref name="repo"/>'s server.</summary>
    public static LiveMetricsDashboardPage LiveMetrics(IWaitStatsRepository repo, MonitoringManager? monitoring = null, Guid? connectionId = null,
                                                       ConnectionHistory? history = null, INavigator? navigator = null) =>
        new(new LiveMetricsDashboardViewModel(repo, monitoring, connectionId, history, "Prod", navigator ?? new FakeNavigator()));
}
