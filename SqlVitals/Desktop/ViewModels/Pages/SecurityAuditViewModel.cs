using SqlVitals.Engine.Security;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Security Audit's verdict (#150): the risks found, the highest first.</summary>
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
