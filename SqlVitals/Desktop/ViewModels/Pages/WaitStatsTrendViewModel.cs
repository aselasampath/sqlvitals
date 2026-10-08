using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Wait Trend's verdict (#149): the wait type that waits the most on the chart.</summary>
public sealed class WaitStatsTrendViewModel(INavigator navigator) : CodeBehindViewModel
{
    public void Show(IReadOnlyList<WaitTrendSeries> series, WaitTrendState state, string unit, string over, string? noData) =>
        Verdict = WaitTrendVerdict.From(series, state, unit, over, noData);

    protected override Task OnActionAsync(VerdictAction action) =>
        action.Key == WaitTrendVerdict.TopWaits ? navigator.GoToAsync("TopWaits") : Task.CompletedTask;
}
