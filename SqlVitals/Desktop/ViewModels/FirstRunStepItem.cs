using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One of the first-run flow's steps across its top (#177): done is ✓ on the accent, the current one
/// its number on the accent, one still to come muted (#190: the view's triggers colour it).
/// </summary>
public sealed partial class FirstRunStepItem(FirstRunStep step) : ObservableObject
{
    public FirstRunStep Step { get; } = step;

    public string Name { get; } = step.ToString();

    /// <summary>Done or current: on the accent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number))]
    private bool _isReached;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number))]
    private bool _isCurrent;

    /// <summary>✓ once done, its number otherwise.</summary>
    public string Number => IsReached && !IsCurrent ? "✓" : ((int)Step + 1).ToString(CultureInfo.CurrentCulture);
}
