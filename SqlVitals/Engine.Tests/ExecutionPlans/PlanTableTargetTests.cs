using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Engine.Tests.ExecutionPlans;

/// <summary>Which table and statistics object an operator's link opens (#244).</summary>
public class PlanTableTargetTests
{
    private static string Plan(string objectAttributes, string statsUsage = "") => $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.5">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementId="1" StatementType="SELECT" StatementSubTreeCost="1">
              <QueryPlan>
                {statsUsage}
                <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="1" EstimatedTotalSubtreeCost="1">
                  <OutputList/>
                  <NestedLoops Optimized="0">
                    <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.5">
                      <OutputList/>
                      <IndexScan><Object {objectAttributes}/></IndexScan>
                    </RelOp>
                  </NestedLoops>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    private const string Orders = """Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Date]" """;

    private static string Used(params (string Table, string Statistics)[] statistics) =>
        "<OptimizerStatsUsage>" + string.Concat(statistics.Select(s =>
            $"""<StatisticsInfo Database="[SalesDb]" Schema="[dbo]" Table="[{s.Table}]" Statistics="[{s.Statistics}]" ModificationCount="5" SamplingPercent="100" LastUpdate="2026-11-01T00:00:00"/>""")) +
        "</OptimizerStatsUsage>";

    private static (ExecutionPlan Plan, PlanOperator Seek, PlanOperator Join, PlanOperator Root) Parsed(string xml)
    {
        var plan = ShowplanParser.Parse(xml);
        Assert.Null(plan.Error);
        var root = plan.Statements[0].Root;
        var join = root.Children[0];
        return (plan, join.Children[0], join, root);
    }

    [Fact]
    public void AnOperatorOnATable_OpensItsDatabaseAndTable_WithoutTheirBrackets()
    {
        var (plan, seek, _, _) = Parsed(Plan(Orders));

        var target = PlanTableTarget.For(plan, seek);

        Assert.NotNull(target);
        Assert.Equal("SalesDb", target.Database);
        Assert.Equal("dbo", target.Schema);
        Assert.Equal("Orders", target.Table);
        Assert.Equal("dbo.Orders", target.FullName);
    }

    [Fact]
    public void APlanWithNoStatisticsInformation_OpensTheTableAlone()
    {
        var (plan, seek, _, _) = Parsed(Plan(Orders));

        Assert.Empty(plan.Statements[0].StatisticsUsed);
        Assert.Null(PlanTableTarget.For(plan, seek)!.Statistics);
    }

    [Fact]
    public void TheOperatorsOwnIndex_IsTheOneOpened_WhenTheOptimizerUsedIt()
    {
        var (plan, seek, _, _) = Parsed(Plan(Orders, Used(("Orders", "PK_Orders"), ("Orders", "IX_Orders_Date"))));

        Assert.Equal("IX_Orders_Date", PlanTableTarget.For(plan, seek)!.Statistics);
    }

    [Fact]
    public void WithoutItsOwnIndexAmongThem_TheFirstListedIsOpened()
    {
        var (plan, seek, _, _) = Parsed(Plan(Orders, Used(("Orders", "_WA_Sys_Status"), ("Orders", "PK_Orders"))));

        Assert.Equal("_WA_Sys_Status", PlanTableTarget.For(plan, seek)!.Statistics);
    }

    [Fact]
    public void AnotherTablesStatistics_AreNotOpened()
    {
        var (plan, seek, _, _) = Parsed(Plan(Orders, Used(("Customers", "PK_Customers"))));

        Assert.Null(PlanTableTarget.For(plan, seek)!.Statistics);
    }

    [Fact]
    public void OperatorsWithNoTable_HaveNoTarget()
    {
        var (plan, _, join, root) = Parsed(Plan(Orders));

        Assert.Null(PlanTableTarget.For(plan, join));
        Assert.Null(PlanTableTarget.For(plan, root));
    }

    [Theory]
    [InlineData("""Table="[@orders]" """)]
    [InlineData("""Database="[tempdb]" Schema="[dbo]" Table="[#orders___00001]" """)]
    [InlineData("""Database="[SalesDb]" Schema="[sys]" Table="[sysrscols]" """)]
    [InlineData("""Database="[mssqlsystemresource]" Schema="[dbo]" Table="[spt_values]" """)]
    [InlineData("""Schema="[dbo]" """)]
    [InlineData("""Database="[SalesDb]" Schema="[dbo]" Table="[vOrderTotals]" Index="[IX_v]" IndexKind="ViewClustered" """)]
    public void TablesThePageDoesntList_HaveNoTarget(string objectAttributes)
    {
        var (plan, seek, _, _) = Parsed(Plan(objectAttributes));

        Assert.Null(PlanTableTarget.For(plan, seek));
    }

    [Fact]
    public void AnOperatorOfAnotherPlan_FindsNoStatistics()
    {
        var (_, seek, _, _) = Parsed(Plan(Orders, Used(("Orders", "IX_Orders_Date"))));

        Assert.Null(PlanTableTarget.For(ExecutionPlan.Empty, seek)!.Statistics);
    }
}
