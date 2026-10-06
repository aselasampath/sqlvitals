using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>File I/O's verdict (#150): the files slower than the threshold, the worst first.</summary>
public sealed class FileIoViewModel(Action showSlow) : PageViewModel
{
    public void Show(FileLatencyList? list) => Verdict = FileIoVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == FileIoVerdict.ShowSlow)
            showSlow();
    }
}
