using System.Globalization;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Verdicts;

/// <summary>Sizes as the Resources pages say them: "820 MB", "12.4 GB".</summary>
internal static class Sizes
{
    public static string Mb(double mb) => mb >= 1024
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} GB", mb / 1024)
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} MB", mb);

    public static string Kb(long kb) => Mb(kb / 1024.0);

    public static string Plural(int count, string one, string many) =>
        string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", count, count == 1 ? one : many);
}

/// <summary>
/// Memory's verdict (#150): queries waiting for a memory grant, Windows short of memory, or
/// how SQL Server's memory is used when neither.
/// </summary>
public static class MemoryVerdict
{
    /// <summary>Opens the Memory Grants tab with the longest waiter selected.</summary>
    public const string ShowWaiting = "show-waiting";

    /// <summary>Opens the Memory Clerks tab.</summary>
    public const string Clerks = "clerks";

    /// <summary>A grant waited for this long is critical: the query has done nothing all that time.</summary>
    public const int CriticalWaitMs = 30_000;

    /// <param name="snapshot">Its <c>Memory Grants Pending</c> counter says whether queries wait now.</param>
    /// <param name="grants">The grants read with the snapshot (the 25 largest), to name the longest waiter;
    /// null when only the snapshot was read (a trend reading).</param>
    public static PageVerdict From(MemorySnapshot snapshot, IReadOnlyList<MemoryGrant>? grants)
    {
        var culture = CultureInfo.CurrentCulture;
        var listed  = (grants ?? []).Where(g => g.IsPending).OrderByDescending(g => g.WaitForGrantMs).ToList();
        var count   = (int)Math.Max(snapshot.MemoryGrantsPending, listed.Count);
        var used    = string.Format(culture, "SQL Server uses {0} of its {1} target; the buffer pool {2}, the plan cache {3}.",
                                    Sizes.Kb(snapshot.TotalServerMemoryKB), Sizes.Kb(snapshot.TargetServerMemoryKB),
                                    Sizes.Kb(snapshot.DatabaseCacheMemoryKB), Sizes.Kb(snapshot.SqlCacheMemoryKB));
        var lowOs = snapshot.SystemMemoryState.Contains("low", StringComparison.OrdinalIgnoreCase);
        const string Cause = "Queries wait when the grants running use the memory set aside for them: " +
                             "large sorts and hashes, often from a missing index or a bad row estimate.";

        if (count > 0)
        {
            var queries = count == 1 ? "1 query waits" : $"{count:N0} queries wait";
            if (listed.FirstOrDefault() is not { } longest)
                return new PageVerdict(lowOs ? VerdictLevel.Critical : VerdictLevel.Warning, $"{queries} for a memory grant",
                    (grants is null ? "Refresh lists them. " : "They aren't among the 25 largest grants listed. ") + Cause + " " + used,
                    [new(ShowWaiting, "Show the waiting grants")]);

            var headline = string.Format(culture, "{0} for a memory grant; session {1} the longest, for {2:0.#} s",
                                         queries, longest.SessionId, longest.WaitForGrantMs / 1000.0);
            var why = string.Format(culture, "It asked for {0} (RESOURCE_SEMAPHORE). {1} {2}", Sizes.Kb(longest.RequestedMemoryKB), Cause, used);
            return new PageVerdict(longest.WaitForGrantMs >= CriticalWaitMs || lowOs ? VerdictLevel.Critical : VerdictLevel.Warning,
                                   headline, why, [new(ShowWaiting, "Show the waiting grants")]);
        }

        if (lowOs)
            return new PageVerdict(VerdictLevel.Critical, "Windows reports low physical memory",
                string.Format(culture, "{0} of {1} is available ({2}). SQL Server gives memory back when Windows runs low, so its caches shrink. {3}",
                              Sizes.Kb(snapshot.AvailablePhysicalKB), Sizes.Kb(snapshot.PhysicalMemoryKB), snapshot.SystemMemoryState, used),
                [new(Clerks, "Memory clerks")]);

        var running = (grants ?? []).Where(g => !g.IsPending).ToList();
        return new PageVerdict(VerdictLevel.Ok, "No query waits for memory",
            used + (grants is null ? ""
                : running.Count == 0 ? " No query holds a memory grant now."
                : string.Format(culture, " {0} {1} of grants.", Sizes.Plural(running.Count, "query holds", "queries hold"), Sizes.Kb(running.Sum(g => g.GrantedMemoryKB)))),
            [new(Clerks, "Memory clerks")]);
    }
}

/// <summary>
/// TempDB's verdict (#150): a session holding a lot of TempDB, files nearly full, or a large
/// version store; else how much room is left.
/// </summary>
public static class TempDbVerdict
{
    /// <summary>Selects the session holding the most.</summary>
    public const string ShowSession = "show-session";

    /// <summary>A session holding this much is worth a look (the grid's Status says Warning from 100 MB, Critical from 1 GB).</summary>
    public const long BigSessionKb = 500 * 1024;

    /// <summary>TempDB's files this full can't take a big spill without growing.</summary>
    public const double FullPct = 90;

    /// <summary>A version store this big means a long transaction keeps old row versions.</summary>
    public const long BigVersionStoreKb = 1024 * 1024;

    public static PageVerdict From(IReadOnlyList<TempDbFile> files, IReadOnlyList<TempDbSession> sessions, IReadOnlyList<TempDbCounter> counters)
    {
        var culture  = CultureInfo.CurrentCulture;
        var data     = files.Where(f => !string.Equals(f.FileType, "LOG", StringComparison.OrdinalIgnoreCase)).ToList();
        var size     = data.Sum(f => f.FileSizeMB);
        var free     = data.Sum(f => f.FreeSpaceMB);
        var usedPct  = size > 0 ? 100.0 * (size - free) / size : 0;
        var version  = counters.FirstOrDefault(c => c.CounterName.Contains("Version Store Size", StringComparison.OrdinalIgnoreCase))?.CounterValue ?? 0;
        var top      = sessions.OrderByDescending(s => s.NetTempDBUsageKB).FirstOrDefault();
        var topText  = top is null || top.NetTempDBUsageKB <= 0 ? "No session holds TempDB space now."
            : string.Format(culture, "Session {0}{1} holds the most, {2}: {3}.", top.SessionId,
                            string.IsNullOrWhiteSpace(top.LoginName) ? "" : $" ({top.LoginName})", Sizes.Kb(top.NetTempDBUsageKB),
                            top.InternalObjAllocKB > top.UserObjAllocKB ? "sorts and hashes spilling" : "temp tables and table variables");
        List<VerdictAction> show = top is { NetTempDBUsageKB: > 0 } ? [new(ShowSession, "Show the session")] : [];

        if (data.Count == 0 && sessions.Count == 0 && counters.Count == 0)
            return new PageVerdict(VerdictLevel.Unknown, "Nothing was read from TempDB",
                "The server returned no TempDB files, sessions or counters. Refresh to try again.");

        if (top is { NetTempDBUsageKB: >= BigSessionKb })
            return new PageVerdict(VerdictLevel.Warning,
                string.Format(culture, "Session {0} holds {1} of TempDB", top.SessionId, Sizes.Kb(top.NetTempDBUsageKB)),
                topText + string.Format(culture, " TempDB's data files are {0:0} % used.", usedPct), show);

        if (usedPct >= FullPct)
            return new PageVerdict(VerdictLevel.Warning,
                string.Format(culture, "TempDB's data files are {0:0} % used: {1} free of {2}", usedPct, Sizes.Mb(free), Sizes.Mb(size)),
                "A big spill or version store has to grow the files first, and a query waits while they grow. " + topText, show);

        if (version >= BigVersionStoreKb)
            return new PageVerdict(VerdictLevel.Warning,
                string.Format(culture, "The version store holds {0}", Sizes.Kb(version)),
                "Snapshot isolation and RCSI keep row versions until the oldest transaction that may read them ends: a long-running " +
                "transaction keeps them all. " + topText, show);

        return new PageVerdict(VerdictLevel.Ok,
            string.Format(culture, "TempDB has room: {0} free of {1}", Sizes.Mb(free), Sizes.Mb(size)),
            topText + (version > 0 ? string.Format(culture, " The version store holds {0}.", Sizes.Kb(version)) : ""), show);
    }
}

/// <summary>File I/O's verdict (#150): the files slower than your threshold, the worst first.</summary>
public static class FileIoVerdict
{
    /// <summary>Shows only the files over the threshold, the slowest selected.</summary>
    public const string ShowSlow = "show-slow";

    /// <param name="list">What the last readings measured; null before the first.</param>
    public static PageVerdict From(FileLatencyList? list)
    {
        var culture = CultureInfo.CurrentCulture;
        if (list is null)
            return new PageVerdict(VerdictLevel.Unknown, "Reading the file I/O totals…");
        if (!list.Available)
            return new PageVerdict(VerdictLevel.Unknown, "Couldn't read the file I/O", list.Problem);
        if (list.Measuring)
            return new PageVerdict(VerdictLevel.Info, "Measuring: latency needs a second reading",
                "It is the change between two readings, not the totals since the server started. The next reading is due in a few seconds.");

        var over = string.Format(culture, "over {0:N0} s", list.Seconds);
        if (list.Slowest is not { } slowest)
        {
            var busiest = list.Files.Where(f => f.Change is { IsIdle: false })
                                    .MaxBy(f => Math.Max(f.ReadLatencyMs ?? 0, f.WriteLatencyMs ?? 0));
            return new PageVerdict(VerdictLevel.Ok, "No file is slower than your threshold",
                string.Format(culture, "{0} {1}, highlighted for {2}.", Sizes.Plural(list.Files.Count, "file", "files"), over, list.Thresholds.Describe())
                + (busiest is null ? " None read or wrote anything."
                    : string.Format(culture, " The slowest, {0} · {1}, took {2} ms.", busiest.DatabaseName, busiest.FileName,
                                    FileLatency.FormatMs(Math.Max(busiest.ReadLatencyMs ?? 0, busiest.WriteLatencyMs ?? 0)))));
        }

        // Reads and writes of a file share its threshold, so the slower one is the one over it.
        var (what, ms) = (slowest.WriteLatencyMs ?? 0) > (slowest.ReadLatencyMs ?? 0)
            ? ("writes", slowest.WriteLatencyText) : ("reads", slowest.ReadLatencyText);
        var headline = string.Format(culture, "{0} slower than your threshold; {1} · {2} {3} in {4} ms",
                                     Sizes.Plural(list.SlowCount, "file is", "files are"), slowest.DatabaseName, slowest.FileName, what, ms);
        var slowVolumes = list.Volumes.Where(v => v.IsSlow).ToList();
        var why = string.Format(culture, "Latency {0}, highlighted for {1}.", over, list.Thresholds.Describe())
                + (list.CriticalCount > 0 ? string.Format(culture, " {0} at {1:0}× the threshold or more.", Sizes.Plural(list.CriticalCount, "is", "are"), FileLatencyThresholds.CriticalFactor) : "")
                + (slowVolumes.Count > 0
                    ? string.Format(culture, " {0} slow as a whole ({1}): the storage itself, not one file.", slowVolumes.Count == 1 ? "A volume is" : $"{slowVolumes.Count} volumes are",
                                    string.Join(", ", slowVolumes.Select(v => v.VolumeText)))
                    : " No volume is slow as a whole: look at what reads or writes that file.");
        return new PageVerdict(list.CriticalCount > 0 ? VerdictLevel.Critical : VerdictLevel.Warning, headline, why,
                               [new(ShowSlow, "Show the slow files")]);
    }
}

/// <summary>DB Storage's verdict (#150): files about to hit their maximum size, auto-shrink on, or the storage at a glance.</summary>
public static class DbStorageVerdict
{
    /// <summary>Selects the file the verdict is about on the Database Files tab.</summary>
    public const string ShowFile = "show-file";

    /// <summary>A file with a maximum size this full can't grow much more.</summary>
    public const double NearMaxPct = 90;

    /// <summary>The file the verdict is about, for <see cref="ShowFile"/>; null when none.</summary>
    public static DatabaseFile? Subject(IReadOnlyList<DatabaseFile> files) =>
        NearMax(files).FirstOrDefault() ?? files.Where(f => f.AutoShrink).OrderByDescending(f => f.FileSizeMB).FirstOrDefault();

    private static IEnumerable<DatabaseFile> NearMax(IEnumerable<DatabaseFile> files) => files
        .Where(f => f.MaxSizeMB > 0 && f.FileSizeMB >= f.MaxSizeMB * (decimal)(NearMaxPct / 100))
        .OrderByDescending(f => f.FileSizeMB / f.MaxSizeMB);

    public static PageVerdict From(IReadOnlyList<DatabaseFile> files, IReadOnlyList<TempDbFileSummary> tempDb)
    {
        var culture = CultureInfo.CurrentCulture;
        if (files.Count == 0)
            return new PageVerdict(VerdictLevel.Unknown, "No database files were read",
                "The server returned no files of an online database. Refresh to try again.");

        var databases = files.Select(f => f.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var largest   = files.GroupBy(f => f.DatabaseName).Select(g => (Name: g.Key, Mb: (double)g.Sum(f => f.FileSizeMB)))
                             .OrderByDescending(d => d.Mb).First();
        var glance = string.Format(culture, "{0} in {1}; the largest, {2}, {3}. Data {4}, log {5}, TempDB {6}.",
                                   Sizes.Mb((double)files.Sum(f => f.FileSizeMB)), Sizes.Plural(databases, "database", "databases"),
                                   largest.Name, Sizes.Mb(largest.Mb),
                                   Sizes.Mb((double)files.Where(f => f.FileType == "ROWS").Sum(f => f.FileSizeMB)),
                                   Sizes.Mb((double)files.Where(f => f.FileType == "LOG").Sum(f => f.FileSizeMB)),
                                   Sizes.Mb((double)tempDb.Sum(f => f.SizeMB)));
        List<VerdictAction> show = [new(ShowFile, "Show the file")];

        var nearMax = NearMax(files).ToList();
        if (nearMax.Count > 0)
        {
            var file = nearMax[0];
            return new PageVerdict(VerdictLevel.Warning,
                string.Format(culture, "{0} near {1} maximum size; {2} · {3} at {4:0} %",
                              Sizes.Plural(nearMax.Count, "file is", "files are"), nearMax.Count == 1 ? "its" : "their",
                              file.DatabaseName, file.LogicalFileName, 100 * file.FileSizeMB / file.MaxSizeMB),
                string.Format(culture, "It is {0} of {1}. A file at its maximum can't grow: inserts fail with error 1105 (data) or 9002 (log). ",
                              Sizes.Mb((double)file.FileSizeMB), Sizes.Mb((double)file.MaxSizeMB)) + glance, show);
        }

        var shrinking = files.Where(f => f.AutoShrink).Select(f => f.DatabaseName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (shrinking.Count > 0)
            return new PageVerdict(VerdictLevel.Warning,
                string.Format(culture, "Auto-shrink is on for {0}", shrinking.Count == 1 ? shrinking[0] : $"{shrinking.Count} databases"),
                "Shrinking moves pages around and fragments every index, and the file grows straight back. Config Checks has the script to turn it off. " + glance, show);

        return new PageVerdict(VerdictLevel.Info, string.Format(culture, "{0} of database files, none near a limit", Sizes.Mb((double)files.Sum(f => f.FileSizeMB))), glance);
    }
}

/// <summary>One counter's line on Perfmon: its values per point, a null where nothing was recorded.</summary>
public sealed record PerfmonSeries(string Counter, IReadOnlyList<double?> Values);

/// <summary>
/// Perfmon's verdict (#150): the counters charted that are past a known limit now, and the ones
/// rising. Counters have different units, so they aren't compared with each other.
/// </summary>
public static class PerfmonVerdict
{
    /// <summary>Opens the page that shows the cause: <see cref="Limit.Page"/>.</summary>
    public const string Cause = "cause";

    /// <summary>A counter with a value past which something is wrong, and where to look.</summary>
    public sealed record Limit(string Counter, double Above, string Meaning, string Page, string PageText);

    /// <summary>The counters with a well-known limit; any other is only charted.</summary>
    public static readonly IReadOnlyList<Limit> Limits =
    [
        new("Memory Grants Pending", 0, "queries wait for a memory grant", "Memory", "Memory"),
        new("Processes blocked", 0, "sessions are blocked", "Processes", "Processes"),
        new("Number of Deadlocks/sec", 0, "deadlocks are happening", "Deadlocks", "Deadlocks"),
        new("Free list stalls/sec", 2, "requests wait for a free buffer page: memory pressure", "Memory", "Memory"),
        new("Lazy writes/sec", 20, "the lazy writer flushes pages to free buffers: memory pressure", "Memory", "Memory"),
    ];

    /// <summary>The limit a verdict's <see cref="Cause"/> opens the page of; null when none is past.</summary>
    public static Limit? Breached(IReadOnlyList<PerfmonSeries> series) => Past(series).Select(p => p.Limit).FirstOrDefault();

    private static IEnumerable<(Limit Limit, double Value)> Past(IReadOnlyList<PerfmonSeries> series) =>
        from limit in Limits
        let line = series.FirstOrDefault(s => string.Equals(s.Counter, limit.Counter, StringComparison.OrdinalIgnoreCase))
        let last = line?.Values.LastOrDefault(v => v is not null)
        where last > limit.Above
        select (limit, last!.Value);

    public static PageVerdict From(IReadOnlyList<PerfmonSeries> series, WaitTrendState state, string? noData = null)
    {
        var culture = CultureInfo.CurrentCulture;
        if (series.Count == 0)
            return new PageVerdict(VerdictLevel.Unknown, "No counter is selected",
                "Tick counters on the left, or pick a group of them.");

        var drawn = series.Where(s => s.Values.Any(v => v is not null)).ToList();
        if (drawn.Count == 0)
            return state switch
            {
                WaitTrendState.NotStarted => new PageVerdict(VerdictLevel.Unknown, "Not charting yet",
                    noData ?? "Press Resume (▶) in the top bar: the counters are read every few seconds and drawn as they change."),
                WaitTrendState.Live => new PageVerdict(VerdictLevel.Info, "Reading the first samples",
                    noData ?? "A rate needs two reads to show how much happened between them."),
                _ => new PageVerdict(VerdictLevel.Unknown, "Nothing recorded in this range",
                    noData ?? "The monitoring history has no counters for this range."),
            };

        // A counter past its limit is named already, so it isn't listed as rising too.
        var past = Past(drawn).ToList();
        var rising = drawn.Where(s => WaitTrendVerdict.Rising(s.Values.OfType<double>().ToList()))
                          .Select(s => s.Counter)
                          .Where(c => !past.Any(p => string.Equals(p.Limit.Counter, c, StringComparison.OrdinalIgnoreCase)))
                          .ToList();
        var risingText = rising.Count == 0 ? ""
            : string.Format(culture, " Rising: {0}.", string.Join(", ", rising.Take(3)) + (rising.Count > 3 ? $" and {rising.Count - 3} more" : ""));

        if (past.Count > 0)
        {
            var (limit, value) = past[0];
            var headline = string.Format(culture, "{0} is {1:#,0.##}: {2}", limit.Counter, value, limit.Meaning);
            var why = (past.Count > 1
                        ? "Also past its limit: " + string.Join(", ", past.Skip(1).Select(p => string.Format(culture, "{0} at {1:#,0.##}", p.Limit.Counter, p.Value))) + "."
                        : string.Format(culture, "Its latest value is above {0:#,0.##}.", limit.Above))
                    + risingText;
            return new PageVerdict(VerdictLevel.Warning, headline, why.Trim(), [new(Cause, limit.PageText)]);
        }

        var watched = drawn.Count(s => Limits.Any(l => string.Equals(l.Counter, s.Counter, StringComparison.OrdinalIgnoreCase)));
        return new PageVerdict(rising.Count > 0 ? VerdictLevel.Info : VerdictLevel.Ok,
            string.Format(culture, "{0} charted, none past a known limit", Sizes.Plural(drawn.Count, "counter", "counters")),
            (watched == 0
                ? "None of them has a well-known limit (memory grants pending, processes blocked, deadlocks, free list stalls, lazy writes): read them against your own baseline."
                : string.Format(culture, "{0} of them {1} a well-known limit.", watched, watched == 1 ? "has" : "have"))
            + risingText);
    }
}
