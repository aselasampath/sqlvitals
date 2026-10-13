using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The pages of the second wave of #180 (#186), each built on its view model as the page factory
/// builds it, for the tests that drive the whole page. A test reads through
/// <c>page.ViewModel.RefreshAsync()</c>, which reports a failure in the verdict rather than throwing.
/// </summary>
internal static class Wave2Pages
{
    public static ProcessesPage Processes(IWaitStatsRepository repo, ConnectionSettingsService settings, string? view = null, INavigator? navigator = null) =>
        new(new ProcessesViewModel(repo, repo, settings, navigator ?? new FakeNavigator(), view), settings);

    public static ResourceQueriesPage ResourceQueries(IWaitStatsRepository repo, TimeSpan? serverOffset = null, ISectionFolds? folds = null,
                                                      IDialogService? dialogs = null) =>
        new(new ResourceQueriesViewModel(repo, dialogs ?? new FakeDialogService(), serverOffset), folds);

    public static AgentJobsPage AgentJobs(IWaitStatsRepository repo, ISectionFolds? folds = null) =>
        new(new AgentJobsViewModel(repo), folds);

    public static ConfigChecksPage ConfigChecks(IWaitStatsRepository repo, string? server, ISectionFolds? folds = null, IDialogService? dialogs = null) =>
        new(new ConfigChecksViewModel(repo, dialogs ?? new FakeDialogService(), server), folds);

    public static SecurityAuditPage SecurityAudit(IWaitStatsRepository repo, string? server, ISectionFolds? folds = null, IDialogService? dialogs = null) =>
        new(new SecurityAuditViewModel(repo, dialogs ?? new FakeDialogService(), server), folds);

    public static ExportPage Export(IWaitStatsRepository repo, ISectionFolds? folds = null) =>
        new(new ExportViewModel(new ExportService(repo), new FakeDialogService(), new FakeFileDialogs(), open: _ => { }), folds);
}
