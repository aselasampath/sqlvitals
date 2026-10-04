using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Controls;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// The view model of a page on the <see cref="PageShell"/> template (#146): its verdict, and what its
/// actions do. A page adopts only what it needs; its grids and code-behind stay as they are until
/// they are moved here (#149, #150).
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>What the page says first; null until it has read something to judge.</summary>
    [ObservableProperty]
    private PageVerdict? _verdict;

    /// <summary>Puts the verdict, and what its actions do, on the page's shell.</summary>
    public void Attach(PageShell shell)
    {
        shell.SetBinding(PageShell.VerdictProperty, new Binding(nameof(Verdict)) { Source = this });
        shell.VerdictCommand = ActCommand;
    }

    /// <summary>The page couldn't read what it judges: the verdict says so.</summary>
    /// <param name="what">"the sessions", "the backup history".</param>
    public void ShowError(string what, Exception error) =>
        Verdict = PageVerdict.Unreadable(what, MainWindow.FormatErrorStatus(error));

    [RelayCommand]
    private void Act(VerdictAction action) => OnAction(action);

    /// <summary>One of the verdict's actions was pressed.</summary>
    protected abstract void OnAction(VerdictAction action);
}

/// <summary>Processes' verdict (#146): the head blockers and how long they have blocked.</summary>
public sealed class ProcessesViewModel(Action<int> goToSession, Action chainsOnly) : PageViewModel
{
    public void Show(IReadOnlyList<ProcessNode> classified, HealthThreshold? blocking) =>
        Verdict = ProcessesVerdict.From(classified, blocking);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ProcessesVerdict.GoToHead when action.Id is { } id: goToSession((int)id); break;
            case ProcessesVerdict.ChainsOnly:                         chainsOnly();          break;
        }
    }
}

/// <summary>Query Store's verdict (#146): the queries that got slower in the period against the 7 days before.</summary>
public sealed class QueryStoreViewModel(Action<long> openQuery, Action openRegressions) : PageViewModel
{
    public void Show(QueryStoreStatus status, IReadOnlyList<QueryRegression>? regressions, string comparison, RegressionCriteria criteria,
                     string? notComparedWhy = null) =>
        Verdict = QueryStoreVerdict.From(status, regressions, comparison, criteria, notComparedWhy);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case QueryStoreVerdict.OpenQuery when action.Id is { } id: openQuery(id);    break;
            case QueryStoreVerdict.Regressions:                       openRegressions(); break;
        }
    }
}

/// <summary>Backups' verdict (#146): the databases past the RPO.</summary>
public sealed class BackupsViewModel(Action showThem) : PageViewModel
{
    public void Show(BackupList list) => Verdict = BackupsVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == BackupsVerdict.ShowThem)
            showThem();
    }
}
