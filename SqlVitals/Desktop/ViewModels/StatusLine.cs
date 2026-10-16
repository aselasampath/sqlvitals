using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A Settings status line (#131, #190): "✓ Saved…" in green, "✗ Could not…" in red, anything else
/// muted; hidden while empty. Settings' <c>StatusLine</c> style colours it from <see cref="Success"/>.
/// An error's text may echo a secret back (#175), so every message is redacted before it is shown.
/// </summary>
public sealed partial class StatusLine : ObservableObject
{
    /// <summary>What is shown, with its ✓ or ✗; empty while hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    private string _text = string.Empty;

    /// <summary>True for done, false for failed, null for news on the way ("Testing…").</summary>
    [ObservableProperty]
    private bool? _success;

    public bool IsShown => Text.Length > 0;

    /// <summary>Shows a message; empty hides the line.</summary>
    public void Show(string message, bool? success)
    {
        message = SettingsContext.Redact(message);
        Success = success;
        Text = message.Length == 0 ? string.Empty : success switch
        {
            true  => "✓  " + message,
            false => "✗  " + message,
            null  => message,
        };
    }

    public void Clear() => Show(string.Empty, null);
}
