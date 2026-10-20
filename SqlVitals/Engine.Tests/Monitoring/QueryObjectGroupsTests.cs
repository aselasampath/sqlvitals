using System.Globalization;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

/// <summary>Resource Queries grouped by the object each statement belongs to (#232).</summary>
public class QueryObjectGroupsTests
{
    private static ResourceIntensiveQuery Q(string? obj, long reads, long cpuUs, string db = "Sales", string? type = "Stored Proc") =>
        new($"SELECT {reads}", db, obj, 1, reads, reads, cpuUs, cpuUs, 0, 0, 0, 0, default, default, default, ObjectType: obj is null ? "Ad-Hoc" : type);

    [Fact]
    public void A_procedures_statements_are_one_group_with_their_totals()
    {
        var groups = QueryObjectGroups.ByObject([Q("GetOrders", 100, 10), Q("GetOrders", 50, 5), Q("Other", 10, 1)], QueryGroupRank.Reads);

        var orders = groups[0];
        Assert.Equal(("GetOrders", "Stored Proc", 2, 150L, 15L), (orders.Name, orders.Type, orders.Queries, orders.Reads, orders.CpuUs));
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Ad_hoc_queries_are_one_group_with_no_type()
    {
        var groups = QueryObjectGroups.ByObject([Q(null, 10, 1), Q("", 20, 2), Q("  ", 30, 3)], QueryGroupRank.Reads);

        var adHoc = Assert.Single(groups);
        Assert.Equal((QueryObjectGroups.AdHoc, 3), (adHoc.Name, adHoc.Queries));
        Assert.Null(adHoc.Type);
    }

    [Fact]
    public void Many_medium_statements_outrank_one_large_one()
    {
        var groups = QueryObjectGroups.ByObject(
            [Q("OneBig", 1_000, 0), Q("Chatty", 400, 0), Q("Chatty", 400, 0), Q("Chatty", 400, 0)], QueryGroupRank.Reads);

        Assert.Equal(["Chatty", "OneBig"], groups.Select(g => g.Name));
    }

    [Fact]
    public void The_cpu_tab_ranks_by_cpu()
    {
        var rows = new[] { Q("Reader", 1_000, 1), Q("Burner", 1, 1_000) };

        Assert.Equal("Reader", QueryObjectGroups.ByObject(rows, QueryGroupRank.Reads)[0].Name);
        Assert.Equal("Burner", QueryObjectGroups.ByObject(rows, QueryGroupRank.Cpu)[0].Name);
    }

    [Fact]
    public void Equal_totals_are_ordered_by_name() =>
        Assert.Equal(["alpha", "Beta"], QueryObjectGroups.ByObject([Q("Beta", 5, 0), Q("alpha", 5, 0)], QueryGroupRank.Reads).Select(g => g.Name));

    [Fact]
    public void The_key_ignores_case_and_tells_databases_apart()
    {
        Assert.Equal(QueryObjectGroups.KeyOf(Q("GetOrders", 1, 1)), QueryObjectGroups.KeyOf(Q("getorders ", 1, 1)));
        Assert.NotEqual(QueryObjectGroups.KeyOf(Q("GetOrders", 1, 1, db: "Sales")), QueryObjectGroups.KeyOf(Q("GetOrders", 1, 1, db: "Hr")));
        Assert.NotEqual(QueryObjectGroups.KeyOf(Q(null, 1, 1)), QueryObjectGroups.KeyOf(Q("GetOrders", 1, 1)));
    }

    [Theory]
    [InlineData(1,  850,          "1 query · 1,234 reads · CPU 850 µs")]
    [InlineData(3,  12_400,       "3 queries · 1,234 reads · CPU 12 ms")]
    [InlineData(10, 12_345_678,   "10 queries · 1,234 reads · CPU 12.3 s")]
    public void The_summary_reads_in_words(int queries, long cpuUs, string expected) =>
        Assert.Equal(expected, new QueryObjectGroup("k", "n", null, queries, 1_234, cpuUs).Summary(CultureInfo.InvariantCulture));

    [Fact]
    public void Nothing_gives_no_groups() => Assert.Empty(QueryObjectGroups.ByObject([], QueryGroupRank.Cpu));
}
