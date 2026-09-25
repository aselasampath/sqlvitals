using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Engine.Tests.ExecutionPlans;

public class ShowplanParserTests
{
    private const double Tolerance = 0.001;

    private static ExecutionPlan Load(string fileName) =>
        ShowplanParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ExecutionPlans", "Plans", fileName)));

    private static PlanOperator Node(PlanStatement statement, int nodeId) =>
        statement.Root.Descendants().Single(o => !o.IsStatementRoot && o.NodeId == nodeId);

    // ── Single statement ─────────────────────────────────────────────────

    [Fact]
    public void Parse_SingleStatement_ReadsTheStatementAndItsRootNode()
    {
        var plan = Load("SingleStatement.sqlplan");

        Assert.Null(plan.Error);
        Assert.False(plan.IsActualPlan);
        var statement = Assert.Single(plan.Statements);
        Assert.Equal(1, statement.Number);
        Assert.Equal("SELECT", statement.StatementType);
        Assert.StartsWith("SELECT o.OrderId", statement.Text);
        Assert.Equal(100, statement.CostPercentOfBatch, Tolerance);
        Assert.True(statement.HasQueryPlan);

        Assert.True(statement.Root.IsStatementRoot);
        Assert.Equal("SELECT", statement.Root.PhysicalOp);
        Assert.Equal(5, plan.OperatorCount);
    }

    [Fact]
    public void Parse_SingleStatement_BuildsTheOperatorTreeInDocumentOrder()
    {
        var statement = Load("SingleStatement.sqlplan").Statements[0];

        var top = Assert.Single(statement.Root.Children);
        Assert.Equal(0, top.NodeId);
        Assert.Equal(new[] { 1, 4 }, top.Children.Select(c => c.NodeId));
        Assert.Equal(new[] { 2, 3 }, top.Children[0].Children.Select(c => c.NodeId));
        Assert.Empty(Node(statement, 4).Children);
    }

    [Fact]
    public void Parse_OperatorCostIsSubtreeCostLessChildrenAndPercentagesAddUpTo100()
    {
        var statement = Load("SingleStatement.sqlplan").Statements[0];

        Assert.Equal(0.0002, Node(statement, 0).EstimatedOperatorCost, 6);
        Assert.Equal(0.0003, Node(statement, 1).EstimatedOperatorCost, 6);
        Assert.Equal(0.0062, Node(statement, 3).EstimatedOperatorCost, 6);
        Assert.Equal(0.0062 / 0.0132 * 100, Node(statement, 3).CostPercent, Tolerance);

        var total = statement.Root.Descendants().Where(o => !o.IsStatementRoot).Sum(o => o.CostPercent);
        Assert.Equal(100, total, Tolerance);
    }

    [Fact]
    public void Parse_ShowsALookupAsKeyLookupWithItsIndexKind()
    {
        var lookup = Node(Load("SingleStatement.sqlplan").Statements[0], 3);

        Assert.Equal("Key Lookup", lookup.PhysicalOp);
        Assert.Equal("Clustered", lookup.Subtitle);
        Assert.Equal("[dbo].[Orders].[PK_Orders] [o]", lookup.ObjectName);
    }

    [Fact]
    public void Parse_EstimatedExecutionsCountRebindsAndRewinds()
    {
        var lookup = Node(Load("SingleStatement.sqlplan").Statements[0], 3);

        Assert.Equal(3, lookup.EstimatedExecutions);
        Assert.Equal(1, lookup.EstimateRows);
        Assert.Equal(3, lookup.EstimatedRowsAllExecutions);
    }

    [Fact]
    public void Parse_ReadsSeekPredicatesOrderingAndOutputList()
    {
        var statement = Load("SingleStatement.sqlplan").Statements[0];
        var seek = Node(statement, 2);

        Assert.Equal("Index Seek", seek.PhysicalOp);
        Assert.Equal("NonClustered", seek.Subtitle);
        Assert.Equal("Prefix: [o].CustomerId = Scalar Operator([@CustomerId])", seek.SeekPredicate);
        Assert.True(seek.Ordered);
        Assert.Equal("[o].OrderId, [o].OrderDate, [c].Name", Node(statement, 0).OutputList);
    }

    [Fact]
    public void Parse_TheSubtitleIsTheLogicalOperationWhenItDiffers()
    {
        var join = Node(Load("SingleStatement.sqlplan").Statements[0], 0);

        Assert.Equal("Nested Loops", join.PhysicalOp);
        Assert.Equal("Inner Join", join.Subtitle);
    }

    [Fact]
    public void Parse_OperatorPropertiesIncludeSummaryAndEveryXmlAttribute()
    {
        var seek = Node(Load("SingleStatement.sqlplan").Statements[0], 2);

        Assert.Contains(seek.Properties, p => p.Name == "Seek Predicates" && p.Value == seek.SeekPredicate);
        Assert.Contains(seek.Properties, p => p.Name == "Estimated Number of Executions" && p.Value == "1");

        var attributes = Assert.Single(seek.Properties, p => p.Name == "RelOp Attributes");
        Assert.Contains(attributes.Children, p => p.Name == "Table Cardinality" && p.Value == "200000");

        var indexScan = Assert.Single(seek.Properties, p => p.Name == "Index Scan");
        var obj = Assert.Single(indexScan.Children, p => p.Name == "Object");
        Assert.Contains(obj.Children, p => p.Name == "Index" && p.Value == "[IX_Orders_CustomerId]");
    }

    [Fact]
    public void Parse_EstimatedPlansHaveNoActualsOrBadEstimates()
    {
        var statement = Load("SingleStatement.sqlplan").Statements[0];

        Assert.All(statement.Root.Descendants(), o =>
        {
            Assert.False(o.HasActuals);
            Assert.Null(o.EstimateErrorFactor);
            Assert.False(o.IsBadEstimate);
        });
    }

    // ── Several statements and batches ───────────────────────────────────

    [Fact]
    public void Parse_Batch_ListsEveryStatementIncludingIfBranchesInOrder()
    {
        var plan = Load("MultiStatementBatch.sqlplan");

        Assert.Null(plan.Error);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, plan.Statements.Select(s => s.Number));
        Assert.Equal(new[] { "ASSIGN", "COND WITH QUERY", "SELECT", "SELECT", "UPDATE", "SELECT" },
                     plan.Statements.Select(s => s.StatementType));
    }

    [Fact]
    public void Parse_Batch_StatementCostIsRelativeToItsOwnBatch()
    {
        var pct = Load("MultiStatementBatch.sqlplan").Statements.Select(s => s.CostPercentOfBatch).ToList();

        Assert.Equal(0, pct[0], Tolerance);
        Assert.Equal(10, pct[1], Tolerance);
        Assert.Equal(30, pct[2], Tolerance);
        Assert.Equal(10, pct[3], Tolerance);
        Assert.Equal(50, pct[4], Tolerance);
        Assert.Equal(100, pct[5], Tolerance);   // the only statement of the second batch
    }

    [Fact]
    public void Parse_Batch_StatementsWithoutAQueryPlanKeepJustTheirRootNode()
    {
        var assign = Load("MultiStatementBatch.sqlplan").Statements[0];

        Assert.False(assign.HasQueryPlan);
        Assert.Equal("ASSIGN", assign.Root.PhysicalOp);
        Assert.Empty(assign.Root.Children);
    }

    [Fact]
    public void Parse_Batch_ConditionWithQueryReadsThePlanUnderCondition()
    {
        var cond = Load("MultiStatementBatch.sqlplan").Statements[1];

        var compute = Assert.Single(cond.Root.Children);
        Assert.Equal("Compute Scalar", compute.PhysicalOp);
        var scan = Assert.Single(compute.Children);
        Assert.Equal("Clustered Index Scan", scan.PhysicalOp);
        Assert.Equal("[SalesDb].[dbo].[Orders].[Status]=[@Status]", scan.Predicate);
    }

    [Fact]
    public void Parse_Batch_ExpressionsInsideAnOperatorAreNotChildOperators()
    {
        var update = Assert.Single(Load("MultiStatementBatch.sqlplan").Statements[4].Root.Children);

        Assert.Equal("Clustered Index Update", update.PhysicalOp);
        Assert.Equal("Update", update.Subtitle);
        var compute = Assert.Single(update.Children);
        Assert.Equal("Compute Scalar", compute.PhysicalOp);
        Assert.Equal("Clustered Index Seek", Assert.Single(compute.Children).PhysicalOp);
    }

    // ── Parallel plans ───────────────────────────────────────────────────

    [Fact]
    public void Parse_Parallel_FlagsParallelOperatorsAndReadsExchangeTypes()
    {
        var statement = Assert.Single(Load("Parallel.sqlplan").Statements);
        var operators = statement.Root.Descendants().Where(o => !o.IsStatementRoot).ToList();

        Assert.Equal(6, operators.Count);
        Assert.All(operators, o => Assert.True(o.IsParallel));
        Assert.Equal("Parallelism", operators[0].PhysicalOp);
        Assert.Equal("Gather Streams", operators[0].Subtitle);
        Assert.Equal("Repartition Streams", Node(statement, 2).Subtitle);
        Assert.Equal("Aggregate", Node(statement, 1).Subtitle);
        Assert.Contains(statement.Root.Properties, p => p.Name == "Degree of Parallelism" && p.Value == "4");
    }

    [Fact]
    public void Parse_Parallel_TheScanIsTheMostExpensiveOperator()
    {
        var statement = Load("Parallel.sqlplan").Statements[0];

        var costliest = statement.Root.Descendants().MaxBy(o => o.CostPercent)!;
        Assert.Equal(5, costliest.NodeId);
        Assert.Equal(70, costliest.CostPercent, Tolerance);
    }

    // ── Actual plans and warnings ────────────────────────────────────────

    [Fact]
    public void Parse_Actual_SumsRowsAndExecutionsAcrossThreadsAndTakesTheSlowestThread()
    {
        var plan = Load("ActualWithWarnings.sqlplan");
        var scan = Node(plan.Statements[0], 3);

        Assert.True(plan.IsActualPlan);
        Assert.Equal(5000, scan.ActualRows);
        Assert.Equal(2, scan.ActualExecutions);
        Assert.Equal(55, scan.ActualElapsedMs);
        Assert.Equal("Row", scan.ActualExecutionMode);
        Assert.Contains(scan.Properties, p => p.Name == "Actual Number of Rows" && p.Value == "5,000");
    }

    [Fact]
    public void Parse_Actual_FlagsEstimatesThatAreOffByTenTimesOrMore()
    {
        var statement = Load("ActualWithWarnings.sqlplan").Statements[0];

        Assert.Equal(500, Node(statement, 0).EstimateErrorFactor!.Value, Tolerance);
        Assert.True(Node(statement, 0).IsBadEstimate);      // 10 estimated, 5,000 actual
        Assert.False(Node(statement, 1).IsBadEstimate);     // 1,000 estimated, 5,000 actual
        Assert.False(Node(statement, 2).IsBadEstimate);     // spot on
        Assert.True(Node(statement, 3).IsBadEstimate);
    }

    [Fact]
    public void Parse_Actual_ReadsOperatorWarnings()
    {
        var statement = Load("ActualWithWarnings.sqlplan").Statements[0];

        var sort = Node(statement, 0);
        Assert.Equal(2, sort.Warnings.Count);
        Assert.All(sort.Warnings, w => Assert.Equal(PlanWarningKind.SpillToTempDb, w.Kind));
        Assert.Contains("spill level 1", sort.Warnings[0].Message);
        Assert.Contains("812 pages written to tempdb", sort.Warnings[1].Message);

        var join = Assert.Single(Node(statement, 1).Warnings);
        Assert.Equal(PlanWarningKind.NoJoinPredicate, join.Kind);

        var noStats = Assert.Single(Node(statement, 3).Warnings);
        Assert.Equal(PlanWarningKind.NoStatistics, noStats.Kind);
        Assert.Equal("Columns with no statistics: [dbo].[Orders].Status", noStats.Message);

        Assert.Empty(Node(statement, 2).Warnings);
    }

    [Fact]
    public void Parse_Actual_StatementWarningsGoOnTheStatementNode()
    {
        var root = Load("ActualWithWarnings.sqlplan").Statements[0].Root;

        Assert.Equal(new[] { PlanWarningKind.ImplicitConversion, PlanWarningKind.MemoryGrant },
                     root.Warnings.Select(w => w.Kind));
        Assert.Equal("Type conversion in expression (CONVERT_IMPLICIT(nvarchar(20),[c].[CustomerCode],0)=[@Code]) " +
                     "may affect \"Seek Plan\" in query plan choice", root.Warnings[0].Message);
        Assert.Equal("Excessive Grant — requested 1024 KB, granted 1024 KB, used 16 KB", root.Warnings[1].Message);
    }

    [Fact]
    public void Parse_Actual_ReadsMissingIndexesAsACreateStatement()
    {
        var statement = Load("ActualWithWarnings.sqlplan").Statements[0];

        var index = Assert.Single(statement.MissingIndexes);
        Assert.Equal(87.5, index.Impact, Tolerance);
        Assert.Equal("[dbo].[Orders]", index.Table);
        Assert.Equal(new[] { "[Status]" }, index.EqualityColumns);
        Assert.Equal(new[] { "[OrderDate]" }, index.InequalityColumns);
        Assert.Equal(new[] { "[CustomerId]" }, index.IncludeColumns);
        Assert.Equal("CREATE NONCLUSTERED INDEX [IX_Orders_Status_OrderDate] ON [dbo].[Orders] ([Status], [OrderDate]) " +
                     "INCLUDE ([CustomerId])", index.CreateStatement);
    }

    // ── Bad and unusual input ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoPlanIsAnEmptyPlanWithoutAnError(string? xml)
    {
        var plan = ShowplanParser.Parse(xml);

        Assert.Empty(plan.Statements);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void Parse_TruncatedXmlReturnsAnErrorInsteadOfThrowing()
    {
        var plan = Load("Malformed.sqlplan");

        Assert.Empty(plan.Statements);
        Assert.StartsWith("The plan XML could not be read", plan.Error);
    }

    [Fact]
    public void Parse_TextThatIsNotXmlReturnsAnError()
    {
        Assert.NotNull(ShowplanParser.Parse("(No plan: evicted)").Error);
    }

    [Fact]
    public void Parse_UnknownOperatorsAreKeptWithTheirChildren()
    {
        const string xml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><Batch><Statements>
              <StmtSimple StatementType="SELECT" StatementSubTreeCost="2">
                <QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Quantum Seek" LogicalOp="Quantum Seek" EstimateRows="1" EstimatedTotalSubtreeCost="2">
                    <QuantumSeek Entanglement="high">
                      <RelOp NodeId="1" EstimateRows="1" EstimatedTotalSubtreeCost="1">
                        <FutureScan />
                      </RelOp>
                    </QuantumSeek>
                  </RelOp>
                </QueryPlan>
              </StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;

        var plan = ShowplanParser.Parse(xml);

        Assert.Null(plan.Error);
        var op = Assert.Single(plan.Statements[0].Root.Children);
        Assert.Equal("Quantum Seek", op.PhysicalOp);
        Assert.Equal(50, op.CostPercent, Tolerance);
        var child = Assert.Single(op.Children);
        Assert.Equal("FutureScan", child.PhysicalOp);           // no PhysicalOp: the element name
        Assert.Contains(op.Properties, p => p.Name == "Quantum Seek"
            && p.Children.Any(c => c.Name == "Entanglement" && c.Value == "high"));
    }

    [Fact]
    public void Parse_SubqueriesInsideExpressionsAreNotDrawnAsChildren()
    {
        const string xml = """
            <ShowPlanXML><BatchSequence><Batch><Statements>
              <StmtSimple StatementType="SELECT" StatementSubTreeCost="1">
                <QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Filter" LogicalOp="Filter" EstimatedTotalSubtreeCost="1">
                    <Filter>
                      <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimatedTotalSubtreeCost="0.5"><TableScan /></RelOp>
                      <Predicate>
                        <ScalarOperator ScalarString="[x] = (SELECT MAX(y) FROM t)">
                          <Subquery>
                            <RelOp NodeId="9" PhysicalOp="Stream Aggregate" LogicalOp="Aggregate" EstimatedTotalSubtreeCost="0.1"><StreamAggregate /></RelOp>
                          </Subquery>
                        </ScalarOperator>
                      </Predicate>
                    </Filter>
                  </RelOp>
                </QueryPlan>
              </StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;

        var filter = Assert.Single(ShowplanParser.Parse(xml).Statements[0].Root.Children);

        Assert.Equal(1, Assert.Single(filter.Children).NodeId);
        Assert.Equal("[x] = (SELECT MAX(y) FROM t)", filter.Predicate);
    }

    [Fact]
    public void Parse_UnreadableNumbersAreZero()
    {
        const string xml = """
            <ShowPlanXML><BatchSequence><Batch><Statements>
              <StmtSimple StatementType="SELECT" StatementSubTreeCost="lots">
                <QueryPlan>
                  <RelOp NodeId="x" PhysicalOp="Table Scan" LogicalOp="Table Scan" EstimateRows="NaN" EstimatedTotalSubtreeCost="">
                    <TableScan />
                  </RelOp>
                </QueryPlan>
              </StmtSimple>
            </Statements></Batch></BatchSequence></ShowPlanXML>
            """;

        var plan = ShowplanParser.Parse(xml);

        Assert.Null(plan.Error);
        var scan = Assert.Single(plan.Statements[0].Root.Children);
        Assert.Equal(0, scan.NodeId);
        Assert.Equal(0, scan.EstimateRows);
        Assert.Equal(0, scan.CostPercent);
        Assert.Equal(0, plan.Statements[0].CostPercentOfBatch);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("EstimatedTotalSubtreeCost", "Estimated Total Subtree Cost")]
    [InlineData("EstimateCPU", "Estimate CPU")]
    [InlineData("NodeId", "Node Id")]
    [InlineData("Parallel", "Parallel")]
    public void Humanize_SplitsPascalCaseIntoWords(string name, string expected)
    {
        Assert.Equal(expected, ShowplanParser.Humanize(name));
    }

    [Theory]
    [InlineData("key lookup", true)]
    [InlineData("PK_ORDERS", true)]
    [InlineData("OrderDate", false)]       // only in the output list, which Find does not search
    [InlineData("", false)]
    public void Matches_SearchesNameObjectAndPredicates(string term, bool expected)
    {
        var lookup = Node(Load("SingleStatement.sqlplan").Statements[0], 3);

        Assert.Equal(expected, lookup.Matches(term));
    }
}
