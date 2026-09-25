namespace SqlVitals.Engine.ExecutionPlans;

/// <summary>
/// A showplan document read into an operator tree per statement, ready to draw.
/// Built by <see cref="ShowplanParser"/>; never null, even for bad input.
/// </summary>
public sealed record ExecutionPlan(
    IReadOnlyList<PlanStatement> Statements,
    bool IsActualPlan,
    string? Error)
{
    public static readonly ExecutionPlan Empty = new([], false, null);

    public static ExecutionPlan Failed(string error) => new([], false, error);

    public int OperatorCount => Statements.Sum(s => s.Root.Descendants().Count(o => !o.IsStatementRoot));
}

/// <summary>
/// One statement of the batch. Statements without a query plan (SET, IF without a query,
/// RETURN) are kept so the numbering matches SSMS; their <see cref="Root"/> has no children.
/// </summary>
public sealed record PlanStatement(
    int Number,
    string StatementType,
    string? Text,
    double SubtreeCost,
    double CostPercentOfBatch,          // share of its batch's cost, 0–100
    PlanOperator Root,
    IReadOnlyList<PlanMissingIndex> MissingIndexes)
{
    public bool HasQueryPlan => Root.Children.Count > 0;
}

public sealed record PlanMissingIndex(
    double Impact,
    string Table,
    IReadOnlyList<string> EqualityColumns,
    IReadOnlyList<string> InequalityColumns,
    IReadOnlyList<string> IncludeColumns,
    string CreateStatement);

public enum PlanWarningKind
{
    SpillToTempDb,
    NoStatistics,
    NoJoinPredicate,
    ImplicitConversion,
    MemoryGrant,
    UnmatchedIndexes,
    Other,
}

public sealed record PlanWarning(PlanWarningKind Kind, string Message);

/// <summary>A name/value pair for the Properties panel; values or children, or both.</summary>
public sealed record PlanProperty(string Name, string? Value, IReadOnlyList<PlanProperty> Children)
{
    public static PlanProperty Leaf(string name, string? value) => new(name, value, []);
}

/// <summary>
/// One node of the diagram: a <c>RelOp</c>, or the statement node (SELECT, INSERT, COND…)
/// that SSMS draws at the far left of each statement.
/// </summary>
public sealed class PlanOperator
{
    /// <summary>A plan whose actual and estimated rows differ by this factor is flagged.</summary>
    public const double BadEstimateFactor = 10;

    public int NodeId { get; init; } = -1;
    public bool IsStatementRoot { get; init; }
    public required string PhysicalOp { get; init; }
    public required string LogicalOp { get; init; }

    /// <summary>Clustered, NonClustered, Heap… from the operator's <c>Object</c>, when it has one.</summary>
    public string? IndexKind { get; init; }

    /// <summary>For example <c>[dbo].[Orders].[IX_Orders_CustomerId] [o]</c>.</summary>
    public string? ObjectName { get; init; }

    public double EstimatedOperatorCost { get; init; }
    public double EstimatedSubtreeCost { get; init; }

    /// <summary>Operator cost as a share of its statement, 0–100.</summary>
    public double CostPercent { get; init; }

    /// <summary>Estimated rows per execution.</summary>
    public double EstimateRows { get; init; }
    public double EstimatedExecutions { get; init; } = 1;
    public double EstimateIO { get; init; }
    public double EstimateCPU { get; init; }
    public double AvgRowSize { get; init; }
    public bool IsParallel { get; init; }
    public bool? Ordered { get; init; }
    public string? EstimatedExecutionMode { get; init; }
    public string? ActualExecutionMode { get; init; }

    public string? SeekPredicate { get; init; }
    public string? Predicate { get; init; }
    public string? OutputList { get; init; }

    /// <summary>Summed over threads; null for estimated plans.</summary>
    public long? ActualRows { get; init; }
    public long? ActualExecutions { get; init; }

    /// <summary>Slowest thread's elapsed time; null when the plan doesn't carry it.</summary>
    public long? ActualElapsedMs { get; init; }

    public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];
    public IReadOnlyList<PlanOperator> Children { get; init; } = [];
    /// <summary>Everything about the operator, for the Properties panel.</summary>
    public IReadOnlyList<PlanProperty> Properties { get; internal set; } = [];

    public double EstimatedRowsAllExecutions => EstimateRows * Math.Max(1, EstimatedExecutions);

    public bool HasActuals => ActualRows is not null;

    /// <summary>
    /// Second line of the node label, as SSMS shows it: the logical operation when it
    /// differs ("Inner Join"), otherwise the index kind ("Clustered").
    /// </summary>
    public string? Subtitle =>
        !string.IsNullOrEmpty(LogicalOp) && !string.Equals(LogicalOp, PhysicalOp, StringComparison.OrdinalIgnoreCase)
            ? LogicalOp
            : IndexKind;

    /// <summary>
    /// How far actual rows were from the estimate, as a factor ≥ 1 (10 means ten times
    /// more, or ten times fewer). Null without actuals. Both sides are floored at one row
    /// so that "estimated 0.3, got 0" is not a bad estimate.
    /// </summary>
    public double? EstimateErrorFactor
    {
        get
        {
            if (ActualRows is not { } actual) return null;
            var a = Math.Max(1, actual);
            var e = Math.Max(1, EstimatedRowsAllExecutions);
            return Math.Max(a / e, e / a);
        }
    }

    public bool IsBadEstimate => EstimateErrorFactor >= BadEstimateFactor;

    /// <summary>This operator and every operator below it, depth first.</summary>
    public IEnumerable<PlanOperator> Descendants()
    {
        var stack = new Stack<PlanOperator>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var op = stack.Pop();
            yield return op;
            for (int i = op.Children.Count - 1; i >= 0; i--)
                stack.Push(op.Children[i]);
        }
    }

    /// <summary>Case-insensitive match on operator name, object and predicates, for Find.</summary>
    public bool Matches(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        term = term.Trim();
        return Contains(PhysicalOp) || Contains(LogicalOp) || Contains(ObjectName)
            || Contains(SeekPredicate) || Contains(Predicate);

        bool Contains(string? s) => s?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }
}
