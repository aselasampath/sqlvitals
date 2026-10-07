using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// The view model of a page on the <see cref="PageShell"/> template (#146): its verdict, and what its
/// actions do. Each page's view model derives from it, one file each under <c>ViewModels/Pages</c>
/// (#181). A page adopts only what it needs; its grids and code-behind stay as they are until they
/// are moved to its view model (#180).
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
        Verdict = PageVerdict.Unreadable(what, ErrorStatus.Format(error));

    // Concurrent: a press while another page opens isn't held back, as before the actions could await.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Act(VerdictAction action) => OnActionAsync(action);

    /// <summary>One of the verdict's actions was pressed: one that opens another page (<see cref="INavigator"/>).</summary>
    protected virtual Task OnActionAsync(VerdictAction action)
    {
        OnAction(action);
        return Task.CompletedTask;
    }

    /// <summary>One of the verdict's actions was pressed: one that stays on the page.</summary>
    protected virtual void OnAction(VerdictAction action) { }
}
