using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>Query Store, Query Regressions and the history collector's reads of SqlVitalsIT.</summary>
[Collection(SqlServerTestGroup.Name)]
public class QueryStoreTests(SqlServerFixture server)
{
    private static readonly QueryStorePeriod Period = QueryStorePeriod.Last24Hours;

    private readonly IWaitStatsRepository _repo = server.NewRepository();

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreStatusAsync))]
    public async Task Status_IsTheDatabasesReadWriteQueryStore()
    {
        var status = await _repo.GetQueryStoreStatusAsync();

        Assert.Equal(SqlServerFixture.Database, status.DatabaseName);
        Assert.True(status.IsAvailable);
        Assert.Equal("READ_WRITE", status.Health!.ActualState);
        Assert.True(status.Health.ForcedPlans >= 1);
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreAsync))]
    public async Task TopQueriesForExport_AreRead()
    {
        var (health, queries) = await _repo.GetQueryStoreAsync();

        Assert.NotNull(health);
        Assert.NotEmpty(queries.ToList());
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreTopQueriesAsync))]
    public async Task TopQueries_ByEveryMetricAndStatistic()
    {
        foreach (var metric in Enum.GetValues<QueryStoreMetric>())
            foreach (var statistic in Enum.GetValues<QueryStoreStatistic>())
            {
                var rows = await _repo.GetQueryStoreTopQueriesAsync(Period, metric, statistic, top: 25);

                Assert.True(rows.Unavailable is null, $"{metric} {statistic}: {rows.Unavailable}");
                Assert.NotEmpty(rows.Rows);
            }
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreOverTimeAsync))]
    public async Task OverTime_HasTheWorkloadsSlot()
    {
        var status = await _repo.GetQueryStoreStatusAsync();
        var slot = QueryStoreTimeSlots.SlotLength(Period.Length, status.Health!.IntervalLengthMin);
        var origin = QueryStoreTimeSlots.Origin(slot, TimeSpan.Zero);

        var rows = await _repo.GetQueryStoreOverTimeAsync(Period, slot, origin);

        Assert.Null(rows.Unavailable);
        Assert.NotEmpty(rows.Rows);
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreHighVariationAsync))]
    public async Task HighVariation_IsRead()
    {
        var rows = await _repo.GetQueryStoreHighVariationAsync(Period, QueryStoreMetric.Duration, minExecutions: 2, top: 25);

        Assert.Null(rows.Unavailable);
        Assert.NotEmpty(rows.Rows);
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreWaitCategoriesAsync), nameof(IQueryStoreRepository.GetQueryStoreWaitQueriesAsync))]
    public async Task Waits_ByCategory_AndTheQueriesInOne()
    {
        var categories = await _repo.GetQueryStoreWaitCategoriesAsync(Period);
        Assert.Null(categories.Unavailable);

        // The waits Query Store records depend on the run; any category's queries will do.
        var category = categories.Rows.Count > 0 ? categories.Rows[0].Category : 0;
        var queries = await _repo.GetQueryStoreWaitQueriesAsync(Period, category, top: 25);

        Assert.Null(queries.Unavailable);
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreForcedPlansAsync))]
    public async Task ForcedPlans_HaveTheProceduresPlan_AndHintsOn2022()
    {
        var forced = await _repo.GetQueryStoreForcedPlansAsync(Period);

        Assert.Null(forced.Plans.Unavailable);
        Assert.NotEmpty(forced.Plans.Rows);
        if (server.MajorVersion >= 16)
        {
            Assert.NotNull(forced.Hints);
            Assert.Null(forced.Hints.Unavailable);
            Assert.NotEmpty(forced.Hints.Rows);
        }
    }

    [SqlServerFact]
    [Covers(nameof(IQueryStoreRepository.GetQueryStoreQueryDetailsAsync))]
    public async Task QueryDetails_OfTheTopQuery()
    {
        var top = await _repo.GetQueryStoreTopQueriesAsync(Period, QueryStoreMetric.Executions, QueryStoreStatistic.Total, top: 1);

        var details = await _repo.GetQueryStoreQueryDetailsAsync(top.Rows[0].QueryId, Period);

        Assert.NotNull(details.Query);
        Assert.NotEmpty(details.Intervals);
        Assert.NotEmpty(details.Plans);
        Assert.NotEmpty(details.Summaries);
    }

    [SqlServerFact]
    [Covers(nameof(IQueryRegressionRepository.GetQueryStoreRegressionStatsAsync), nameof(IQueryRegressionRepository.GetQueryStoreTextsAsync),
            nameof(IQueryRegressionRepository.GetQueryStorePlanAsync))]
    public async Task Regressions_ReadTheRecentPeriod_ItsTexts_AndAPlan()
    {
        var stats = await _repo.GetQueryStoreRegressionStatsAsync(RegressionWindows.Default);

        Assert.True(stats.IsAvailable);
        Assert.Equal(SqlServerFixture.Database, stats.DatabaseName);
        var recent = stats.Stats.Where(s => s.IsRecent).ToList();
        Assert.NotEmpty(recent);

        var ids = recent.Select(s => long.Parse(s.QueryKey, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToList();
        var texts = await _repo.GetQueryStoreTextsAsync(ids);
        Assert.Equal(ids.Order(), texts.Keys.Order());

        var planId = recent.First(s => s.PlanId is not null).PlanId!.Value;
        Assert.NotNull(await _repo.GetQueryStorePlanAsync(planId));
    }

    [SqlServerFact]
    [Covers(nameof(IHistoryRepository.GetHistoryDetailAsync), nameof(IHistoryRepository.GetQueryTextsAsync),
            nameof(IQueryRegressionRepository.GetCachedPlanForQueryHashAsync))]
    public async Task HistoryDetail_ItsQueryTexts_AndACachedPlan()
    {
        var detail = await _repo.GetHistoryDetailAsync(queriesExecutedSince: null, maxQueries: 50);

        Assert.NotEmpty(detail.Waits);
        Assert.Contains(detail.Files, f => f.DatabaseName == SqlServerFixture.Database);
        Assert.NotEmpty(detail.Queries);
        Assert.NotNull(detail.Memory);
        Assert.NotEmpty(detail.Counters ?? []);

        var hashes = detail.Queries.Select(q => q.QueryHash).ToList();
        var texts = await _repo.GetQueryTextsAsync(hashes, maxLength: 4000);
        Assert.NotEmpty(texts);

        // A hash still in the plan cache has its plan.
        string? plan = null;
        foreach (var hash in hashes)
            if ((plan = await _repo.GetCachedPlanForQueryHashAsync(hash)) is not null)
                break;
        Assert.NotNull(plan);
    }
}
