using System.Windows.Input;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A muted connection in Alerts &amp; chat's "Muted: A, B" (#131): a link to its Monitoring &amp;
/// alerts tab, then a comma unless it is the last.
/// </summary>
public sealed record MutedConnection(string Name, string Separator, ICommand Open)
{
    public string AutomationName => $"{Name}: Monitoring & alerts tab";
}
