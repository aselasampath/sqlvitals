using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SqlVitals.Engine.ExecutionPlans;

/// <summary>
/// Reads showplan XML (estimated or actual, from the plan cache, Query Store or a
/// .sqlplan file) into an <see cref="ExecutionPlan"/> for the graphical plan viewer.
/// <para>
/// Elements are matched by local name, so every showplan schema version reads the same
/// way and unknown operators are kept with whatever attributes they have. Never throws:
/// bad input comes back as an <see cref="ExecutionPlan"/> with an <see cref="ExecutionPlan.Error"/>.
/// </para>
/// </summary>
public static class ShowplanParser
{
    // RelOp children that describe the operator. Its other child is the operation element
    // (NestedLoops, IndexScan, Hash…) that holds the operator's details and child RelOps.
    private static readonly HashSet<string> RelOpInfoElements = new(StringComparer.Ordinal)
    {
        "OutputList", "Warnings", "MemoryFractions", "RunTimeInformation",
        "RunTimePartitionSummary", "InternalInfo",
    };

    // RelOps below these belong to an expression or a function body, not to the plan tree.
    private static readonly HashSet<string> NotPlanTreeElements = new(StringComparer.Ordinal)
    {
        "ScalarOperator", "UDF", "Statements",
    };

    private static readonly HashSet<string> OperatorDumpSkips = new(StringComparer.Ordinal) { "RelOp" };

    private static readonly HashSet<string> StatementDumpSkips = new(StringComparer.Ordinal)
    {
        "RelOp", "Then", "Else", "Statements",
    };

    public static ExecutionPlan Parse(string? showplanXml)
    {
        if (string.IsNullOrWhiteSpace(showplanXml))
            return ExecutionPlan.Empty;

        XElement? root;
        try
        {
            root = XDocument.Parse(showplanXml).Root;
        }
        catch (XmlException ex)
        {
            return ExecutionPlan.Failed($"The plan XML could not be read: {ex.Message}");
        }

        if (root is null)
            return ExecutionPlan.Empty;

        try
        {
            return ParseDocument(root);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ExecutionPlan.Failed($"The plan could not be drawn: {ex.Message}");
        }
    }

    private static ExecutionPlan ParseDocument(XElement root)
    {
        bool isActual = root.DescendantsAndSelf()
            .Any(e => Is(e, "RunTimeInformation") || Is(e, "QueryTimeStats"));

        // A .sqlplan holds ShowPlanXML/BatchSequence/Batch/Statements. Anything else
        // (a bare Statements fragment, say) is read as one batch.
        var batches = root.DescendantsAndSelf().Where(e => Is(e, "Batch")).ToList();
        if (batches.Count == 0) batches.Add(root);

        var statements = new List<PlanStatement>();
        int number = 0;
        foreach (var batch in batches)
        {
            var stmtElements = new List<XElement>();
            foreach (var list in batch.DescendantsAndSelf().Where(e => Is(e, "Statements")).Take(1))
                CollectStatements(list, stmtElements);

            var costs = stmtElements.Select(s => ReadDouble(s, "StatementSubTreeCost")).ToList();
            var batchCost = costs.Sum();
            for (int i = 0; i < stmtElements.Count; i++)
            {
                var pct = batchCost > 0 ? costs[i] / batchCost * 100 : 0;
                statements.Add(ParseStatement(stmtElements[i], ++number, costs[i], pct));
            }
        }

        return new ExecutionPlan(statements, isActual, null);
    }

    private static void CollectStatements(XElement statementsElement, List<XElement> into)
    {
        foreach (var stmt in statementsElement.Elements())
        {
            if (!stmt.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal)) continue;
            into.Add(stmt);

            // IF … ELSE: the branches' statements follow the condition, as in SSMS.
            foreach (var branch in stmt.Elements().Where(e => Is(e, "Then") || Is(e, "Else")))
                foreach (var inner in branch.Elements().Where(e => Is(e, "Statements")))
                    CollectStatements(inner, into);
        }
    }

    // ── Statements ───────────────────────────────────────────────────────

    private static PlanStatement ParseStatement(XElement stmt, int number, double cost, double pctOfBatch)
    {
        var type = Attr(stmt, "StatementType") ?? stmt.Name.LocalName;
        var text = Attr(stmt, "StatementText")?.Trim();
        var queryPlan = FindQueryPlan(stmt);
        var topRelOp = queryPlan?.Elements().FirstOrDefault(e => Is(e, "RelOp"));

        PlanOperator? top = null;
        if (topRelOp is not null)
        {
            // Operator percentages are of the tree's own cost, so they add up to 100.
            var treeCost = ReadDouble(topRelOp, "EstimatedTotalSubtreeCost");
            top = ParseRelOp(topRelOp, treeCost > 0 ? treeCost : cost);
        }

        var warnings = ParseWarnings(Child(queryPlan, "Warnings"));
        var estRows = ReadDouble(stmt, "StatementEstRows");

        var props = new List<PlanProperty>
        {
            PlanProperty.Leaf("Statement Type", type),
            PlanProperty.Leaf("Estimated Subtree Cost", FormatCost(cost)),
            PlanProperty.Leaf("Estimated Number of Rows", FormatRows(estRows)),
        };
        if (Attr(queryPlan, "DegreeOfParallelism") is { } dop)
            props.Add(PlanProperty.Leaf("Degree of Parallelism", dop));
        if (warnings.Count > 0)
            props.Add(WarningsProperty(warnings));
        if (text is not null)
            props.Add(PlanProperty.Leaf("Statement", text));
        props.Add(Dump(stmt, StatementDumpSkips));

        var root = new PlanOperator
        {
            IsStatementRoot = true,
            PhysicalOp = type,
            LogicalOp = type,
            EstimatedSubtreeCost = cost,
            EstimateRows = estRows,
            Warnings = warnings,
            Children = top is null ? [] : [top],
            Properties = props,
        };

        return new PlanStatement(number, type, text, cost, pctOfBatch, root, ParseMissingIndexes(queryPlan));
    }

    private static XElement? FindQueryPlan(XElement stmt)
    {
        var direct = stmt.Elements().FirstOrDefault(e => Is(e, "QueryPlan"));
        if (direct is not null) return direct;

        // COND WITH QUERY keeps its plan under Condition, cursors under CursorPlan/Operation.
        foreach (var child in stmt.Elements())
        {
            if (Is(child, "Then") || Is(child, "Else") || Is(child, "Statements")) continue;
            var nested = child.Descendants().FirstOrDefault(e => Is(e, "QueryPlan"));
            if (nested is not null) return nested;
        }
        return null;
    }

    // ── Operators ────────────────────────────────────────────────────────

    private static PlanOperator ParseRelOp(XElement relOp, double statementCost)
    {
        var opElement = relOp.Elements().FirstOrDefault(e => !RelOpInfoElements.Contains(e.Name.LocalName));
        var children = ChildRelOps(opElement).Select(c => ParseRelOp(c, statementCost)).ToList();

        var subtreeCost = ReadDouble(relOp, "EstimatedTotalSubtreeCost");
        var operatorCost = Math.Max(0, subtreeCost - children.Sum(c => c.EstimatedSubtreeCost));
        var costPercent = statementCost > 0 ? operatorCost / statementCost * 100 : 0;

        var physical = Attr(relOp, "PhysicalOp") ?? opElement?.Name.LocalName ?? "Unknown";
        var logical = Attr(relOp, "LogicalOp") ?? physical;
        if (IsTrue(Attr(opElement, "Lookup")) && !physical.Contains("Lookup", StringComparison.OrdinalIgnoreCase))
            physical = logical = "Key Lookup";

        var objectElement = opElement?.Elements().FirstOrDefault(e => Is(e, "Object"));
        var executions = 1 + ReadDouble(relOp, "EstimateRebinds") + ReadDouble(relOp, "EstimateRewinds");

        var counters = Child(relOp, "RunTimeInformation")?.Elements()
            .Where(e => Is(e, "RunTimeCountersPerThread")).ToList() ?? [];
        long? actualRows = counters.Count > 0 ? counters.Sum(c => ReadLong(c, "ActualRows")) : null;
        long? actualExecutions = counters.Count > 0 ? counters.Sum(c => ReadLong(c, "ActualExecutions")) : null;
        long? actualElapsed = counters.Any(c => Attr(c, "ActualElapsedms") is not null)
            ? counters.Max(c => ReadLong(c, "ActualElapsedms"))
            : null;
        var actualMode = counters.Select(c => Attr(c, "ActualExecutionMode")).FirstOrDefault(m => m is not null);

        var op = new PlanOperator
        {
            NodeId = (int)ReadLong(relOp, "NodeId"),
            PhysicalOp = physical,
            LogicalOp = logical,
            IndexKind = Attr(objectElement, "IndexKind"),
            ObjectName = FormatObject(objectElement),
            EstimatedOperatorCost = operatorCost,
            EstimatedSubtreeCost = subtreeCost,
            CostPercent = costPercent,
            EstimateRows = ReadDouble(relOp, "EstimateRows"),
            EstimatedExecutions = executions,
            EstimateIO = ReadDouble(relOp, "EstimateIO"),
            EstimateCPU = ReadDouble(relOp, "EstimateCPU"),
            AvgRowSize = ReadDouble(relOp, "AvgRowSize"),
            IsParallel = IsTrue(Attr(relOp, "Parallel")),
            Ordered = Attr(opElement, "Ordered") is { } ordered ? IsTrue(ordered) : null,
            EstimatedExecutionMode = Attr(relOp, "EstimatedExecutionMode"),
            ActualExecutionMode = actualMode,
            SeekPredicate = FormatSeekPredicates(opElement),
            Predicate = ScalarText(Child(opElement, "Predicate")),
            OutputList = FormatColumnList(Child(relOp, "OutputList")),
            ActualRows = actualRows,
            ActualExecutions = actualExecutions,
            ActualElapsedMs = actualElapsed,
            Warnings = ParseWarnings(Child(relOp, "Warnings")),
            Children = children,
        };

        op.Properties = OperatorProperties(op, relOp);
        return op;
    }

    private static List<XElement> ChildRelOps(XElement? opElement)
    {
        var result = new List<XElement>();
        if (opElement is null) return result;

        var stack = new Stack<XElement>(opElement.Elements().Reverse());
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (Is(e, "RelOp")) { result.Add(e); continue; }
            if (NotPlanTreeElements.Contains(e.Name.LocalName)) continue;
            foreach (var c in e.Elements().Reverse()) stack.Push(c);
        }
        return result;
    }

    private static List<PlanProperty> OperatorProperties(PlanOperator op, XElement relOp)
    {
        var props = new List<PlanProperty>
        {
            PlanProperty.Leaf("Physical Operation", op.PhysicalOp),
            PlanProperty.Leaf("Logical Operation", op.LogicalOp),
        };

        if (op.HasActuals)
        {
            props.Add(PlanProperty.Leaf("Actual Number of Rows", FormatRows(op.ActualRows!.Value)));
            props.Add(PlanProperty.Leaf("Actual Number of Executions", FormatRows(op.ActualExecutions ?? 0)));
            if (op.ActualElapsedMs is { } ms)
                props.Add(PlanProperty.Leaf("Actual Elapsed Time (ms)", ms.ToString("N0", CultureInfo.InvariantCulture)));
            if (op.ActualExecutionMode is { } mode)
                props.Add(PlanProperty.Leaf("Actual Execution Mode", mode));
        }
        if (op.EstimatedExecutionMode is { } estMode)
            props.Add(PlanProperty.Leaf("Estimated Execution Mode", estMode));

        props.Add(PlanProperty.Leaf("Estimated Operator Cost",
            $"{FormatCost(op.EstimatedOperatorCost)} ({op.CostPercent.ToString("0", CultureInfo.InvariantCulture)}%)"));
        props.Add(PlanProperty.Leaf("Estimated I/O Cost", FormatCost(op.EstimateIO)));
        props.Add(PlanProperty.Leaf("Estimated CPU Cost", FormatCost(op.EstimateCPU)));
        props.Add(PlanProperty.Leaf("Estimated Subtree Cost", FormatCost(op.EstimatedSubtreeCost)));
        props.Add(PlanProperty.Leaf("Estimated Number of Executions", FormatRows(op.EstimatedExecutions)));
        props.Add(PlanProperty.Leaf("Estimated Number of Rows Per Execution", FormatRows(op.EstimateRows)));
        props.Add(PlanProperty.Leaf("Estimated Number of Rows for All Executions", FormatRows(op.EstimatedRowsAllExecutions)));
        props.Add(PlanProperty.Leaf("Estimated Row Size", $"{FormatRows(op.AvgRowSize)} B"));
        props.Add(PlanProperty.Leaf("Node ID", op.NodeId.ToString(CultureInfo.InvariantCulture)));
        props.Add(PlanProperty.Leaf("Parallel", op.IsParallel ? "True" : "False"));
        if (op.Ordered is { } ordered)
            props.Add(PlanProperty.Leaf("Ordered", ordered ? "True" : "False"));
        if (op.ObjectName is not null)
            props.Add(PlanProperty.Leaf("Object", op.ObjectName));
        if (op.SeekPredicate is not null)
            props.Add(PlanProperty.Leaf("Seek Predicates", op.SeekPredicate));
        if (op.Predicate is not null)
            props.Add(PlanProperty.Leaf("Predicate", op.Predicate));
        if (op.OutputList is not null)
            props.Add(PlanProperty.Leaf("Output List", op.OutputList));
        if (op.Warnings.Count > 0)
            props.Add(WarningsProperty(op.Warnings));

        // Everything else, as it appears in the XML.
        props.Add(new PlanProperty("RelOp Attributes", null,
            relOp.Attributes().Where(a => !a.IsNamespaceDeclaration)
                 .Select(a => PlanProperty.Leaf(Humanize(a.Name.LocalName), a.Value)).ToList()));
        foreach (var child in relOp.Elements())
        {
            if (Is(child, "OutputList") || Is(child, "Warnings")) continue;   // shown above
            props.Add(Dump(child, OperatorDumpSkips));
        }
        return props;
    }

    // ── Warnings ─────────────────────────────────────────────────────────

    internal static List<PlanWarning> ParseWarnings(XElement? warnings)
    {
        var result = new List<PlanWarning>();
        if (warnings is null) return result;

        if (IsTrue(Attr(warnings, "NoJoinPredicate")))
            result.Add(new(PlanWarningKind.NoJoinPredicate, "No join predicate"));
        if (IsTrue(Attr(warnings, "SpatialGuess")))
            result.Add(new(PlanWarningKind.Other, "Spatial guess: the optimizer guessed the cardinality of a spatial filter"));
        if (IsTrue(Attr(warnings, "FullUpdateForOnlineIndexBuild")))
            result.Add(new(PlanWarningKind.Other, "Full update for online index build"));
        if (IsTrue(Attr(warnings, "UnmatchedIndexes")) && Child(warnings, "UnmatchedIndexes") is null)
            result.Add(new(PlanWarningKind.UnmatchedIndexes,
                "Unmatched indexes: a filtered index could not be used because of parameterization"));

        foreach (var w in warnings.Elements())
        {
            switch (w.Name.LocalName)
            {
                case "SpillToTempDb":
                    result.Add(new(PlanWarningKind.SpillToTempDb, Join(
                        "Operator used tempdb to spill data during execution",
                        Attr(w, "SpillLevel") is { } level ? $"with spill level {level}" : null,
                        Attr(w, "SpilledThreadCount") is { } threads ? $"and {threads} spilled thread(s)" : null)));
                    break;

                case "SortSpillDetails":
                case "HashSpillDetails":
                case "ExchangeSpillDetails":
                    result.Add(new(PlanWarningKind.SpillToTempDb, SpillDetails(w)));
                    break;

                case "ColumnsWithNoStatistics":
                    result.Add(new(PlanWarningKind.NoStatistics,
                        "Columns with no statistics: " + FormatColumnList(w)));
                    break;

                case "PlanAffectingConvert":
                    result.Add(new(PlanWarningKind.ImplicitConversion,
                        $"Type conversion in expression ({Attr(w, "Expression")}) may affect " +
                        $"\"{Attr(w, "ConvertIssue")}\" in query plan choice"));
                    break;

                case "MemoryGrantWarning":
                    result.Add(new(PlanWarningKind.MemoryGrant, Join(
                        Attr(w, "GrantWarningKind") ?? "Memory grant warning",
                        Attr(w, "RequestedMemory") is { } req ? $"— requested {req} KB," : null,
                        Attr(w, "GrantedMemory") is { } granted ? $"granted {granted} KB," : null,
                        Attr(w, "MaxUsedMemory") is { } used ? $"used {used} KB" : null).TrimEnd(',')));
                    break;

                case "UnmatchedIndexes":
                    var indexes = w.Descendants().Where(e => Is(e, "Object"))
                        .Select(FormatObject).Where(n => n is not null);
                    result.Add(new(PlanWarningKind.UnmatchedIndexes,
                        "Unmatched indexes: " + string.Join(", ", indexes)));
                    break;

                case "Wait":
                    result.Add(new(PlanWarningKind.Other,
                        $"Wait {Attr(w, "WaitType")}: {Attr(w, "WaitTime")} ms"));
                    break;

                default:
                    var details = string.Join(", ", w.Attributes()
                        .Select(a => $"{Humanize(a.Name.LocalName)} {a.Value}"));
                    result.Add(new(PlanWarningKind.Other,
                        details.Length > 0 ? $"{Humanize(w.Name.LocalName)}: {details}" : Humanize(w.Name.LocalName)));
                    break;
            }
        }
        return result;
    }

    private static string SpillDetails(XElement w)
    {
        var kind = w.Name.LocalName.Replace("SpillDetails", "", StringComparison.Ordinal);
        var parts = new List<string>();
        if (Attr(w, "WritesToTempDb") is { } writes) parts.Add($"{writes} pages written to tempdb");
        if (Attr(w, "ReadsFromTempDb") is { } reads) parts.Add($"{reads} pages read from tempdb");
        if (Attr(w, "GrantedMemoryKb") is { } granted) parts.Add($"granted memory {granted} KB");
        if (Attr(w, "UsedMemoryKb") is { } used) parts.Add($"used memory {used} KB");
        return parts.Count > 0 ? $"{kind} spill: {string.Join(", ", parts)}" : $"{kind} spill";
    }

    private static PlanProperty WarningsProperty(IReadOnlyList<PlanWarning> warnings) =>
        new("Warnings", warnings.Count.ToString(CultureInfo.InvariantCulture),
            warnings.Select(w => PlanProperty.Leaf(Humanize(w.Kind.ToString()), w.Message)).ToList());

    // ── Missing indexes ──────────────────────────────────────────────────

    private static List<PlanMissingIndex> ParseMissingIndexes(XElement? queryPlan)
    {
        var result = new List<PlanMissingIndex>();
        var groups = Child(queryPlan, "MissingIndexes")?.Elements().Where(e => Is(e, "MissingIndexGroup"));
        if (groups is null) return result;

        foreach (var group in groups)
        {
            var impact = ReadDouble(group, "Impact");
            foreach (var mi in group.Elements().Where(e => Is(e, "MissingIndex")))
            {
                var tableName = Attr(mi, "Table") ?? "";
                var table = string.Join(".", new[] { Attr(mi, "Schema"), tableName }
                    .Where(p => !string.IsNullOrEmpty(p)).Select(p => QuoteName(p!)));

                List<string> Columns(string usage) => mi.Elements()
                    .Where(g => Is(g, "ColumnGroup") && string.Equals(Attr(g, "Usage"), usage, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(g => g.Elements().Where(c => Is(c, "Column")))
                    .Select(c => QuoteName(Attr(c, "Name") ?? ""))
                    .ToList();

                var equality = Columns("EQUALITY");
                var inequality = Columns("INEQUALITY");
                var include = Columns("INCLUDE");

                result.Add(new PlanMissingIndex(impact, table, equality, inequality, include,
                    CreateIndexStatement(table, tableName, equality.Concat(inequality).ToList(), include)));
            }
        }
        return result;
    }

    private static string CreateIndexStatement(string table, string tableName, List<string> keys, List<string> include)
    {
        if (keys.Count == 0)
            return $"-- The optimizer suggested an index on {table} without key columns.";

        var name = string.Join("_", new[] { "IX", UnquoteName(tableName) }
            .Concat(keys.Select(UnquoteName))
            .Select(SanitizeNamePart)
            .Where(p => p.Length > 0));
        if (name.Length > 128) name = name[..128];

        var sql = $"CREATE NONCLUSTERED INDEX {QuoteName(name)} ON {table} ({string.Join(", ", keys)})";
        return include.Count > 0 ? $"{sql} INCLUDE ({string.Join(", ", include)})" : sql;
    }

    private static string SanitizeNamePart(string part)
    {
        var sb = new StringBuilder(part.Length);
        foreach (var c in part)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString().Trim('_');
    }

    private static string QuoteName(string name) =>
        name.StartsWith('[') && name.EndsWith(']') ? name : "[" + name.Replace("]", "]]") + "]";

    private static string UnquoteName(string name) =>
        name.StartsWith('[') && name.EndsWith(']') ? name[1..^1].Replace("]]", "]") : name;

    // ── Text formatting ──────────────────────────────────────────────────

    /// <summary><c>[dbo].[Orders].[IX_Orders_CustomerId] [o]</c>, from an <c>Object</c> element.</summary>
    private static string? FormatObject(XElement? obj)
    {
        if (obj is null) return null;
        var name = string.Join(".", new[] { Attr(obj, "Schema"), Attr(obj, "Table"), Attr(obj, "Index") }
            .Where(p => !string.IsNullOrEmpty(p)));
        if (name.Length == 0) return null;
        return Attr(obj, "Alias") is { Length: > 0 } alias ? $"{name} {alias}" : name;
    }

    private static string FormatColumn(XElement columnReference)
    {
        var column = Attr(columnReference, "Column") ?? "";
        if (Attr(columnReference, "Alias") is { Length: > 0 } alias)
            return $"{alias}.{column}";

        var prefix = string.Join(".", new[] { Attr(columnReference, "Schema"), Attr(columnReference, "Table") }
            .Where(p => !string.IsNullOrEmpty(p)));
        return prefix.Length > 0 ? $"{prefix}.{column}" : column;
    }

    private static string? FormatColumnList(XElement? list)
    {
        if (list is null) return null;
        var columns = list.Elements().Where(e => Is(e, "ColumnReference")).Select(FormatColumn).ToList();
        return columns.Count > 0 ? string.Join(", ", columns) : null;
    }

    /// <summary>The <c>ScalarString</c> of the first scalar expression inside an element.</summary>
    private static string? ScalarText(XElement? container) =>
        container?.DescendantsAndSelf().Select(e => Attr(e, "ScalarString")).FirstOrDefault(s => s is not null);

    /// <summary>
    /// SSMS-style seek text, for example <c>Prefix: [dbo].[Orders].CustomerId = Scalar Operator([@CustomerId])</c>.
    /// </summary>
    private static string? FormatSeekPredicates(XElement? opElement)
    {
        var seek = Child(opElement, "SeekPredicates");
        if (seek is null) return null;

        var ranges = seek.Descendants()
            .Where(e => Is(e, "Prefix") || Is(e, "StartRange") || Is(e, "EndRange"))
            .Select(range =>
            {
                var columns = Child(range, "RangeColumns")?.Elements()
                    .Where(e => Is(e, "ColumnReference")).Select(FormatColumn) ?? [];
                var values = Child(range, "RangeExpressions")?.Elements()
                    .Select(e => Attr(e, "ScalarString") is { } s ? $"Scalar Operator({s})"
                               : Is(e, "ColumnReference") ? FormatColumn(e) : e.Value) ?? [];
                var label = range.Name.LocalName switch
                {
                    "StartRange" => "Start",
                    "EndRange" => "End",
                    _ => "Prefix",
                };
                return $"{label}: {string.Join(", ", columns)} {ScanOperator(Attr(range, "ScanType"))} {string.Join(", ", values)}";
            })
            .ToList();

        return ranges.Count > 0 ? string.Join("; ", ranges) : null;
    }

    private static string ScanOperator(string? scanType) => scanType switch
    {
        "EQ" => "=",
        "GT" => ">",
        "GE" => ">=",
        "LT" => "<",
        "LE" => "<=",
        "NE" => "<>",
        "IS" => "IS",
        "IsNull" => "IS NULL",
        "IsNotNull" => "IS NOT NULL",
        "ISNOT" => "IS NOT",
        _ => scanType ?? "",
    };

    /// <summary>"EstimatedTotalSubtreeCost" → "Estimated Total Subtree Cost".</summary>
    public static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string FormatCost(double cost) => cost.ToString("0.#######", CultureInfo.InvariantCulture);

    internal static string FormatRows(double rows) => rows.ToString("#,0.###", CultureInfo.InvariantCulture);

    private static string Join(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>
    /// An element as a property tree: attributes become values, child elements nodes.
    /// Expressions collapse to their text and column lists to one line.
    /// </summary>
    private static PlanProperty Dump(XElement e, HashSet<string> skip)
    {
        var name = Humanize(e.Name.LocalName);
        if (Attr(e, "ScalarString") is { } scalar) return PlanProperty.Leaf(name, scalar);
        if (Is(e, "ColumnReference")) return PlanProperty.Leaf(name, FormatColumn(e));

        var children = new List<PlanProperty>();
        foreach (var a in e.Attributes())
            if (!a.IsNamespaceDeclaration)
                children.Add(PlanProperty.Leaf(Humanize(a.Name.LocalName), a.Value));
        foreach (var c in e.Elements())
            if (!skip.Contains(c.Name.LocalName))
                children.Add(Dump(c, skip));

        if (children.Count > 0 && children.All(c => c.Name == "Column Reference"))
            return PlanProperty.Leaf(name, string.Join(", ", children.Select(c => c.Value)));

        var text = e.HasElements ? null : e.Value.Trim();
        return new PlanProperty(name, string.IsNullOrEmpty(text) ? null : text, children);
    }

    // ── XML helpers ──────────────────────────────────────────────────────

    private static bool Is(XElement e, string localName) =>
        string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal);

    private static XElement? Child(XElement? e, string localName) =>
        e?.Elements().FirstOrDefault(c => Is(c, localName));

    private static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;

    private static bool IsTrue(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static double ReadDouble(XElement? e, string name) =>
        double.TryParse(Attr(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && double.IsFinite(v) ? v : 0;

    private static long ReadLong(XElement? e, string name)
    {
        var raw = Attr(e, name);
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? (long)d : 0;
    }
}
