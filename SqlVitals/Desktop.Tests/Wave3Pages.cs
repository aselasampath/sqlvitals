using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The pages of the third wave of #180 (#187), each built on its view model as the page factory
/// builds it, for the tests that drive the whole page. A test reads through
/// <c>page.ViewModel.RefreshAsync()</c>, which reports a failure in the verdict rather than throwing.
/// </summary>
internal static class Wave3Pages
{
    public static DeadlocksPage Deadlocks(IWaitStatsRepository repo, ISectionFolds? folds = null, IDialogService? dialogs = null,
                                          IFileDialogs? files = null, IClipboard? clipboard = null) =>
        new(new DeadlocksViewModel(repo, dialogs ?? new FakeDialogService(), files ?? new FakeFileDialogs(), clipboard ?? new FakeClipboard()), folds);

    public static AlertsPage Alerts(MonitoringManager monitoring, ConnectionSettingsService settings, Guid? connectionId = null,
                                    INavigator? navigator = null) =>
        new(new AlertsViewModel(monitoring, settings, navigator ?? new FakeNavigator(), connectionId), settings);

    public static MemoryPage Memory(IWaitStatsRepository repo, ISectionFolds? folds = null) =>
        new(new MemoryViewModel(repo), folds);

    public static IndexHealthPage IndexHealth(IWaitStatsRepository repo, ISectionFolds? folds = null, IDialogService? dialogs = null) =>
        new(new IndexHealthViewModel(repo, repo, dialogs ?? new FakeDialogService()), folds);

    public static FileIoPage FileIo(IWaitStatsRepository repo, ConnectionSettingsService settings) =>
        new(new FileIoViewModel(repo, settings), settings);

    public static QueryRegressionsPage QueryRegressions(IWaitStatsRepository repo, ConnectionHistory? history, ConnectionSettingsService settings,
                                                        INavigator? navigator = null, IDialogService? dialogs = null) =>
        new(new QueryRegressionsViewModel(repo, history, settings, navigator ?? new FakeNavigator(), dialogs ?? new FakeDialogService()), settings);
}
