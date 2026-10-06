using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Top Waits' verdict (#149): the top wait category and what it means.</summary>
public sealed class TopWaitsViewModel(Action<string> openPage) : PageViewModel
{
    private string _category = string.Empty;

    public void Show(IReadOnlyList<TopWaitType> waits)
    {
        Verdict   = TopWaitsVerdict.From(waits);
        _category = waits.Count == 0 ? string.Empty
            : waits.GroupBy(w => w.WaitCategory).OrderByDescending(g => g.Sum(w => w.WaitTimeMs)).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case TopWaitsVerdict.Trend: openPage("WaitTrend"); break;
            case TopWaitsVerdict.Cause when TopWaitsVerdict.CausePage(_category) is { } page: openPage(page.Tag); break;
        }
    }
}
