using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>The typed repository fake the page tests use (#168).</summary>
public class FakeRepositoryTests
{
    [Fact]
    public async Task A_call_is_answered_by_its_function_with_its_arguments()
    {
        IWaitStatsRepository repo = new FakeRepository
        {
            GetSessionPlanAsync = id => Task.FromResult(new SessionPlan($"<plan session=\"{id}\"/>", true)),
        };

        var plan = await repo.GetSessionPlanAsync(53);

        Assert.Equal("<plan session=\"53\"/>", plan.PlanXml);
    }

    [Fact]
    public void A_call_without_a_function_says_it_isnt_faked()
    {
        IWaitStatsRepository repo = new FakeRepository();

        // Thrown as it's called, not in the task: the test stops at the call it didn't set up.
        var error = Assert.Throws<NotSupportedException>(() => { _ = repo.GetProcessesAsync(); });

        Assert.Equal("GetProcessesAsync isn't faked in this test.", error.Message);
    }

    [Fact]
    public async Task An_unreachable_server_fails_the_task_and_every_call_is_told()
    {
        var calls = new List<string>();
        IWaitStatsRepository repo = new FakeRepository
        {
            Unreachable          = true,
            Called               = calls.Add,
            GetTopWaitTypesAsync = () => Task.FromResult<IEnumerable<TopWaitType>>([]),
        };

        await repo.GetTopWaitTypesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.GetProcessesAsync());

        Assert.Equal(["GetTopWaitTypesAsync", "GetProcessesAsync"], calls);
    }
}
