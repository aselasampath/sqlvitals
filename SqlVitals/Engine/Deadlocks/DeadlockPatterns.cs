using System.Text.RegularExpressions;

namespace SqlVitals.Engine.Deadlocks;

/// <summary>
/// Deadlocks that look alike: the same objects fought over by the same code. The one to fix
/// first is usually the one that keeps coming back.
/// </summary>
/// <param name="Objects">The objects fought over, as in <see cref="DeadlockReport.ObjectsText"/>.</param>
/// <param name="Code">The procedures (or statements) that took part, each once.</param>
/// <param name="Deadlocks">Every deadlock with this pattern, newest first.</param>
public sealed record DeadlockPattern(string Objects, string Code, IReadOnlyList<DeadlockReport> Deadlocks)
{
    public int      Count     => Deadlocks.Count;
    public DateTime LastSeen  => Deadlocks[0].Time;
    public DateTime FirstSeen => Deadlocks[^1].Time;
    public DeadlockReport Newest => Deadlocks[0];
}

public static partial class DeadlockPatterns
{
    /// <summary>
    /// Groups deadlocks by pattern, the most frequent first (then the most recent). Each report
    /// comes back with <see cref="DeadlockReport.TimesSeen"/> set to its pattern's count.
    /// </summary>
    public static (IReadOnlyList<DeadlockPattern> Patterns, IReadOnlyList<DeadlockReport> Reports) Group(
        IEnumerable<DeadlockReport> reports)
    {
        var counted = reports
            .GroupBy(r => (Objects: Key(Objects(r)), Code: Key(Code(r))))
            .Select(g =>
            {
                var newestFirst = g.OrderByDescending(r => r.TimestampUtc).ToList();
                return new DeadlockPattern(
                    string.Join("; ", Objects(newestFirst[0])),
                    CodeText(newestFirst[0]),
                    newestFirst.Select(r => r with { TimesSeen = newestFirst.Count }).ToList());
            })
            .OrderByDescending(p => p.Count)
            .ThenByDescending(p => p.Newest.TimestampUtc)
            .ToList();

        var all = counted
            .SelectMany(p => p.Deadlocks)
            .OrderByDescending(r => r.TimestampUtc)
            .ToList();

        return (counted, all);
    }

    // The objects, without the index (a deadlock can land on another index of the same table)
    // and in a set order. A resource with no object, like a parallel exchange, counts by kind.
    private static IEnumerable<string> Objects(DeadlockReport r) => r.Resources
        .Select(res => res.ObjectName ?? res.Kind)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Code(DeadlockReport r) => r.Processes
        .Select(p => p.ProcedureName ?? Normalize(p.Statement ?? p.InputBuffer));

    // Order and case don't matter; the separator can't appear in a name or statement.
    private static string Key(IEnumerable<string> parts) => string.Join('\u001F', parts
        .Select(s => s.ToUpperInvariant())
        .Distinct()
        .Order(StringComparer.Ordinal));

    private static string CodeText(DeadlockReport r) => string.Join("; ", r.Processes
        .Select(p => p.CodeText)
        .Where(c => c.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Statement text with the literals and spacing taken out, so the same ad hoc statement
    /// with other values counts as the same code.
    /// </summary>
    internal static string Normalize(string? statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) return string.Empty;

        var text = StringLiteral().Replace(statement, "?");
        text = NumberLiteral().Replace(text, "?");
        return Whitespace().Replace(text, " ").Trim().ToUpperInvariant();
    }

    [GeneratedRegex(@"N?'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"(?<![\w@#$])(?:0x[0-9A-Fa-f]+|\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLiteral();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
