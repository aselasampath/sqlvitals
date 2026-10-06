using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Verdicts;

/// <summary>An alert as the Alerts page lists it: with its connection's name.</summary>
public sealed record NamedAlert(Alert Alert, string Connection);

/// <summary>
/// Alerts' verdict (#150): the active alerts, the critical and longest first, or that none are,
/// and whether alerts can be raised at all.
/// </summary>
public static class AlertsVerdict
{
    /// <summary>Opens the evidence of the alert the verdict names (#145).</summary>
    public const string Investigate = "investigate";

    /// <summary>Lists the active alerts only.</summary>
    public const string ActiveOnly = "active-only";

    /// <summary>The alert the verdict names: critical first, then the longest running.</summary>
    public static NamedAlert? Worst(IEnumerable<NamedAlert> alerts) => alerts
        .Where(a => a.Alert.IsActive)
        .OrderByDescending(a => a.Alert.Severity == HealthLevel.Critical)
        .ThenBy(a => a.Alert.StartedUtc)
        .FirstOrDefault();

    /// <param name="alerts">The alerts listed, active and past, for the connection picked.</param>
    /// <param name="inPeriod">"in the last 7 days", "in the history kept".</param>
    /// <param name="monitored">How many connections are monitored, so can raise alerts.</param>
    /// <param name="notMonitored">The names of the listed connections that aren't.</param>
    /// <param name="activeOnly">Only the active alerts are listed already.</param>
    public static PageVerdict From(IReadOnlyList<NamedAlert> alerts, string inPeriod, DateTime nowUtc, int monitored, IReadOnlyList<string> notMonitored,
                                   bool activeOnly = false)
    {
        var culture = CultureInfo.CurrentCulture;
        var off = monitored == 0 ? "No alerts are being raised: monitoring is off."
            : notMonitored.Count > 0 ? $"Monitoring is off for {string.Join(", ", notMonitored)}, so no alerts are raised for {(notMonitored.Count == 1 ? "it" : "them")}."
            : "";
        var past = alerts.Count(a => !a.Alert.IsActive);
        var pastText = past == 0 ? $"No past alerts {inPeriod}."
            : string.Format(culture, "{0} {1}.", Sizes.Plural(past, "past alert", "past alerts"), inPeriod);

        if (Worst(alerts) is not { } worst)
        {
            if (monitored == 0)
                return new PageVerdict(VerdictLevel.Unknown, "No alerts are being raised: monitoring is off",
                    "Start it on a server's Overview, from its tile on All servers or its header in the sidebar, or turn on Start monitoring automatically in Settings. " + pastText);
            var last = alerts.Where(a => a.Alert.EndedUtc is not null).MaxBy(a => a.Alert.EndedUtc);
            return new PageVerdict(VerdictLevel.Ok, "No active alerts",
                (pastText + (last is null ? ""
                    : string.Format(culture, " The last ended {0} ago: {1} on {2}.", AlertText.Duration(nowUtc - last.Alert.EndedUtc!.Value),
                                    HealthThresholds.Info(last.Alert.Indicator).Name, last.Connection))
                 + " " + off).Trim());
        }

        var active   = alerts.Where(a => a.Alert.IsActive).ToList();
        var critical = active.Count(a => a.Alert.Severity == HealthLevel.Critical);
        var headline = string.Format(culture, "{0}{1}; {2} on {3}, {4} for {5}",
                                     Sizes.Plural(active.Count, "active alert", "active alerts"),
                                     critical > 0 && active.Count > 1 ? string.Format(culture, ", {0:N0} critical", critical) : "",
                                     HealthThresholds.Info(worst.Alert.Indicator).Name, worst.Connection,
                                     AlertText.Severity(worst.Alert.Severity).ToLower(culture), AlertText.Duration(worst.Alert.Duration(nowUtc)));
        var why = string.Format(culture, "Worst {0} (threshold {1}). {2}", AlertText.Value(worst.Alert.Indicator, worst.Alert.Value),
                                AlertText.Threshold(worst.Alert.Indicator, worst.Alert.Threshold),
                                string.IsNullOrWhiteSpace(worst.Alert.Detail) ? "" : worst.Alert.Detail.Trim().TrimEnd('.') + ". ")
                + pastText + (off.Length > 0 ? " " + off : "");
        List<VerdictAction> actions = [new(Investigate, "Investigate")];
        if (past > 0 && !activeOnly)
            actions.Add(new(ActiveOnly, "Active only"));
        return new PageVerdict(critical > 0 ? VerdictLevel.Critical : VerdictLevel.Warning, headline, why.Trim(), actions);
    }
}

/// <summary>What the last export did.</summary>
/// <param name="Error">Why it failed; null when it was saved.</param>
public sealed record ExportOutcome(string Path, int Sections, long Bytes, TimeSpan Took, string? Error = null);

/// <summary>Export / AI's verdict (#150): what an export will write and where, or what the last one did.</summary>
public static class ExportVerdict
{
    /// <summary>Exports now.</summary>
    public const string Export = "export";

    /// <summary>Opens the file saved last.</summary>
    public const string Open = "open";

    /// <param name="path">The full path the report will be saved to; null when the folder or file name is missing.</param>
    /// <param name="last">The last export from this page, if any.</param>
    public static PageVerdict From(int selected, int sections, string? path, ExportOutcome? last)
    {
        var culture = CultureInfo.CurrentCulture;
        if (last is { Error: { } error })
            return new PageVerdict(VerdictLevel.Warning, "The export failed", $"{error.Trim()} Nothing was saved to {last.Path}.",
                                   selected > 0 && path is not null ? [new(Export, "Try again")] : []);

        if (last is not null)
            return new PageVerdict(VerdictLevel.Ok,
                string.Format(culture, "Exported {0} to {1}", Sizes.Plural(last.Sections, "section", "sections"), System.IO.Path.GetFileName(last.Path)),
                string.Format(culture, "{0:N1} KB in {1:N1} s, saved to {2}. Paste it into an AI chat, or attach it.", last.Bytes / 1024.0, last.Took.TotalSeconds, last.Path),
                [new(Open, "Open the file"), new(Export, "Export again")]);

        if (selected == 0)
            return new PageVerdict(VerdictLevel.Unknown, "Nothing to export: no section is selected",
                "Tick the sections the report should have, or Select All.");
        if (path is null)
            return new PageVerdict(VerdictLevel.Unknown, "Choose where to save the report",
                "Enter a folder and a file name, or press … to pick them.");

        return new PageVerdict(VerdictLevel.Info,
            string.Format(culture, "Ready to export {0} of {1} sections", selected, sections),
            string.Format(culture, "To {0}: plain UTF-8 text with labelled sections and tables, read from the open server now.", path),
            [new(Export, "Export now")]);
    }
}
