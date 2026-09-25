using System.Windows.Media;
using SqlVitals.Engine.ExecutionPlans;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// Line-art icons for execution plan operators, drawn on a 32×32 grid. Original artwork:
/// each family of operators gets one glyph, and anything unrecognised gets the generic one.
/// </summary>
public static class PlanOperatorIcons
{
    private const string Table = "M3,6 H19 V24 H3 Z M3,12 H19 M3,18 H19 M9,6 V24";

    private static readonly Dictionary<string, Geometry> Cache = new();

    private static readonly (string Key, string Data)[] Glyphs =
    [
        ("Scan",        Table + " M22,15 H30 M26.5,11.5 L30,15 L26.5,18.5"),
        ("Seek",        Table + " M29,21 A4.5,4.5 0 1 1 20,21 A4.5,4.5 0 1 1 29,21 Z M28,24.5 L31,28"),
        ("Lookup",      "M13,16 A5.5,5.5 0 1 1 2,16 A5.5,5.5 0 1 1 13,16 Z M13,16 H30 M25,16 V21 M29,16 V20"),
        ("NestedLoops", "M16,6 A10,10 0 1 1 6,16 M6,18.5 V12 M3,15 L6,12 L9,15 M12,16 H20"),
        ("Hash",        "M13,4 L10,28 M22,4 L19,28 M5,12 H28 M4,20 H27"),
        ("Merge",       "M3,8 H11 L19,16 H30 M3,24 H11 L19,16 M26,12 L30,16 L26,20"),
        ("Sort",        "M3,7 H9 M3,13 H13 M3,19 H17 M3,25 H21 M27,5 V24 M23.5,20.5 L27,24 L30.5,20.5"),
        ("Aggregate",   "M25,6 H8 L17,16 L8,26 H25"),
        ("Compute",     "M7,3 H25 V29 H7 Z M10,6 H22 V12 H10 Z M11,17 H13 M15,17 H17 M19,17 H21 M11,21 H13 M15,21 H17 M19,21 H21 M11,25 H13 M15,25 H17 M19,25 H21"),
        ("Filter",      "M3,6 H29 L19.5,17 V26 L12.5,29 V17 Z"),
        ("Parallelism", "M3,9 H24 M3,16 H24 M3,23 H24 M20.5,5.5 L24,9 L20.5,12.5 M20.5,12.5 L24,16 L20.5,19.5 M20.5,19.5 L24,23 L20.5,26.5 M28,4 V28"),
        ("Spool",       "M6,9 A10,3.5 0 0 0 26,9 A10,3.5 0 0 0 6,9 Z M6,9 V23 A10,3.5 0 0 0 26,23 V9 M6,16 A10,3.5 0 0 0 26,16"),
        ("Top",         "M4,5 H28 M16,28 V10 M10.5,15.5 L16,10 L21.5,15.5"),
        ("Modify",      Table + " M20,29 L21.5,23.5 L28.5,16.5 L31,19 L24,26 Z M27,18 L29.5,20.5"),
        ("Concat",      "M3,7 H15 M3,16 H15 M3,25 H15 M15,7 V25 M15,16 H29 M25,12 L29,16 L25,20"),
        ("Constant",    "M10,5 H5 V27 H10 M22,5 H27 V27 H22 M12.5,12 L16,9 V23 M13,23 H19"),
        ("Sequence",    "M3,7 H29 M3,16 H29 M3,25 H29 M11,4 V28 M21,4 V28"),
        ("Statement",   "M8,3 H20 L26,9 V29 H8 Z M20,3 V9 H26 M11,15 H23 M11,19 H23 M11,23 H19"),
        ("Condition",   "M16,3 L29,16 L16,29 L3,16 Z M16,10 V17 M16,21 V22"),
        ("Generic",     "M5,5 H27 V27 H5 Z M12,16 H20 M16,12 V20"),
    ];

    /// <summary>A frozen geometry for the operator's icon.</summary>
    public static Geometry For(PlanOperator op)
    {
        var key = Category(op);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var geometry)) return geometry;

            var data = Glyphs.First(g => g.Key == key).Data;
            geometry = Geometry.Parse(data);
            geometry.Freeze();
            Cache[key] = geometry;
            return geometry;
        }
    }

    public static string Category(PlanOperator op)
    {
        var name = op.PhysicalOp;
        if (op.IsStatementRoot)
            return name.StartsWith("COND", StringComparison.OrdinalIgnoreCase) ? "Condition" : "Statement";

        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("Lookup")) return "Lookup";
        if (Has("Constant Scan")) return "Constant";
        if (Has("Seek")) return "Seek";
        if (Has("Insert") || Has("Update") || Has("Delete") || name.Equals("Merge", StringComparison.OrdinalIgnoreCase))
            return "Modify";
        if (Has("Scan")) return "Scan";
        if (Has("Nested Loops") || Has("Adaptive Join")) return "NestedLoops";
        if (Has("Hash")) return "Hash";
        if (Has("Merge")) return "Merge";
        if (Has("Sort")) return "Sort";
        if (Has("Aggregate")) return "Aggregate";
        if (Has("Compute Scalar")) return "Compute";
        if (Has("Filter")) return "Filter";
        if (Has("Parallelism")) return "Parallelism";
        if (Has("Spool")) return "Spool";
        if (Has("Top")) return "Top";
        if (Has("Concatenation") || Has("Union")) return "Concat";
        if (Has("Segment") || Has("Sequence")) return "Sequence";
        return "Generic";
    }
}
