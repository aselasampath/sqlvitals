using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class WaitStatsRepositoryCategoryTests
{
    [Theory]
    [InlineData("CPU", "#FF6B6B")]
    [InlineData("I/O", "#FFA62B")]
    [InlineData("Memory", "#7B68EE")]
    [InlineData("Locks", "#FF4560")]
    [InlineData("Network", "#00D9FF")]
    [InlineData("Benign/Idle", "#555555")]
    [InlineData("SomeUnknownCategory", "#26E7A6")]
    public void CategoryColor_MapsKnownCategoriesToTheirColor(string category, string expectedColor)
    {
        Assert.Equal(expectedColor, WaitsRepository.CategoryColor(category));
    }
}
