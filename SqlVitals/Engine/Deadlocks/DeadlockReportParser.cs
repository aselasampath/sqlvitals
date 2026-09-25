using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Deadlocks;

/// <summary>
/// Reads <c>xml_deadlock_report</c> events from the system_health session into
/// <see cref="DeadlockReport"/>s (#38).
/// <para>
/// Shredded here rather than with T-SQL <c>.nodes()</c>, like <see cref="SpTraceXmlParser"/>:
/// the monitored server only filters by event name. Pure and static, so it is tested without a
/// database, and it never throws on bad input.
/// </para>
/// </summary>
public static partial class DeadlockReportParser
{
    public const string EventName = "xml_deadlock_report";

    /// <summary>
    /// One <c>event_data</c> row from <c>sys.fn_xe_file_target_read_file</c>: an
    /// <c>&lt;event&gt;</c> element. Null when it isn't a readable deadlock report.
    /// </summary>
    public static DeadlockReport? ParseEvent(string? eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml)) return null;

        try
        {
            return FromEvent(XElement.Parse(eventXml));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The deadlock reports in a ring_buffer <c>target_data</c> document. Other events in the
    /// buffer are skipped; reports that can't be read are counted in <paramref name="unreadable"/>.
    /// </summary>
    public static IReadOnlyList<DeadlockReport> ParseRingBuffer(string? targetDataXml, out int unreadable)
    {
        unreadable = 0;
        if (string.IsNullOrWhiteSpace(targetDataXml)) return [];

        // The DMV can cut the document off mid-event; keep the complete ones.
        var root = SpTraceXmlParser.TryParseRoot(targetDataXml);
        if (root is null) return [];

        var reports = new List<DeadlockReport>();
        foreach (var ev in root.Elements("event").Where(e => (string?)e.Attribute("name") == EventName))
        {
            if (FromEvent(ev) is { } report) reports.Add(report);
            else unreadable++;
        }
        return reports;
    }

    /// <summary>
    /// The files to read for system_health's event_file target, from its <c>target_data</c>
    /// (<c>&lt;EventFileTarget&gt;&lt;File name="…\system_health_0_133….xel"/&gt;</c>): the same
    /// folder and name with a wildcard, so every rollover file is read. Falls back to the bare
    /// <c>system_health*.xel</c>, which the server resolves against its log folder.
    /// </summary>
    public static string EventFilePattern(string? eventFileTargetData)
    {
        const string fallback = "system_health*.xel";
        if (string.IsNullOrWhiteSpace(eventFileTargetData)) return fallback;

        string? current;
        try
        {
            current = (string?)XElement.Parse(eventFileTargetData).Element("File")?.Attribute("name");
        }
        catch (XmlException)
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(current)) return fallback;

        // system_health_0_133712345678900000.xel → system_health*.xel, in the same folder.
        var pattern = RolloverSuffix().Replace(current, "*.xel");
        return pattern.Contains('*') ? pattern
             : current.EndsWith(".xel", StringComparison.OrdinalIgnoreCase) ? current[..^4] + "*.xel"
             : fallback;
    }

    // ── Event → report ───────────────────────────────────────────────────

    internal static DeadlockReport? FromEvent(XElement ev)
    {
        if ((string?)ev.Attribute("name") != EventName) return null;

        // Without a time the report can't be placed in the history.
        if (!DateTime.TryParse((string?)ev.Attribute("timestamp"), CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return null;

        // <data name="xml_report"><value><deadlock>…; SQL Server 2008 wrapped it in <deadlock-list>.
        var deadlock = ev.Descendants("deadlock").FirstOrDefault();
        return deadlock is null ? null : FromDeadlock(deadlock, DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    internal static DeadlockReport? FromDeadlock(XElement deadlock, DateTime timestampUtc)
    {
        var processElements = deadlock.Element("process-list")?.Elements("process").ToList() ?? [];
        if (processElements.Count == 0) return null;

        var victims = (deadlock.Element("victim-list")?.Elements("victimProcess") ?? [])
            .Select(v => (string?)v.Attribute("id"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        // The SQL Server 2008 form names the one victim on the element itself.
        if (Attr(deadlock, "victim") is { } victim) victims.Add(victim);

        var sessionIds = processElements
            .Select(p => (Id: Attr(p, "id"), Spid: Int(p, "spid")))
            .Where(p => p.Id is not null)
            .GroupBy(p => p.Id!)
            .ToDictionary(g => g.Key, g => g.First().Spid, StringComparer.Ordinal);

        var resources = (deadlock.Element("resource-list")?.Elements() ?? [])
            .Select(r => ParseResource(r, sessionIds))
            .ToList();

        var processes = processElements
            .Select(p => ParseProcess(p, victims, resources))
            .ToList();

        // An .xdl file is exactly this element; SSMS opens it as a deadlock graph.
        return new DeadlockReport(timestampUtc, processes, resources, deadlock.ToString());
    }

    private static DeadlockProcess ParseProcess(XElement p, HashSet<string> victims, IReadOnlyList<DeadlockResource> resources)
    {
        var id = Attr(p, "id") ?? string.Empty;

        // Frames run innermost first: the first names the statement that was running.
        var frames = p.Element("executionStack")?.Elements("frame").ToList() ?? [];
        var procedure = frames
            .Select(f => Attr(f, "procname"))
            .FirstOrDefault(n => n is not null && !IsPlaceholder(n));
        var statement = frames
            .Select(f => Clean(f.Value))
            .FirstOrDefault(t => t is not null && !IsPlaceholder(t));

        return new DeadlockProcess(
            Id:               id,
            IsVictim:         victims.Contains(id),
            SessionId:        Int(p, "spid"),
            LoginName:        Attr(p, "loginname"),
            HostName:         Attr(p, "hostname"),
            ClientApp:        Attr(p, "clientapp"),
            DatabaseName:     Attr(p, "currentdbname") ?? (Attr(p, "currentdb") is { } dbId ? $"database id {dbId}" : null),
            IsolationLevel:   Attr(p, "isolationlevel"),
            LockMode:         Attr(p, "lockMode"),
            WaitResource:     Attr(p, "waitresource"),
            WaitTimeMs:       Long(p, "waittime") ?? 0,
            TranCount:        Int(p, "trancount") ?? 0,
            TransactionName:  Attr(p, "transactionname"),
            ProcedureName:    procedure,
            Statement:        statement,
            InputBuffer:      Clean(p.Element("inputbuf")?.Value),
            LastBatchStarted: Date(p, "lastbatchstarted"))
        {
            WaitingFor = Locks(resources, id, r => r.Waiters),
            Holding    = Locks(resources, id, r => r.Owners),
        };
    }

    // "X on Sales.dbo.Orders (PK_Orders)" for each resource where the process is in the list.
    private static string Locks(IEnumerable<DeadlockResource> resources, string processId,
                                Func<DeadlockResource, IReadOnlyList<DeadlockLock>> side) =>
        string.Join("; ", resources
            .SelectMany(r => side(r)
                .Where(l => l.ProcessId == processId)
                .Select(l => l.Mode.Length > 0 ? $"{l.Mode} on {r.ObjectText}" : r.ObjectText))
            .Distinct());

    private static DeadlockResource ParseResource(XElement r, IReadOnlyDictionary<string, int?> sessionIds)
    {
        var tag = r.Name.LocalName;

        DeadlockLock Lock(XElement e)
        {
            var id = Attr(e, "id") ?? string.Empty;
            return new DeadlockLock(id, sessionIds.GetValueOrDefault(id), Attr(e, "mode") ?? string.Empty);
        }

        return new DeadlockResource(
            Kind:       KindOf(tag),
            ObjectName: Attr(r, "objectname"),
            IndexName:  Attr(r, "indexname"),
            Detail:     DetailOf(tag, r),
            Owners:     (r.Element("owner-list")?.Elements("owner")   ?? []).Select(Lock).ToList(),
            Waiters:    (r.Element("waiter-list")?.Elements("waiter") ?? []).Select(Lock).ToList());
    }

    private static string KindOf(string tag) => tag switch
    {
        "keylock"         => "Key",
        "pagelock"        => "Page",
        "ridlock"         => "RID",
        "objectlock"      => "Object",
        "hobtlock"        => "HoBT",
        "extentlock"      => "Extent",
        "allocunitlock"   => "Allocation unit",
        "filelock"        => "File",
        "databaselock"    => "Database",
        "metadatalock"    => "Metadata",
        "applicationlock" => "Application",
        "exchangeEvent"   => "Parallel exchange",
        "threadpool"      => "Thread pool",
        "resourceWait"    => "Resource wait",
        _                 => tag,
    };

    private static string DetailOf(string tag, XElement r)
    {
        string? A(string name) => Attr(r, name);

        return tag switch
        {
            "keylock" or "hobtlock"   => A("hobtid") is { } hobt ? $"hobt {hobt}" : string.Empty,
            "pagelock" or "ridlock"   => A("pageid") is { } page ? $"page {A("fileid")}:{page}" : string.Empty,
            "objectlock"              => A("objectname") is null && A("objid") is { } objId ? $"object id {objId}" : string.Empty,
            "metadatalock"            => string.Join(" ", new[] { A("subresource"), A("classid") }.OfType<string>()),
            "applicationlock"         => A("resource") ?? string.Empty,
            "exchangeEvent"           => string.Join(" ", new[] { A("WaitType"), A("nodeId") is { } node ? $"node {node}" : null }.OfType<string>()),
            _                         => string.Empty,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    // "adhoc" and "unknown" stand in for a missing procedure name or statement text.
    private static bool IsPlaceholder(string value) =>
        value.Equals("adhoc", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Attr(XElement el, string name) => Clean((string?)el.Attribute(name));

    private static int? Int(XElement el, string name) =>
        int.TryParse(Attr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? Long(XElement el, string name) =>
        long.TryParse(Attr(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    // Process times are the server's local time, with no zone: kept as they are.
    private static DateTime? Date(XElement el, string name) =>
        DateTime.TryParse(Attr(el, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    [GeneratedRegex(@"_\d+_\d+\.xel$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RolloverSuffix();
}
