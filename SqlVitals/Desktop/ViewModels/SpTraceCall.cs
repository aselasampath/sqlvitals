using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A row of Live Calls: a captured call, and what called it (#238). The caller can come in a poll
/// after its children, so Called by and Level change on the row in place.
/// </summary>
public sealed partial class SpTraceCall(SpTraceEvent call) : ObservableObject
{
    public SpTraceEvent Event { get; } = call;

    /// <summary>The calling procedure, "(client)", or null while it isn't known.</summary>
    public string? CalledBy => Nesting.CalledBy;

    /// <summary>1 for the client's call, 2 for a procedure it called, and so on; null when not known.</summary>
    public int? Level => Nesting.Level;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalledBy), nameof(Level))]
    private CallNesting _nesting;
}
