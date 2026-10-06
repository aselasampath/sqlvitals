using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

// The view models of the Configuration pages on the template (#150).

/// <summary>Config Checks' verdict: how many checks fail, the highest risk first.</summary>
public sealed class ConfigChecksViewModel(Action showCheck, Action script) : PageViewModel
{
    public void Show(ConfigCheckList list) => Verdict = ConfigChecksVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case ConfigChecksVerdict.ShowCheck: showCheck(); break;
            case ConfigChecksVerdict.Script:    script();    break;
        }
    }
}

/// <summary>Security Audit's verdict: the risks found, the highest first.</summary>
public sealed class SecurityAuditViewModel(Action showFinding, Action script) : PageViewModel
{
    public void Show(SecurityFindingList list) => Verdict = SecurityAuditVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case SecurityAuditVerdict.ShowFinding: showFinding(); break;
            case SecurityAuditVerdict.Script:      script();      break;
        }
    }
}
