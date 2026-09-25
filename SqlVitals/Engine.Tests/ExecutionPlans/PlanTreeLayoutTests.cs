using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Engine.Tests.ExecutionPlans;

public class PlanTreeLayoutTests
{
    private static PlanOperator Op(string name, params PlanOperator[] children) =>
        new() { PhysicalOp = name, LogicalOp = name, Children = children };

    private static (int Column, int Row) Cell(PlanLayout layout, string name)
    {
        var n = layout.Nodes.Single(p => p.Operator.PhysicalOp == name);
        return (n.Column, n.Row);
    }

    [Fact]
    public void Compute_ASingleNodeTakesOneCell()
    {
        var layout = PlanTreeLayout.Compute(Op("SELECT"));

        Assert.Equal((0, 0), Cell(layout, "SELECT"));
        Assert.Equal(1, layout.Columns);
        Assert.Equal(1, layout.Rows);
    }

    [Fact]
    public void Compute_ChildrenGoRightAndLaterSiblingsGoBelowTheSubtreeBeforeThem()
    {
        //  SELECT ─ Join ─ Loop ─ Seek
        //                       └ Lookup
        //               └ Scan
        var root = Op("SELECT",
            Op("Join",
                Op("Loop", Op("Seek"), Op("Lookup")),
                Op("Scan")));

        var layout = PlanTreeLayout.Compute(root);

        Assert.Equal((0, 0), Cell(layout, "SELECT"));
        Assert.Equal((1, 0), Cell(layout, "Join"));
        Assert.Equal((2, 0), Cell(layout, "Loop"));
        Assert.Equal((3, 0), Cell(layout, "Seek"));
        Assert.Equal((3, 1), Cell(layout, "Lookup"));
        Assert.Equal((2, 2), Cell(layout, "Scan"));
        Assert.Equal(4, layout.Columns);
        Assert.Equal(3, layout.Rows);
    }

    [Fact]
    public void Compute_NoTwoNodesShareACell()
    {
        var root = Op("A", Op("B", Op("C"), Op("D", Op("E"), Op("F"))), Op("G", Op("H")), Op("I"));

        var layout = PlanTreeLayout.Compute(root);

        Assert.Equal(9, layout.Nodes.Count);
        Assert.Equal(layout.Nodes.Count, layout.Nodes.Select(n => (n.Column, n.Row)).Distinct().Count());
    }
}
