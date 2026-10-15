using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>A counter in Perfmon's list (#150, #188): ticked, it is charted.</summary>
public sealed partial class CounterItem : ObservableObject
{
    public string CounterName { get; init; } = "";

    /// <summary>Its object without the instance's prefix: "SQL Statistics"; empty from the history, which keeps names only.</summary>
    public string ObjectShort { get; init; } = "";

    public string DisplayName => CounterName;

    [ObservableProperty]
    private bool _isChecked;
}
