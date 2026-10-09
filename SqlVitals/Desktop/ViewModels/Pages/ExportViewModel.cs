using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Export / AI's verdict (#150): what an export will write, or what the last one did.</summary>
public sealed class ExportViewModel(Func<Task> export, Action open) : CodeBehindViewModel
{
    public void Show(int selected, int sections, string? path, ExportOutcome? last) =>
        Verdict = ExportVerdict.From(selected, sections, path, last);

    // Export is awaited by the verdict's command, so nothing it throws is lost in an async void (#169).
    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case ExportVerdict.Export: return export();
            case ExportVerdict.Open:   open(); break;
        }
        return Task.CompletedTask;
    }
}
