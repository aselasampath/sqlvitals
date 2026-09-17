using SqlPulse.Engine.Repositories;

namespace SqlPulse.Engine.Tests.Repositories;

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
        Assert.Equal(expectedColor, WaitStatsRepository.CategoryColor(category));
    }

    [Theory]
    [InlineData("CPU", 1)]
    [InlineData("I/O", 2)]
    [InlineData("Memory", 3)]
    [InlineData("Locks", 4)]
    [InlineData("Network", 5)]
    [InlineData("Other", 6)]
    [InlineData("SomeUnknownCategory", 7)]
    public void CategorySortOrder_MapsKnownCategoriesToTheirRank(string category, int expectedOrder)
    {
        Assert.Equal(expectedOrder, WaitStatsRepository.CategorySortOrder(category));
    }
}
