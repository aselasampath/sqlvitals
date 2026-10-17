using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A category in Settings' side menu (#131): a radio button that shows <see cref="IsSelected"/> and
/// asks Settings' view model to open it, which may stay where it is when the user does (#190).
/// </summary>
public sealed partial class SettingsMenuItem(SettingsCategoryInfo info, ICommand open) : ObservableObject
{
    public SettingsCategory Category { get; } = info.Id;

    public string Title { get; } = info.Title;

    public ICommand Open { get; } = open;

    /// <summary>The category on screen.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>"5" beside Connections; null for the others.</summary>
    [ObservableProperty]
    private string? _count;

    /// <summary>"Connections, 5 saved" for a screen reader; the title for the others.</summary>
    [ObservableProperty]
    private string _automationName = info.Title;

    /// <summary>Shows the selection again, as Settings has it, after a click it didn't take.</summary>
    internal void ShowSelection() => OnPropertyChanged(nameof(IsSelected));
}
