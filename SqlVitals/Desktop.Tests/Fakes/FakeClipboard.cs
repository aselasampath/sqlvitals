using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>An <see cref="IClipboard"/> that keeps what was copied, without the real clipboard (#182).</summary>
internal sealed class FakeClipboard : IClipboard
{
    /// <summary>The last text copied; null before the first copy.</summary>
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}
