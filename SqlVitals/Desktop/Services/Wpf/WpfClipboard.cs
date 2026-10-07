using SqlVitals.Desktop.Helpers;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary><see cref="IClipboard"/> through <see cref="ClipboardHelper"/>, which retries a busy clipboard.</summary>
internal sealed class WpfClipboard : IClipboard
{
    public void SetText(string text) => ClipboardHelper.SetText(text);
}
