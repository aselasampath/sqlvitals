using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One box of the Checks table in Settings (#131, #190): a warning, critical or weight box. The
/// table's view model fills it and reads it; the box binds its text, and shows an accent border
/// while <see cref="IsEdited"/>.
/// </summary>
public sealed partial class ThresholdField(string name, string toolTip, bool isThreshold, Action changed) : ObservableObject
{
    /// <summary>What is typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOff))]
    private string _text = string.Empty;

    /// <summary>It differs from the default it is compared with.</summary>
    [ObservableProperty]
    private bool _isEdited;

    /// <summary>It can't be typed in: a connection on the default thresholds shows them read-only.</summary>
    [ObservableProperty]
    private bool _isReadOnly;

    /// <summary>"CPU warning threshold", for a screen reader.</summary>
    public string Name { get; } = name;

    public string ToolTip { get; } = toolTip;

    /// <summary>A threshold box shows "off" through itself while it is blank, which is what blank means.</summary>
    public bool ShowsOff => isThreshold && Text.Length == 0;

    partial void OnTextChanged(string value) => changed();
}
