using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Index Health's verdict (#150): the missing indexes asked for, and the unused ones.</summary>
public sealed class IndexHealthViewModel(Action showMissing, Action showUnused) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<MissingIndex> missing, IReadOnlyList<UnusedIndex> unused) => Verdict = IndexHealthVerdict.From(missing, unused);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case IndexHealthVerdict.ShowMissing: showMissing(); break;
            case IndexHealthVerdict.ShowUnused:  showUnused();  break;
        }
    }
}
