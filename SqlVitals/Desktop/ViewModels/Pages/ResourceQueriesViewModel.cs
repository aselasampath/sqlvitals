using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Resource Queries' verdict (#149): the top query by CPU, and by reads.</summary>
public sealed class ResourceQueriesViewModel(Action showCpu, Action showReads) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<ResourceIntensiveQuery> byCpu, IReadOnlyList<ResourceIntensiveQuery> byReads) =>
        Verdict = ResourceQueriesVerdict.From(byCpu, byReads);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ResourceQueriesVerdict.ShowCpu:   showCpu();   break;
            case ResourceQueriesVerdict.ShowReads: showReads(); break;
        }
    }
}
