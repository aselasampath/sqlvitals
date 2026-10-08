using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Top Waits' verdict (#149): the top wait category and what it means.</summary>
public sealed class TopWaitsViewModel(INavigator navigator) : CodeBehindViewModel
{
    private string _category = string.Empty;

    public void Show(IReadOnlyList<TopWaitType> waits)
    {
        Verdict   = TopWaitsVerdict.From(waits);
        _category = waits.Count == 0 ? string.Empty
            : waits.GroupBy(w => w.WaitCategory).OrderByDescending(g => g.Sum(w => w.WaitTimeMs)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    protected override Task OnActionAsync(VerdictAction action) => action.Key switch
    {
        TopWaitsVerdict.Trend                                                       => navigator.GoToAsync("WaitTrend"),
        TopWaitsVerdict.Cause when TopWaitsVerdict.CausePage(_category) is { } page => navigator.GoToAsync(page.Tag),
        _                                                                           => Task.CompletedTask,
    };
}
