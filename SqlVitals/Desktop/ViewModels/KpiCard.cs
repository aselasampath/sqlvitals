namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One of a page's summary cards (#185): "18,420" over "Total Plans", in the colour of what it
/// means, with a tip that explains it. The view draws it with a <c>KpiCard</c> border; the value's
/// colour follows <see cref="Tone"/>, a theme brush of the same name.
/// </summary>
public sealed record KpiCard(string Label, string Value, KpiCard.Tones Tone, string Tip)
{
    /// <summary>The theme brush the value is drawn in.</summary>
    public enum Tones { Accent, Info, Success, Warning, Danger }
}
