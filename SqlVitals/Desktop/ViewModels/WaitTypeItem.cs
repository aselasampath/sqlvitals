using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>A wait type in Waits' list (#178, #188): ticked, it is charted.</summary>
public sealed partial class WaitTypeItem(string waitType, string waitCategory) : ObservableObject
{
    public string WaitType { get; } = waitType;

    public string WaitCategory { get; } = waitCategory;

    [ObservableProperty]
    private bool _isChecked;
}
