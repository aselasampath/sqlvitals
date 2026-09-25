using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Shreds the XML returned by an Extended Events <c>ring_buffer</c> target into
/// <see cref="SpTraceEvent"/> rows.
/// <para>
/// Deliberately done in C# rather than with T-SQL <c>.nodes()</c>: shredding on the
/// monitored server would burn CPU there, which is the one thing a monitoring tool
/// must not do. Kept pure and static so it is unit-testable without a database.
/// </para>
/// </summary>
public static partial class SpTraceXmlParser
{
    /// <summary>Parses a ring_buffer <c>target_data</c> document. Never throws on bad input.</summary>
    public static SpTraceTargetSnapshot Parse(string? targetDataXml)
    {
        if (string.IsNullOrWhiteSpace(targetDataXml))
            return SpTraceTargetSnapshot.Empty;

        var root = TryParseRoot(targetDataXml);
        if (root is null)
            return SpTraceTargetSnapshot.Empty;

        var events = new List<SpTraceEvent>();
        foreach (var el in root.Elements("event"))
        {
            var ev = TryParseEvent(el);
            if (ev is not null) events.Add(ev);
        }

        return new SpTraceTargetSnapshot(
            events,
            ReadLongAttribute(root, "totalEventsProcessed"),
            ReadLongAttribute(root, "droppedCount"),
            ReadLongAttribute(root, "truncated") != 0);
    }

    // ── XML recovery ─────────────────────────────────────────────────────
    // sys.dm_xe_session_targets truncates target_data (historically around 2 MB),
    // which can cut the document mid-element. Rather than lose the whole poll, drop
    // everything after the last complete </event> and close the root by hand.
    // Also used for system_health's ring buffer (DeadlockReportParser).
    internal static XElement? TryParseRoot(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root;
        }
        catch (System.Xml.XmlException)
        {
            // fall through to the repair attempt
        }

        const string closingTag = "</event>";
        var lastComplete = xml.LastIndexOf(closingTag, StringComparison.Ordinal);
        if (lastComplete < 0) return null;

        var repaired = string.Concat(
            xml.AsSpan(0, lastComplete + closingTag.Length),
            "</RingBufferTarget>");

        try
        {
            return XDocument.Parse(repaired).Root;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static SpTraceEvent? TryParseEvent(XElement el)
    {
        var eventName = (string?)el.Attribute("name");
        if (string.IsNullOrEmpty(eventName)) return null;

        // Index data/action children once — events carry a dozen or more of each.
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var texts  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in el.Elements())
        {
            if (child.Name.LocalName is not ("data" or "action")) continue;

            var key = (string?)child.Attribute("name");
            if (string.IsNullOrEmpty(key) || fields.ContainsKey(key)) continue;

            // Map-typed fields carry both: <value> the numeric key, <text> the friendly
            // name. object_type wants the friendly form, everything else the scalar.
            var value = child.Element("value")?.Value;
            var text  = child.Element("text")?.Value;

            if (value is not null) fields[key] = value;
            else if (text is not null) fields[key] = text;

            if (text is not null) texts[key] = text;
        }

        var statement = Get(fields, "statement") ?? Get(fields, "batch_text");
        var objectName = Get(fields, "object_name") ?? ExtractProcedureName(statement);

        // module_end does not expose logical_reads / writes at all; missing means zero.
        return new SpTraceEvent(
            EventTime:      ReadTimestamp(el),
            EventName:      eventName,
            ObjectName:     objectName,
            ObjectType:     Get(texts, "object_type") ?? Get(fields, "object_type"),
            DatabaseName:   Get(fields, "database_name"),
            DurationMs:     MicrosecondsToMilliseconds(ReadLong(fields, "duration")),
            CpuTimeMs:      MicrosecondsToMilliseconds(ReadLong(fields, "cpu_time")),
            LogicalReads:   ReadLong(fields, "logical_reads"),
            PhysicalReads:  ReadLong(fields, "physical_reads"),
            Writes:         ReadLong(fields, "writes"),
            RowsReturned:   ReadLong(fields, "row_count"),
            SessionId:      (int)ReadLong(fields, "session_id"),
            LoginName:      Get(fields, "username"),
            ClientAppName:  Get(fields, "client_app_name"),
            ClientHostName: Get(fields, "client_hostname"),
            Statement:      statement,
            ActivityId:     Get(fields, "attach_activity_id"));
    }

    // ── Field helpers ────────────────────────────────────────────────────

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static long ReadLong(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v)
        && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0L;

    private static long ReadLongAttribute(XElement el, string name) =>
        long.TryParse((string?)el.Attribute(name), NumberStyles.Integer,
                      CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0L;

    /// <summary>XE reports durations in microseconds; the UI works in milliseconds.</summary>
    private static long MicrosecondsToMilliseconds(long microseconds) => microseconds / 1000;

    private static DateTime ReadTimestamp(XElement el)
    {
        var raw = (string?)el.Attribute("timestamp");
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                              DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                              out var utc))
            return utc.ToLocalTime();

        return DateTime.Now;
    }

    // ── Procedure-name recovery ──────────────────────────────────────────
    // object_name is absent on older builds and on batch-submitted EXEC calls, so fall
    // back to reading the name out of the statement text.
    internal static string? ExtractProcedureName(string? statement)
    {
        if (string.IsNullOrWhiteSpace(statement)) return null;

        var match = ExecPattern().Match(statement);
        if (!match.Success) return null;

        var parts = match.Groups["name"].Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().Trim('[', ']', '"'))
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0) return null;

        // Keep at most schema.procedure; drop any leading server/database qualifiers.
        return parts.Length == 1
            ? parts[0]
            : $"{parts[^2]}.{parts[^1]}";
    }

    [GeneratedRegex(
        @"\b(?:exec|execute)\s+(?<name>(?:\[[^\]]+\]|""[^""]+""|[\w$#@]+)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|[\w$#@]*))*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecPattern();
}
