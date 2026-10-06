using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Plan Cache Health's verdict (#149): the worst of what it checks.</summary>
public sealed class PlanCacheHealthViewModel(Action<int> showTab) : PageViewModel
{
    public void Show(PlanCacheHealthSummary summary, int keyLookups) => Verdict = PlanCacheHealthVerdict.From(summary, keyLookups);

    protected override void OnAction(VerdictAction action)
    {
        if (action is { Key: PlanCacheHealthVerdict.ShowTab, Id: { } tab })
            showTab((int)tab);
    }
}
