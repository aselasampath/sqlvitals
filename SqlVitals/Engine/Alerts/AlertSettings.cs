namespace SqlVitals.Engine.Alerts;

/// <summary>
/// The choices Settings offers for alerts: how many samples in a row an indicator must stay past
/// a threshold before an alert starts, and back to normal before it ends (see <see cref="AlertTracker"/>).
/// </summary>
public static class AlertSettings
{
    public const int DefaultSamples = 3;

    public static IReadOnlyList<int> SampleChoices { get; } = [1, 2, 3, 5, 10];

    /// <summary>The offered count closest to a saved one; the default when none was saved.</summary>
    public static int NormalizeSamples(int samples) =>
        samples <= 0 ? DefaultSamples : SampleChoices.MinBy(c => Math.Abs((long)c - samples));
}
