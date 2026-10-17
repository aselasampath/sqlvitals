using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>"Opened from alert" over a page opened as evidence (#145): what it shows, and Show all's words.</summary>
/// <param name="Text">The banner; null over a page not opened as evidence.</param>
/// <param name="ShowAll">Show all's words: "Show all 214 sessions"; null when the focus filters nothing.</param>
public sealed record FocusBannerState(FocusBannerText? Text, string? ShowAll)
{
    /// <summary>No banner.</summary>
    public static readonly FocusBannerState None = new(null, null);
}
