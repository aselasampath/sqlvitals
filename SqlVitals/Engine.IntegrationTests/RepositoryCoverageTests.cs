using System.Reflection;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// Every repository method runs against SQL Server at least once (#46): each is named by a
/// test's [Covers]. Runs without a server, so a new method without a test fails the build's tests.
/// </summary>
public class RepositoryCoverageTests
{
    [Fact]
    public void EveryRepositoryMethod_IsRunByATest()
    {
        var methods = new[] { typeof(IWaitStatsRepository) }
            .Concat(typeof(IWaitStatsRepository).GetInterfaces())
            .SelectMany(i => i.GetMethods())
            .Select(m => m.Name)
            .ToHashSet();

        var covered = typeof(RepositoryCoverageTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttribute<SqlServerFactAttribute>() is not null)
            .SelectMany(m => m.GetCustomAttributes<CoversAttribute>())
            .SelectMany(c => c.Methods)
            .ToHashSet();

        Assert.Empty(methods.Except(covered).Order());
        Assert.Empty(covered.Except(methods).Order());
    }
}
