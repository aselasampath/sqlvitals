using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Plan Cache Health's verdict (#149): the worst of what it checks, implicit conversions too (#178).</summary>
/// <param name="showTab">Shows a tab by its index.</param>
/// <param name="showWorstConversion">Shows the Implicit Conversions tab with its most used plan selected.</param>
public sealed class PlanCacheHealthViewModel(Action<int> showTab, Action showWorstConversion) : CodeBehindViewModel
{
    public void Show(PlanCacheHealthSummary summary, int keyLookups, IReadOnlyList<ImplicitConversion>? conversions = null) =>
        Verdict = PlanCacheHealthVerdict.From(summary, keyLookups, conversions);

    protected override void OnAction(VerdictAction action)
    {
        switch (action)
        {
            case { Key: PlanCacheHealthVerdict.ShowTab, Id: { } tab }:     showTab((int)tab);     break;
            case { Key: PlanCacheHealthVerdict.ShowWorstConversion }:      showWorstConversion(); break;
        }
    }
}
