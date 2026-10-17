using CommunityToolkit.Mvvm.ComponentModel;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>A tick box under Connections in a chat channel's form (#120).</summary>
public sealed partial class ChatConnectionItem(Guid id, string name) : ObservableObject
{
    public Guid   Id   { get; } = id;
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _chosen;
}
