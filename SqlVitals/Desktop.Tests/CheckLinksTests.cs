using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>Each health check links to the page that explains it (#130).</summary>
public sealed class CheckLinksTests
{
    [Fact]
    public void EveryCheck_LinksToAPageInTheSidebar()
    {
        var pages = NavMenu.AllPages.Select(p => p.Tag).ToHashSet();

        Assert.All(Enum.GetValues<HealthIndicator>(), indicator => Assert.Contains(CheckLinks.For(indicator).PageTag, pages));
    }

    [Theory]
    [InlineData(HealthIndicator.Blocking,            "Blocking chain",  "Processes")]
    [InlineData(HealthIndicator.Cpu,                 "Top CPU queries", "ResourceQueries")]
    [InlineData(HealthIndicator.PageLifeExpectancy,  "Memory",          "Memory")]
    [InlineData(HealthIndicator.MemoryGrantsPending, "Memory grants",   "Memory")]
    [InlineData(HealthIndicator.LogSpace,            "Log space",       "DbStorage")]
    [InlineData(HealthIndicator.TempDbSpace,         "TempDB",          "TempDb")]
    public void TheLinks_AreTheStorys(HealthIndicator indicator, string text, string page)
    {
        Assert.Equal(new CheckLink(text, page), CheckLinks.For(indicator));
    }
}
