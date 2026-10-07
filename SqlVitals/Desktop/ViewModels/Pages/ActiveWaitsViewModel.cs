using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Active Waits' verdict (#149): the requests blocked, or what they wait on.</summary>
public sealed class ActiveWaitsViewModel(INavigator navigator) : PageViewModel
{
    public void Show(IReadOnlyList<ActiveWait> waits, HealthThreshold? blocking) => Verdict = ActiveWaitsVerdict.From(waits, blocking);

    // Processes, on the blocking chains: the session blocked is one of them.
    protected override Task OnActionAsync(VerdictAction action) =>
        action.Key == ActiveWaitsVerdict.Processes
            ? navigator.GoToAsync("Processes", new FocusFilter(FocusKind.BlockingChain), "Active Waits")
            : Task.CompletedTask;
}
