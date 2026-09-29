using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Tests.QueryStore;

public class CombinedStdDevTests
{
    [Fact]
    public void One_interval_gives_its_own_std_dev()
    {
        var s = new CombinedStdDev().Add(10, 5.0, 2.0);

        Assert.Equal(10, s.Count);
        Assert.Equal(5.0, s.Mean, 9);
        Assert.Equal(2.0, s.StdDev, 9);
    }

    [Fact]
    public void Intervals_with_the_same_mean_pool_their_variances()
    {
        var s = new CombinedStdDev().Add(10, 5, 2).Add(30, 5, 4);

        // (10 × 4 + 30 × 16) / 40 = 13
        Assert.Equal(Math.Sqrt(13), s.StdDev, 9);
    }

    [Fact]
    public void The_difference_between_intervals_is_counted()
    {
        // Steady within each interval, but half the executions take 0 and half take 10.
        var s = new CombinedStdDev().Add(10, 0, 0).Add(10, 10, 0);

        Assert.Equal(5, s.Mean, 9);
        Assert.Equal(5, s.StdDev, 9);
    }

    [Fact]
    public void It_matches_the_std_dev_of_every_execution()
    {
        var random = new Random(111);
        var groups = Enumerable.Range(0, 6)
            .Select(g => Enumerable.Range(0, random.Next(1, 40)).Select(_ => random.NextDouble() * 100 * (g + 1)).ToList())
            .ToList();

        var combined = new CombinedStdDev();
        foreach (var g in groups)
            combined.Add(g.Count, g.Average(), PopulationStdDev(g));

        var all = groups.SelectMany(g => g).ToList();
        Assert.Equal(all.Average(), combined.Mean, 6);
        Assert.Equal(PopulationStdDev(all), combined.StdDev, 6);
    }

    [Fact]
    public void Zero_executions_give_zero_and_add_nothing()
    {
        var s = new CombinedStdDev();
        Assert.Equal(0, s.Mean);
        Assert.Equal(0, s.StdDev);

        s.Add(0, 100, 50);
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.StdDev);
        Assert.Equal(0, CombinedStdDev.FromSums(0, 10, 10));
    }

    [Fact]
    public void Rounding_just_below_zero_clamps_to_zero()
    {
        // Every execution took 1: the variance should be 0 but rounds a hair below.
        Assert.True(CombinedStdDev.Variance(3, 3, 2.9999999999999996) < 0);
        Assert.Equal(0, CombinedStdDev.FromSums(3, 3, 2.9999999999999996));
    }

    private static double PopulationStdDev(IReadOnlyCollection<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }
}
