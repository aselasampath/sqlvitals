using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Export / AI's verdict (#150): what an export will write, or what the last one did.</summary>
public sealed class ExportViewModel(Action export, Action open) : CodeBehindViewModel
{
    public void Show(int selected, int sections, string? path, ExportOutcome? last) =>
        Verdict = ExportVerdict.From(selected, sections, path, last);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ExportVerdict.Export: export(); break;
            case ExportVerdict.Open:   open();   break;
        }
    }
}
