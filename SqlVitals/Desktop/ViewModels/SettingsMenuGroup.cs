namespace SqlVitals.Desktop.ViewModels;

/// <summary>A group of Settings' side menu (#131): "SERVERS", then its categories.</summary>
public sealed record SettingsMenuGroup(string Heading, bool IsFirst, IReadOnlyList<SettingsMenuItem> Items);
