using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Services;

/// <summary>A link from a health check to the page that explains it, e.g. "Blocking chain" → Processes.</summary>
/// <param name="PageTag">The page's tag in <see cref="NavMenu"/>.</param>
public sealed record CheckLink(string Text, string PageTag);

/// <summary>
/// Where each health check is looked into (#130): the server detail page links a check that's
/// costing points to the page that shows why.
/// </summary>
public static class CheckLinks
{
    public static CheckLink For(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => new("Top CPU queries", "ResourceQueries"),
        HealthIndicator.Blocking            => new("Blocking chain", "Processes"),
        HealthIndicator.PageLifeExpectancy  => new("Memory", "Memory"),
        HealthIndicator.MemoryGrantsPending => new("Memory grants", "Memory"),
        HealthIndicator.LogSpace            => new("Log space", "DbStorage"),
        HealthIndicator.TempDbSpace         => new("TempDB", "TempDb"),
        _ => throw new ArgumentOutOfRangeException(nameof(indicator), indicator, "No page explains this check."),
    };
}
