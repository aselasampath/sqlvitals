using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>App Connections' verdict (#149): who holds the connections; it has no actions.</summary>
public sealed class ApplicationConnectionsViewModel : PageViewModel
{
    public void Show(IReadOnlyList<ApplicationConnection> apps) => Verdict = AppConnectionsVerdict.From(apps);

    protected override void OnAction(VerdictAction action) { }
}
