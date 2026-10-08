using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Perfmon's verdict (#150): counters past a known limit, and the ones rising.</summary>
public sealed class PerfmonViewModel(INavigator navigator) : CodeBehindViewModel
{
    private string? _causePage;

    public void Show(IReadOnlyList<PerfmonSeries> series, WaitTrendState state, string? noData)
    {
        Verdict    = PerfmonVerdict.From(series, state, noData);
        _causePage = PerfmonVerdict.Breached(series)?.Page;
    }

    protected override Task OnActionAsync(VerdictAction action) =>
        action.Key == PerfmonVerdict.Cause && _causePage is { } page ? navigator.GoToAsync(page) : Task.CompletedTask;
}
