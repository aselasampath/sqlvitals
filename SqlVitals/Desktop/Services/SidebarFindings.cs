using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// What the sidebar marks on a screen (#254): the worst health check past a threshold whose
/// evidence the screen is (<see cref="EvidenceRoutes.For"/>), and every such check, for the tooltip.
/// </summary>
/// <param name="ShortName">The worst check in a word or two, after the screen's name: "blocking", "RPO missed".</param>
/// <param name="Lines">Every check marking the screen, worst first: "Warning: blocking for 4 min".</param>
public sealed record PageFinding(HealthLevel Level, string ShortName, IReadOnlyList<string> Lines)
{
    /// <summary>"blocking, warning": how a screen reader announces it after the screen's name.</summary>
    public string Spoken => $"{ShortName}, {HealthScore.Label(Level).ToLowerInvariant()}";
}

/// <summary>
/// The sidebar's flat list (#254): the finding dots from the open server's score, the filter box's
/// matching and the Recent chips. Pure; <c>NavSidebar</c>, <c>NavRail</c> and <c>NavFlyout</c> draw them.
/// </summary>
public static class SidebarFindings
{
    /// <summary>No dots: no server open, not monitored, or every check within its thresholds.</summary>
    public static readonly IReadOnlyDictionary<string, PageFinding> None = new Dictionary<string, PageFinding>();

    /// <summary>How many screens the Recent row shows.</summary>
    public const int RecentShown = 3;

    /// <summary>A check's name beside the screen that explains it, short enough for a 232 px row.</summary>
    public static string ShortName(HealthIndicator indicator) => indicator switch
    {
        HealthIndicator.Cpu                 => "CPU",
        HealthIndicator.Blocking            => "blocking",
        HealthIndicator.OpenTransaction     => "open transaction",
        HealthIndicator.PageLifeExpectancy  => "PLE low",
        HealthIndicator.MemoryGrantsPending => "grants pending",
        HealthIndicator.LogSpace            => "log full",
        HealthIndicator.TempDbSpace         => "space",
        HealthIndicator.AzureIo             => "Azure IO",
        HealthIndicator.DiskFree            => "disk low",
        HealthIndicator.BackupsPastRpo      => "RPO missed",
        HealthIndicator.FailedJobs          => "job failed",
        _                                   => HealthThresholds.Info(indicator).Name,
    };

    /// <summary>
    /// The screens marked by the checks past a threshold in <paramref name="score"/>, by tag. Two
    /// checks on one screen: the worse one names it (<see cref="HealthScore.Past"/>'s order), the
    /// lines list both. None for no score, or a server that can't be reached (it has no checks).
    /// </summary>
    public static IReadOnlyDictionary<string, PageFinding> From(HealthScore? score)
    {
        if (score is null)
            return None;

        var found = new Dictionary<string, PageFinding>();
        foreach (var check in score.Past)
        {
            var tag  = EvidenceRoutes.For(check.Indicator).PageTag;
            var line = $"{HealthScore.Label(check.Level)}: {check.Phrase}";
            found[tag] = found.TryGetValue(tag, out var worse)
                ? worse with { Lines = [.. worse.Lines, line] }
                : new PageFinding(check.Level, ShortName(check.Indicator), [line]);
        }
        return found;
    }

    /// <summary>The findings less those on <paramref name="tags"/>: screens the server hasn't got, whose rows can't be opened.</summary>
    public static IReadOnlyDictionary<string, PageFinding> Without(IReadOnlyDictionary<string, PageFinding> findings, IEnumerable<string> tags)
    {
        var lacking = tags.ToHashSet();
        return lacking.Any(findings.ContainsKey)
            ? findings.Where(f => !lacking.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value)
            : findings;
    }

    /// <summary>The same screens marked the same way: nothing to draw again after a sample.</summary>
    public static bool Same(IReadOnlyDictionary<string, PageFinding> a, IReadOnlyDictionary<string, PageFinding> b) =>
        a.Count == b.Count && a.All(f => b.TryGetValue(f.Key, out var other)
                                         && other.Level == f.Value.Level && other.ShortName == f.Value.ShortName
                                         && other.Lines.SequenceEqual(f.Value.Lines));

    /// <summary>
    /// The screen Enter opens in the filter box: the best match, as the quick switcher ranks them
    /// (the whole name, its start, a word's start, anywhere in it, then the tooltip's words), the
    /// first in the sidebar's order on a tie. Null when none matches.
    /// </summary>
    public static NavPage? Best(string? query, IEnumerable<NavPage> pages)
    {
        var q = QuickSwitcher.Normalise(query);
        if (q.Length == 0)
            return null;
        return pages.Select((page, order) => (Page: page, Score: QuickSwitcher.Match(q, page.Label, page.ToolTip).Score, Order: order))
                    .Where(m => m.Score > 0)
                    .OrderByDescending(m => m.Score).ThenBy(m => m.Order)
                    .Select(m => m.Page)
                    .FirstOrDefault();
    }

    /// <summary>The worst of some screens' findings, e.g. a group's for its icon on the collapsed sidebar; null for none.</summary>
    public static PageFinding? Worst(IEnumerable<PageFinding?> findings) =>
        findings.OfType<PageFinding>().OrderByDescending(f => f.Level).FirstOrDefault();

    /// <summary>
    /// The filter box keeps a screen whose name or tooltip matches what was typed, as the quick
    /// switcher matches them (#144). Nothing typed keeps every screen.
    /// </summary>
    public static bool Matches(string? query, NavPage page)
    {
        var q = QuickSwitcher.Normalise(query);
        return q.Length == 0 || QuickSwitcher.Match(q, page.Label, page.ToolTip).Score > 0;
    }

    /// <summary>
    /// The Recent row: the last <see cref="RecentShown"/> screens of <paramref name="server"/>
    /// visited, most recent first, other than the one on show. None without a server.
    /// </summary>
    /// <param name="recent">The routes opened this session, most recent first (<see cref="RecentRoutes"/>).</param>
    public static IReadOnlyList<string> Recent(IReadOnlyList<Route> recent, Guid? server, string? current)
    {
        if (server is null)
            return [];
        return recent.Where(r => Workspace.ServerOf(r) == server)
                     .Select(r => NavMenu.Current(r.Tag))
                     .Where(tag => NavMenu.IsServerPage(tag) && tag != current)
                     .Distinct()
                     .Take(RecentShown)
                     .ToList();
    }
}
