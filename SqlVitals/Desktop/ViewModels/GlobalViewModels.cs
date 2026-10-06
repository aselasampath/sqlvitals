using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

// The view models of the pages above the server part of the sidebar on the template (#150).

/// <summary>Alerts' verdict: the active alerts, the worst first, and whether alerts are raised at all.</summary>
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

/// <summary>Export / AI's verdict: what an export will write, or what the last one did.</summary>
public sealed class ExportViewModel(Action export, Action open) : PageViewModel
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
