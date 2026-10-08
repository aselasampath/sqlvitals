using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>SP Trace's verdict (#149): what the capture is doing.</summary>
public sealed class SpTraceViewModel(Action start, Action stop, Action showSlowest) : CodeBehindViewModel
{
    public void Show(TraceSessionStatus status, IReadOnlyList<SpTraceEvent> calls, int procedures, int minDurationMs) =>
        Verdict = SpTraceVerdict.From(status, calls, procedures, minDurationMs);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case SpTraceVerdict.Start:       start();       break;
            case SpTraceVerdict.Stop:        stop();        break;
            case SpTraceVerdict.ShowSlowest: showSlowest(); break;
        }
    }
}
