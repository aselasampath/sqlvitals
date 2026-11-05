using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>One value in a <see cref="MultiFilterChoices"/> list (#237), and whether it is ticked.</summary>
public sealed partial class FilterChoice(string value) : ObservableObject
{
    public string Value { get; } = value;

    [ObservableProperty]
    private bool _isChecked;
}
