using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Deadlocks' verdict (#149): how many in the range, and the object fought over most.</summary>
public sealed class DeadlocksViewModel(Action openNewest, Action showRecurring, Action showAll) : PageViewModel
{
    public void Show(DeadlockHistory history, IReadOnlyList<DeadlockReport> inRange, IReadOnlyList<DeadlockPattern> patterns, string during) =>
        Verdict = DeadlocksVerdict.From(history, inRange, patterns, during);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case DeadlocksVerdict.OpenNewest: openNewest();    break;
            case DeadlocksVerdict.Recurring:  showRecurring(); break;
            case DeadlocksVerdict.ShowAll:    showAll();       break;
        }
    }
}
