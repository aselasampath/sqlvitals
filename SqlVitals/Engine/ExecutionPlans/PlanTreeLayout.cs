namespace SqlVitals.Engine.ExecutionPlans;

public sealed record PlanNodePlacement(PlanOperator Operator, int Column, int Row);

public sealed record PlanLayout(IReadOnlyList<PlanNodePlacement> Nodes, int Columns, int Rows);

/// <summary>
/// Places a statement's operators on a grid the way SSMS draws plans: the root in
/// column 0 on the left, each child one column to the right. The first child shares its
/// parent's row; each further child starts below everything drawn for the one before it,
/// so subtrees never overlap and data flows right to left.
/// </summary>
public static class PlanTreeLayout
{
    public static PlanLayout Compute(PlanOperator root)
    {
        var nodes = new List<PlanNodePlacement>();
        var lastRow = Place(root, 0, 0, nodes);
        var columns = nodes.Max(n => n.Column) + 1;
        return new PlanLayout(nodes, columns, lastRow + 1);
    }

    /// <returns>The lowest row used by <paramref name="op"/>'s subtree.</returns>
    private static int Place(PlanOperator op, int column, int row, List<PlanNodePlacement> nodes)
    {
        nodes.Add(new PlanNodePlacement(op, column, row));

        var lastRow = row;
        for (int i = 0; i < op.Children.Count; i++)
        {
            var childRow = i == 0 ? row : lastRow + 1;
            lastRow = Place(op.Children[i], column + 1, childRow, nodes);
        }
        return lastRow;
    }
}
