using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>TempDB's verdict (#150): who holds TempDB, and how much room is left.</summary>
public sealed class TempDbViewModel(Action showSession) : PageViewModel
{
    public void Show(IReadOnlyList<TempDbFile> files, IReadOnlyList<TempDbSession> sessions, IReadOnlyList<TempDbCounter> counters) =>
        Verdict = TempDbVerdict.From(files, sessions, counters);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == TempDbVerdict.ShowSession)
            showSession();
    }
}
