using System.Reflection;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Repositories;

public class PlanCacheHealthSqlTests
{
    [Theory]
    [MemberData(nameof(QueryProfileSql))]
    public void QueryProfiles_UseTheRealColumnNames(string sql)
    {
        // sys.dm_exec_query_profiles has estimate_row_count and row_count on every version;
        // estimated_row_count and actual_row_count don't exist, so naming them fails with 207.
        Assert.DoesNotContain("estimated_row_count", sql);
        Assert.DoesNotContain("actual_row_count", sql);
        Assert.Contains("qp.row_count", sql);
    }

    public static TheoryData<string> QueryProfileSql =>
        new() { PlanCacheHealthRepository.CardinalitySql, PlanCacheHealthRepository.SkewSql };

    [Fact]
    public void Cardinality_SumsEachNodesThreads()
    {
        var sql = PlanCacheHealthRepository.CardinalitySql;

        Assert.Contains("SUM(qp.estimate_row_count)  AS EstimatedRows", sql);
        Assert.Contains("SUM(qp.row_count)           AS ActualRows", sql);
        Assert.Contains("GROUP BY qp.session_id, qp.request_id, qp.node_id", sql);
    }

    [Theory]
    [MemberData(nameof(QueryProfileSql))]
    public void QueryProfiles_NeverDivideByZero(string sql)
    {
        // SQL Server needn't apply "EstimatedRows > 0" before a division in the SELECT or the
        // WHERE: on 2019+ any running request has profile rows, and a node with 0 estimated
        // rows failed the whole page with error 8134 (#46). Every divisor is NULLIF(…, 0).
        var divisors = System.Text.RegularExpressions.Regex.Matches(sql, @"/\s*(\S+)");

        Assert.NotEmpty(divisors);
        Assert.All(divisors, d => Assert.StartsWith("NULLIF(", d.Groups[1].Value, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(QueryProfileSql))]
    public void QueryProfiles_JoinTheirOwnRequest(string sql)
    {
        // A MARS session can have more than one request.
        Assert.Contains("r.request_id = ", sql);
    }

    [Theory]
    [MemberData(nameof(QueryProfileSql))]
    public void QueryProfiles_KeepTheColumnAliasesTheMappingReads(string sql)
    {
        Assert.Contains("SessionId", sql);
        Assert.Contains("NodeId", sql);
        Assert.Contains("DatabaseName", sql);
        Assert.Contains("QueryText", sql);
    }

    [Theory]
    [InlineData(207)]
    [InlineData(208)]
    public async Task Optional_MissingColumnOrObject_LeavesTheSectionEmpty(int number)
    {
        var rows = await PlanCacheHealthRepository.Optional(Fail(number));

        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(-2)]    // timeout
    [InlineData(229)]   // permission denied
    public async Task Optional_OtherErrors_StillFailThePage(int number)
    {
        var ex = await Assert.ThrowsAsync<WaitStatsException>(
            () => PlanCacheHealthRepository.Optional(Fail(number)));

        Assert.Equal(number, ex.SqlErrorNumber);
    }

    [Fact]
    public async Task Optional_Success_ReturnsTheRows()
    {
        IEnumerable<dynamic> rows = [new { SessionId = 51 }];

        Assert.Same(rows, await PlanCacheHealthRepository.Optional(Task.FromResult(rows)));
    }

    // What BaseRepository.Q throws: the SqlException wrapped in a WaitStatsException.
    private static Task<IEnumerable<dynamic>> Fail(int number) =>
        Task.FromException<IEnumerable<dynamic>>(
            new WaitStatsException(nameof(PlanCacheHealthRepository), "GetPlanCacheHealthAsync", SqlError(number)));

    // SqlException has no public constructor, so build one the way SqlClient does.
    private static SqlException SqlError(int number)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var errorCtor = typeof(SqlError).GetConstructors(any)
            .OrderByDescending(c => c.GetParameters().Length)
            .First(c => c.GetParameters()[0].ParameterType == typeof(int));
        var error = errorCtor.Invoke(errorCtor.GetParameters().Select((p, i) =>
            i == 0                          ? number
            : p.ParameterType == typeof(byte)   ? (byte)16
            : p.ParameterType == typeof(string) ? $"Error {number}"
            : p.ParameterType.IsValueType       ? Activator.CreateInstance(p.ParameterType)
            : null).ToArray());

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructors(any)
            .First(c => c.GetParameters().Length == 0).Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", any)!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethod("CreateException", any,
            [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [errors, "16.00.1000"])!;
    }
}
