using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Alerts' verdict (#150): the active alerts, the worst first, and whether alerts are raised at all.</summary>
public sealed class AlertsViewModel(Action<Alert> investigate, Action activeOnly) : PageViewModel
{
    private Alert? _worst;

    public void Show(IReadOnlyList<NamedAlert> alerts, string inPeriod, DateTime nowUtc, int monitored, IReadOnlyList<string> notMonitored,
                     bool activeOnly = false)
    {
        Verdict = AlertsVerdict.From(alerts, inPeriod, nowUtc, monitored, notMonitored, activeOnly);
        _worst  = AlertsVerdict.Worst(alerts)?.Alert;
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case AlertsVerdict.Investigate when _worst is { } alert: investigate(alert); break;
            case AlertsVerdict.ActiveOnly:                           activeOnly();       break;
        }
    }
}
