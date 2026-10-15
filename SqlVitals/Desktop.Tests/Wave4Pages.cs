using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The chart pages of the fourth wave of #180 (#188), each built on its view model as the page
/// factory builds it, for the tests that drive the whole page. A test reads through
/// <c>page.ViewModel.RefreshAsync()</c>, which reports a failure in the verdict rather than throwing.
/// </summary>
internal static class Wave4Pages
{
    public static WaitsPage Waits(IWaitStatsRepository repo, ConnectionHistory? history = null, ISectionFolds? folds = null,
                                  INavigator? navigator = null) =>
        new(new WaitsViewModel(repo, history, navigator ?? new FakeNavigator()), folds);

    public static PerfmonPage Perfmon(IWaitStatsRepository repo, ConnectionHistory? history = null, ISectionFolds? folds = null,
                                      INavigator? navigator = null) =>
        new(new PerfmonViewModel(repo, history, navigator ?? new FakeNavigator()), folds);

    public static SpTracePage SpTrace(IWaitStatsRepository repo, ISectionFolds? folds = null, IDialogService? dialogs = null) =>
        new(new SpTraceViewModel(repo, repo, dialogs ?? new FakeDialogService()), folds);
}
