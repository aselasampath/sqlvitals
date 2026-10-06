using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Perfmon's verdict (#150): counters past a known limit, and the ones rising.</summary>
public sealed class PerfmonViewModel(Action<string> openPage) : PageViewModel
{
    private string? _causePage;

    public void Show(IReadOnlyList<PerfmonSeries> series, WaitTrendState state, string? noData)
    {
        Verdict    = PerfmonVerdict.From(series, state, noData);
        _causePage = PerfmonVerdict.Breached(series)?.Page;
    }

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == PerfmonVerdict.Cause && _causePage is { } page)
            openPage(page);
    }
}
