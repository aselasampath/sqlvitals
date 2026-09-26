using System.Globalization;
using System.Text;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.ConfigChecks;

/// <summary>
/// Checks a server's configuration against best practice (#42): MAXDOP, cost threshold for
/// parallelism, max server memory, TempDB's data files, and each database's auto-shrink, page
/// verify and compatibility level. Each check gives Pass or Warn, the value now, the value
/// recommended and why; one that can't be checked here says why not.
/// </summary>
public static class ConfigurationChecks
{
    /// <summary>sp_configure's default for max server memory: no limit.</summary>
    public const long UnlimitedServerMemoryMb = int.MaxValue;

    /// <summary>A cost threshold for parallelism under this is too low for today's hardware.</summary>
    public const int MinCostThreshold = 25;

    /// <summary>Where to start: commonly recommended, then tuned from the plan cache.</summary>
    public const int RecommendedCostThreshold = 50;

    /// <summary>TempDB data files differing in size by more than this share of the largest aren't "the same size".</summary>
    public const double TempDbSizeTolerance = 0.05;

    public static ConfigCheckList Run(ServerConfiguration config)
    {
        var checks = new List<ConfigCheck>
        {
            MaxDop(config),
            CostThreshold(config),
            MaxServerMemory(config),
            TempDbFileCount(config),
            TempDbFileSizes(config),
        };

        var databases = config.IsAzureSqlDatabase ? config.Databases.Where(d => d.IsCurrent).ToList() : config.Databases;
        checks.AddRange(AutoShrink(databases));
        checks.AddRange(PageVerify(databases));
        checks.AddRange(CompatibilityLevel(config, databases));

        return new ConfigCheckList(ConfigCheckList.Sort(checks), Problem(config));
    }

    private static string? Problem(ServerConfiguration config)
    {
        if (config.IsAzureSqlDatabase)
            return "Azure SQL Database manages the server's memory, cost threshold for parallelism and TempDB, so only this " +
                   "database's MAXDOP, auto-shrink, page verify and compatibility level are checked.";

        if (config.Hardware is null)
            return "This login can't see the server's processors and memory, so MAXDOP, max server memory and the TempDB file " +
                   "count can only be checked in part. Grant it VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on SQL Server " +
                   "2022 and later) to check them all.";

        return null;
    }

    // ── MAXDOP ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Microsoft's recommendation: with one NUMA node, the number of processors, at most 8. With
    /// several, the processors per node when that's 16 or fewer, otherwise half of them, at most 16.
    /// </summary>
    public static int RecommendedMaxDop(ServerHardware hw)
    {
        var processors = Math.Max(1, hw.LogicalProcessors);
        var perNode    = Math.Max(1, hw.ProcessorsPerNumaNode);
        if (hw.NumaNodes <= 1)
            return Math.Min(processors, 8);
        return perNode <= 16 ? perNode : Math.Min(16, perNode / 2);
    }

    private static string MaxDopRule(ServerHardware hw)
    {
        if (hw.NumaNodes <= 1)
            return hw.LogicalProcessors <= 8
                ? "with one NUMA node and 8 or fewer processors, no more than the number of processors"
                : "with one NUMA node and more than 8 processors, 8";
        return hw.ProcessorsPerNumaNode <= 16
            ? "with several NUMA nodes of up to 16 processors, no more than the processors in one node"
            : "with more than 16 processors per NUMA node, half of them, at most 16";
    }

    private static string DescribeHardware(ServerHardware hw) =>
        $"This server gives SQL Server {Plural(hw.LogicalProcessors, "logical processor")}" +
        (hw.NumaNodes > 1 ? $" in {hw.NumaNodes} NUMA nodes of up to {hw.ProcessorsPerNumaNode}" : " in one NUMA node") + ".";

    internal static ConfigCheck MaxDop(ServerConfiguration config)
    {
        const string what =
            "MAXDOP caps how many processors one query can use when it runs in parallel. Too high, and a few large queries " +
            "take every worker thread and processor between them, with CXPACKET and CXCONSUMER waits, while small queries queue.";

        if (config.IsAzureSqlDatabase)
            return AzureMaxDop(config, what);

        if (config.MaxDop is not { } option)
            return NotChecked(ConfigCheckKind.MaxDop, "max degree of parallelism couldn't be read from sys.configurations.");

        var value   = option.ValueInUse;
        var pending = Pending(option);

        if (config.Hardware is not { } hw)
        {
            // 1 is always within the recommendation; anything else depends on the processors.
            return value == 1
                ? new ConfigCheck(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, "1", "No more than the processors per NUMA node",
                    what + " At 1, no query runs in parallel." + pending)
                : new ConfigCheck(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.NotChecked, value == 0 ? "0 (every processor)" : $"{value}",
                    "Depends on the processors", what + " How many processors the server has needs VIEW SERVER STATE to read." + pending);
        }

        var recommended = RecommendedMaxDop(hw);
        var processors  = Math.Max(1, hw.LogicalProcessors);
        var effective   = value <= 0 ? processors : Math.Min(value, processors);

        var current = value == 0 ? $"0 (all {processors})" : $"{value}";
        var recommendedText = recommended == 1 ? "Any (one processor)"
            : hw.NumaNodes <= 1 && recommended == processors ? $"0, or 1 to {recommended}"
            : $"{recommended} or lower";

        var basis = $" {DescribeHardware(hw)} Microsoft recommends, {MaxDopRule(hw)}: {recommended}.";
        var note  = " A database can set its own with ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP.";

        if (effective <= recommended)
        {
            var one = value == 1 && processors > 1
                ? " At 1 no query runs in parallel: right for SharePoint and some OLTP workloads, but large reports, " +
                  "CHECKDB and index rebuilds run on one processor."
                : "";
            return new ConfigCheck(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current, recommendedText,
                what + basis + one + note + pending);
        }

        var why = value == 0
            ? $" At 0, a single query can use all {processors} processors" + (hw.NumaNodes > 1 ? ", across NUMA nodes." : ".")
            : $" At {value}, a single query can use {effective} processors.";
        return new ConfigCheck(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, current, recommendedText,
            what + why + basis + note + pending,
            SpConfigure("max degree of parallelism", recommended));
    }

    private static ConfigCheck AzureMaxDop(ServerConfiguration config, string what)
    {
        var scope = CurrentDatabase(config);
        if (config.DatabaseMaxDop is not { } value)
            return NotChecked(ConfigCheckKind.MaxDop, "The MAXDOP database scoped configuration couldn't be read.", scope);

        const int azureDefault = 8;
        var note = " On Azure SQL Database it is set per database (ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP); new " +
                   $"databases get {azureDefault}, which Microsoft recommends for most workloads.";

        if (value is >= 1 and <= azureDefault)
            return new ConfigCheck(ConfigCheckKind.MaxDop, scope, ConfigCheckStatus.Pass, $"{value}", $"{azureDefault} or lower", what + note);

        return new ConfigCheck(ConfigCheckKind.MaxDop, scope, ConfigCheckStatus.Warn,
            value == 0 ? "0 (every vCore)" : $"{value}", $"{azureDefault} or lower",
            what + $" At {value}, one query can use " +
            (value == 0 ? "every vCore." : $"up to {value} vCores.") + note,
            $"ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = {azureDefault};");
    }

    // ── Cost threshold for parallelism ────────────────────────────────────────

    internal static ConfigCheck CostThreshold(ServerConfiguration config)
    {
        const string what =
            "The estimated cost a query's plan has to reach before SQL Server considers running it in parallel. The default, 5, " +
            "was set for the hardware of the 1990s: today almost any query over a few thousand rows reaches it, so small OLTP " +
            "queries go parallel, using several threads each for no gain and adding CXPACKET waits.";

        if (config.IsAzureSqlDatabase)
            return NotChecked(ConfigCheckKind.CostThreshold,
                "Azure SQL Database manages the server's settings, and cost threshold for parallelism can't be changed there.",
                CurrentDatabase(config));

        if (config.CostThreshold is not { } option)
            return NotChecked(ConfigCheckKind.CostThreshold, "cost threshold for parallelism couldn't be read from sys.configurations.");

        var value       = option.ValueInUse;
        var current     = value == 5 ? "5 (the default)" : $"{value}";
        var recommended = $"{RecommendedCostThreshold} to start ({MinCostThreshold} or more)";
        var advice      = $" Start at {RecommendedCostThreshold}, then tune it from the costs of the plans in the plan cache.";
        var pending     = Pending(option);

        // Nothing runs in parallel, so the threshold is never used.
        var serial = config.MaxDop?.ValueInUse == 1 || config.Hardware?.LogicalProcessors == 1;
        if (serial)
            return new ConfigCheck(ConfigCheckKind.CostThreshold, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current, recommended,
                what + (config.Hardware?.LogicalProcessors == 1
                    ? " With one processor no query runs in parallel, so it has no effect here."
                    : " With MAXDOP at 1 no query runs in parallel, so it has no effect here until MAXDOP is raised.") + pending);

        if (value >= MinCostThreshold)
            return new ConfigCheck(ConfigCheckKind.CostThreshold, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current, recommended,
                what + advice + pending);

        return new ConfigCheck(ConfigCheckKind.CostThreshold, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, current, recommended,
            what + advice + pending,
            SpConfigure("cost threshold for parallelism", RecommendedCostThreshold));
    }

    // ── Max server memory ─────────────────────────────────────────────────────

    /// <summary>
    /// Physical memory less what Windows needs: 1 GB, plus 1 GB for every 4 GB from 4 to 16 GB, plus
    /// 1 GB for every 8 GB above 16 GB. Never less than half the memory.
    /// </summary>
    public static long RecommendedMaxServerMemoryMb(long physicalMb)
    {
        var reserve = 1024
                    + Math.Max(0, Math.Min(physicalMb, 16 * 1024) - 4 * 1024) / 4
                    + Math.Max(0, physicalMb - 16 * 1024) / 8;
        return Math.Max(physicalMb - reserve, physicalMb / 2);
    }

    internal static ConfigCheck MaxServerMemory(ServerConfiguration config)
    {
        const string what =
            "The most memory SQL Server's buffer pool and caches may take. Left unlimited, SQL Server keeps taking memory " +
            "until Windows runs short, which then trims or pages it out: everything on the server slows down.";
        const string others =
            " Take off more for any other SQL Server instance, SSIS, SSAS, SSRS or application on the same server.";

        if (config.IsAzureSqlDatabase)
            return NotChecked(ConfigCheckKind.MaxServerMemory, "Azure SQL Database manages memory by service tier.", CurrentDatabase(config));

        if (config.MaxServerMemoryMb is not { } option)
            return NotChecked(ConfigCheckKind.MaxServerMemory, "max server memory (MB) couldn't be read from sys.configurations.");

        var value     = option.ValueInUse;
        var unlimited = value >= UnlimitedServerMemoryMb;
        var current   = unlimited ? "Not set (no limit)" : FormatMb(value);
        var pending   = Pending(option);

        if (config.Hardware is not { } hw || hw.PhysicalMemoryMb <= 0)
        {
            return unlimited
                ? new ConfigCheck(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, current,
                    "Memory less Windows' share",
                    what + $" It isn't set: {UnlimitedServerMemoryMb:N0} MB, the default, is no limit. How much memory the server has needs VIEW SERVER STATE to read, so the value to set can't be worked out here." +
                    others + pending)
                : new ConfigCheck(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.NotChecked, current,
                    "Memory less Windows' share",
                    what + " How much memory the server has needs VIEW SERVER STATE to read." + pending);
        }

        var physical    = hw.PhysicalMemoryMb;
        var recommended = RecommendedMaxServerMemoryMb(physical);
        var basis = $" The server has {FormatMb(physical)}. Leaving Windows {FormatGb(physical - recommended)} (1 GB, plus 1 GB for every " +
                    $"4 GB from 4 to 16 GB and 1 GB for every 8 GB above 16 GB) gives {FormatMb(recommended)}.";

        if (!unlimited && value <= recommended)
            return new ConfigCheck(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current,
                $"{recommended:N0} MB or less", what + basis + others + pending);

        var why = unlimited ? $" It isn't set: {UnlimitedServerMemoryMb:N0} MB, the default, is no limit."
            : value >= physical ? $" At {FormatMb(value)}, more than the server has, it is no limit at all."
            : $" At {FormatMb(value)}, it leaves Windows only {FormatGb(physical - value)}.";
        return new ConfigCheck(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, current,
            $"{recommended:N0} MB or less", what + why + basis + others + pending,
            SpConfigure("max server memory (MB)", recommended));
    }

    // ── TempDB ────────────────────────────────────────────────────────────────

    /// <summary>One data file per processor, up to 8. Beyond that, more only when contention remains.</summary>
    public static int RecommendedTempDbFiles(ServerHardware hw) => Math.Clamp(hw.LogicalProcessors, 1, 8);

    internal static ConfigCheck TempDbFileCount(ServerConfiguration config)
    {
        const string what =
            "Every temp table, table variable, sort or hash spill and row version allocates pages in TempDB, and with too few data " +
            "files their allocation pages (PFS, GAM, SGAM) become a hot spot: sessions queue on PAGELATCH waits on pages like 2:1:1. " +
            "Microsoft recommends one data file per logical processor up to 8; with more than 8, start with 8 and add four at a " +
            "time only while the contention remains.";

        if (config.IsAzureSqlDatabase)
            return NotChecked(ConfigCheckKind.TempDbFileCount, "Azure SQL Database manages TempDB.", CurrentDatabase(config));

        if (config.TempDbFiles is not { } files)
            return NotChecked(ConfigCheckKind.TempDbFileCount, "TempDB's files couldn't be read from tempdb.sys.database_files.");

        var count   = files.Count;
        var current = Plural(count, "data file");

        if (config.Hardware is not { } hw)
        {
            // 8 is always enough to start with; fewer depends on the processors.
            return count >= 8
                ? new ConfigCheck(ConfigCheckKind.TempDbFileCount, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current,
                    "One per processor, up to 8", what)
                : new ConfigCheck(ConfigCheckKind.TempDbFileCount, ConfigCheck.ServerScope, ConfigCheckStatus.NotChecked, current,
                    "One per processor, up to 8", what + " How many processors the server has needs VIEW SERVER STATE to read.");
        }

        var recommended     = RecommendedTempDbFiles(hw);
        var recommendedText = hw.LogicalProcessors > 8 ? "8 (then 4 more at a time if needed)" : $"{recommended} (one per processor)";
        var basis           = $" {DescribeHardware(hw)}";

        if (count >= recommended)
            return new ConfigCheck(ConfigCheckKind.TempDbFileCount, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current,
                recommendedText, what + basis);

        return new ConfigCheck(ConfigCheckKind.TempDbFileCount, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, current, recommendedText,
            what + basis + $" Add {Plural(recommended - count, "file")} the same size as the others, on a drive with room for them.",
            AddTempDbFilesScript(files, recommended));
    }

    internal static ConfigCheck TempDbFileSizes(ServerConfiguration config)
    {
        const string what =
            "SQL Server fills TempDB's data files in proportion to their free space, so a bigger file, or one that grows more, " +
            "takes more of the allocations and the other files stop sharing the load. Give them all the same size and the same " +
            "growth in MB; from SQL Server 2016 TempDB's files then grow together.";
        const string recommended = "All the same size and growth";

        if (config.IsAzureSqlDatabase)
            return NotChecked(ConfigCheckKind.TempDbFileSizes, "Azure SQL Database manages TempDB.", CurrentDatabase(config));

        if (config.TempDbFiles is not { Count: > 0 } files)
            return NotChecked(ConfigCheckKind.TempDbFileSizes, "TempDB's files couldn't be read from tempdb.sys.database_files.");

        if (files.Count == 1)
            return new ConfigCheck(ConfigCheckKind.TempDbFileSizes, ConfigCheck.ServerScope, ConfigCheckStatus.Pass,
                $"1 file of {files[0].SizeMb:N0} MB", recommended, "There is only one data file, so there's nothing to even out; see TempDB data files.");

        var smallest    = files.Min(f => f.SizeMb);
        var largest     = files.Max(f => f.SizeMb);
        var sameSize    = largest - smallest <= largest * TempDbSizeTolerance;
        var growths     = files.Select(f => f.GrowthText).Distinct().ToList();
        var sameGrowth  = growths.Count == 1;
        var percent     = files.Any(f => f.IsPercentGrowth && f.Growth > 0);

        var current = (smallest == largest ? $"{files.Count} × {largest:N0} MB" : $"{smallest:N0} to {largest:N0} MB") +
                      ", growth " + string.Join(", ", growths);

        if (sameSize && sameGrowth && !percent)
            return new ConfigCheck(ConfigCheckKind.TempDbFileSizes, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, current, recommended, what);

        var problems = new List<string>();
        if (!sameSize)   problems.Add($"their sizes differ, from {smallest:N0} to {largest:N0} MB");
        if (!sameGrowth) problems.Add($"they grow by different amounts ({string.Join(", ", growths)})");
        if (percent)     problems.Add("a percentage growth gets bigger as the file does, so the files drift apart");
        return new ConfigCheck(ConfigCheckKind.TempDbFileSizes, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, current, recommended,
            what + " Here " + string.Join("; ", problems) + ".",
            EvenOutTempDbScript(files));
    }

    // ── Databases ─────────────────────────────────────────────────────────────

    internal static IEnumerable<ConfigCheck> AutoShrink(IReadOnlyList<DatabaseOptions> databases)
    {
        const string what =
            "Auto-shrink shrinks a database's files whenever a quarter of them is free: it moves pages from the end of each file " +
            "to the front, fragmenting every index it touches, and the space is usually needed again, so the file grows back. " +
            "Both use I/O and can block. Shrink by hand, rarely, when space really must be given back.";

        return PerDatabase(ConfigCheckKind.AutoShrink, databases, d => d.AutoShrink,
            _ => new("ON", "OFF", what),
            d => $"ALTER DATABASE {UnusedIndexDropScript.QuoteName(d.Name)} SET AUTO_SHRINK OFF WITH NO_WAIT;",
            () => new("OFF", "OFF", what));
    }

    internal static IEnumerable<ConfigCheck> PageVerify(IReadOnlyList<DatabaseOptions> databases)
    {
        const string what =
            "With CHECKSUM, SQL Server writes a checksum on each page and checks it when the page is read back, so damage done by " +
            "the storage is caught at the first read (error 824), and by DBCC CHECKDB and backups WITH CHECKSUM.";
        const string after =
            " Pages get a checksum the next time they're written after the change; rebuilding the indexes writes them all.";

        return PerDatabase(ConfigCheckKind.PageVerify, databases, d => !d.PageVerify.Equals("CHECKSUM", StringComparison.OrdinalIgnoreCase),
            d => new(d.PageVerify.Length == 0 ? "Unknown" : d.PageVerify, "CHECKSUM",
                what + (d.PageVerify.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                    ? " With NONE, nothing is checked: damaged pages are read as if they were good."
                    : " TORN_PAGE_DETECTION only notices a page that was partly written, not one damaged afterwards.") + after),
            d => $"ALTER DATABASE {UnusedIndexDropScript.QuoteName(d.Name)} SET PAGE_VERIFY CHECKSUM WITH NO_WAIT;",
            () => new("CHECKSUM", "CHECKSUM", what));
    }

    /// <summary>
    /// The newest compatibility level this server offers: its version's on SQL Server. Azure SQL
    /// Database and Managed Instance report no version, so there the highest any database uses.
    /// </summary>
    public static int? NewestCompatibilityLevel(ServerConfiguration config)
    {
        if (!config.IsVersionless && config.ProductMajorVersion is { } major and >= 9)
            return major * 10;
        return config.Databases.Count > 0 ? config.Databases.Max(d => d.CompatibilityLevel) : null;
    }

    /// <summary>"SQL Server 2022" for 160.</summary>
    public static string? VersionOf(int level) => level switch
    {
        80  => "SQL Server 2000",
        90  => "SQL Server 2005",
        100 => "SQL Server 2008",
        110 => "SQL Server 2012",
        120 => "SQL Server 2014",
        130 => "SQL Server 2016",
        140 => "SQL Server 2017",
        150 => "SQL Server 2019",
        160 => "SQL Server 2022",
        170 => "SQL Server 2025",
        _   => null,
    };

    private static string LevelText(int level) => VersionOf(level) is { } version ? $"{level} ({version})" : $"{level}";

    internal static IEnumerable<ConfigCheck> CompatibilityLevel(ServerConfiguration config, IReadOnlyList<DatabaseOptions> databases)
    {
        const string what =
            "A database at an older compatibility level keeps that version's query optimizer (its cardinality estimator and plan " +
            "choices) and misses the newer query processing features, such as batch mode on rowstore, adaptive joins and memory " +
            "grant feedback. Moving up usually makes queries faster but can change plans: turn Query Store on first, change the " +
            "level at a quiet time, and force the old plan for any query that gets slower.";

        if (databases.Count == 0)
            return [];

        if (NewestCompatibilityLevel(config) is not { } newest)
            return [NotChecked(ConfigCheckKind.CompatibilityLevel, "The server's version couldn't be read.",
                AllDatabases(databases.Count, 0))];

        var basis = config.IsVersionless
            ? $" Azure SQL reports no version, so this is held to {newest}, the highest level a database here uses; newer ones may be available."
            : $" {newest} is this server's own level.";

        return PerDatabase(ConfigCheckKind.CompatibilityLevel, databases, d => d.CompatibilityLevel < newest,
            d => new(LevelText(d.CompatibilityLevel), LevelText(newest),
                what + basis + (d.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                    ? " model's level is the one new databases get."
                    : "")),
            d => $"-- Turn Query Store on and let it capture a baseline first, then force the old plan for any query that regresses.\n" +
                 $"-- ALTER DATABASE {UnusedIndexDropScript.QuoteName(d.Name)} SET QUERY_STORE = ON;\n" +
                 $"ALTER DATABASE {UnusedIndexDropScript.QuoteName(d.Name)} SET COMPATIBILITY_LEVEL = {newest};",
            () => new(LevelText(newest), LevelText(newest), what + basis));
    }

    private sealed record Row(string Current, string Recommended, string Explanation);

    // A warning for each database that fails, then one Pass row for the rest.
    private static IEnumerable<ConfigCheck> PerDatabase(
        ConfigCheckKind                 kind,
        IReadOnlyList<DatabaseOptions>  databases,
        Func<DatabaseOptions, bool>     fails,
        Func<DatabaseOptions, Row>      warning,
        Func<DatabaseOptions, string>   fix,
        Func<Row>                       pass)
    {
        var failing = databases.Where(fails).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var db in failing)
        {
            var row = warning(db);
            yield return new ConfigCheck(kind, db.Name, ConfigCheckStatus.Warn, row.Current, row.Recommended, row.Explanation, fix(db));
        }

        var passing = databases.Where(d => !fails(d)).ToList();
        if (passing.Count == 0)
            yield break;

        var ok = pass();
        var scope = passing.Count == 1 ? passing[0].Name : AllDatabases(passing.Count, failing.Count);
        yield return new ConfigCheck(kind, scope, ConfigCheckStatus.Pass, ok.Current, ok.Recommended, ok.Explanation);
    }

    private static string AllDatabases(int count, int others) =>
        others == 0 ? $"All {count:N0} databases" : $"{count:N0} other database{(count == 1 ? "" : "s")}";

    // ── Scripts ───────────────────────────────────────────────────────────────

    private static string SpConfigure(string option, long value) =>
        "EXEC sys.sp_configure N'show advanced options', 1;\n" +
        "RECONFIGURE;\n" +
        $"EXEC sys.sp_configure N'{option}', {value.ToString(CultureInfo.InvariantCulture)};\n" +
        "RECONFIGURE;";

    /// <summary>
    /// Adds files the size and growth of the largest, in the first file's folder, named so as not
    /// to clash with the ones there.
    /// </summary>
    internal static string AddTempDbFilesScript(IReadOnlyList<TempDbDataFile> files, int target)
    {
        var model  = files.Count > 0 ? files.MaxBy(f => f.SizeMb)! : new TempDbDataFile("tempdev", "", 1024, false, 64);
        var folder = files.Count > 0 ? FolderOf(files[0].PhysicalName) : null;
        var names  = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths  = files.Select(f => f.PhysicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var growth = model.Growth == 0 || model.IsPercentGrowth ? "64MB" : $"{model.Growth.ToString(CultureInfo.InvariantCulture)}MB";

        var sb = new StringBuilder();
        sb.AppendLine($"-- Each new file is {model.SizeMb:N0} MB, like the largest now; check the drive has room for them.");
        if (folder is null)
            sb.AppendLine("-- Replace <folder> with the folder TempDB's files are in.");

        var n = 1;
        for (var added = 0; added < target - files.Count; added++)
        {
            string name, path;
            do
            {
                n++;
                name = $"tempdev{n}";
                path = $"{folder ?? "<folder>\\"}{name}.ndf";
            } while (names.Contains(name) || paths.Contains(path));
            names.Add(name);

            sb.AppendLine($"ALTER DATABASE tempdb ADD FILE (NAME = N'{Escape(name)}', FILENAME = N'{Escape(path)}', " +
                          $"SIZE = {model.SizeMb.ToString(CultureInfo.InvariantCulture)}MB, FILEGROWTH = {growth});");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Grows the smaller files to the largest's size, and gives every file the same growth in MB.</summary>
    internal static string EvenOutTempDbScript(IReadOnlyList<TempDbDataFile> files)
    {
        var largest = files.Max(f => f.SizeMb);
        var growthMb = files.Where(f => !f.IsPercentGrowth && f.Growth > 0).Select(f => f.Growth).DefaultIfEmpty(64).Max();

        var sb = new StringBuilder();
        sb.AppendLine("-- Files can only be made bigger this way; a file larger than the rest has to be shrunk (DBCC SHRINKFILE) instead.");
        foreach (var f in files)
        {
            var parts = new List<string> { $"NAME = N'{Escape(f.Name)}'" };
            if (f.SizeMb < largest)
                parts.Add($"SIZE = {largest.ToString(CultureInfo.InvariantCulture)}MB");
            if (f.IsPercentGrowth || f.Growth != growthMb)
                parts.Add($"FILEGROWTH = {growthMb.ToString(CultureInfo.InvariantCulture)}MB");
            if (parts.Count > 1)
                sb.AppendLine($"ALTER DATABASE tempdb MODIFY FILE ({string.Join(", ", parts)});");
        }
        return sb.ToString().TrimEnd();
    }

    // "D:\TempDB\" for "D:\TempDB\tempdb.mdf"; "/var/opt/mssql/data/" on Linux. Null without a folder.
    internal static string? FolderOf(string physicalName)
    {
        var cut = physicalName.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? null : physicalName[..(cut + 1)];
    }

    private static string Escape(string text) => text.Replace("'", "''");

    // ── Text ──────────────────────────────────────────────────────────────────

    private static ConfigCheck NotChecked(ConfigCheckKind kind, string why, string scope = ConfigCheck.ServerScope) =>
        new(kind, scope, ConfigCheckStatus.NotChecked, "", "", why);

    private static string CurrentDatabase(ServerConfiguration config) =>
        config.Databases.FirstOrDefault(d => d.IsCurrent)?.Name ?? "This database";

    private static string Pending(ConfigOption option) =>
        option.IsPending
            ? $" It has been set to {option.Value.ToString("N0", CultureInfo.CurrentCulture)}, but that isn't in use until RECONFIGURE is run."
            : "";

    private static string Plural(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>"28,672 MB (28 GB)".</summary>
    internal static string FormatMb(long mb) => $"{mb:N0} MB ({FormatGb(mb)})";

    /// <summary>"28 GB", "1.5 GB", "512 MB".</summary>
    internal static string FormatGb(long mb) => mb < 1024 ? $"{mb:N0} MB" : $"{mb / 1024.0:0.#} GB";
}
