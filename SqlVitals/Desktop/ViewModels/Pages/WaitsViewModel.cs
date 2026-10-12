using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// The Waits page's verdict (#178, Top Waits and Wait Trend before): the trend's while it draws
/// lines, else the top waits' (<see cref="WaitsVerdict"/>).
/// </summary>
/// <param name="navigator">Where the top waits' cause goes: Processes, Resource Queries, File I/O, Memory.</param>
/// <param name="startTrend">Starts reading the trend, as the top bar's Resume does.</param>
/// <param name="chartTopWaits">Ticks the top 20 wait types, to chart them.</param>
public sealed class WaitsViewModel(INavigator navigator, Action startTrend, Action chartTopWaits) : CodeBehindViewModel
{
    private string _category = string.Empty;

    /// <param name="top">The table; null until it has been read.</param>
    /// <param name="during">The range the table covers; null for since the server started.</param>
    public void Show(IReadOnlyList<TopWaitType>? top, string? during,
                     IReadOnlyList<WaitTrendSeries> series, WaitTrendState state, string unit, string over, string? noData)
    {
        Verdict   = WaitsVerdict.From(top, during, series, state, unit, over, noData);
        _category = top is not { Count: > 0 } ? string.Empty
            : top.GroupBy(w => w.WaitCategory).OrderByDescending(g => g.Sum(w => w.WaitTimeMs)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    protected override Task OnActionAsync(VerdictAction action)
    {
        switch (action.Key)
        {
            case TopWaitsVerdict.Cause when TopWaitsVerdict.CausePage(_category) is { } page:
                return navigator.GoToAsync(page.Tag);
            case WaitTrendVerdict.TopWaits:
                chartTopWaits();
                break;
            case WaitsVerdict.StartTrend:
                startTrend();
                break;
        }
        return Task.CompletedTask;
    }
}
