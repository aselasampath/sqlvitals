using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>DB Storage's verdict (#150): files near their maximum, auto-shrink, or the storage at a glance.</summary>
public sealed class DatabaseStorageViewModel(Action showFile) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<DatabaseFile> files, IReadOnlyList<TempDbFileSummary> tempDb) => Verdict = DbStorageVerdict.From(files, tempDb);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == DbStorageVerdict.ShowFile)
            showFile();
    }
}
