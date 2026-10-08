using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Implicit Conversions' verdict (#149): how many plans convert, the most used first.</summary>
public sealed class ImplicitConversionsViewModel(Action showWorst) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<ImplicitConversion> rows) => Verdict = ImplicitConversionsVerdict.From(rows);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == ImplicitConversionsVerdict.ShowWorst)
            showWorst();
    }
}
