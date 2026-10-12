using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Memory's verdict (#150; the Memory Grants page until #178): queries waiting for memory, or how memory is used.</summary>
public sealed class MemoryViewModel(Action showWaiting, Action showClerks) : CodeBehindViewModel
{
    /// <param name="grants">The grants read with the snapshot; null for a trend reading, which reads only the snapshot.</param>
    public void Show(MemorySnapshot snapshot, IReadOnlyList<MemoryGrant>? grants) => Verdict = MemoryVerdict.From(snapshot, grants);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case MemoryVerdict.ShowWaiting: showWaiting(); break;
            case MemoryVerdict.Clerks:      showClerks();  break;
        }
    }
}
