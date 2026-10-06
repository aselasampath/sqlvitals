using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>Config Checks' verdict (#150): how many checks fail, the highest risk first.</summary>
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
