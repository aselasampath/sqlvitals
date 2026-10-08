using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Backups' verdict (#146): the databases past the RPO.</summary>
public sealed class BackupsViewModel(Action showThem) : CodeBehindViewModel
{
    public void Show(BackupList list) => Verdict = BackupsVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == BackupsVerdict.ShowThem)
            showThem();
    }
}
