using SqlVitals.Engine.ConfigChecks;

namespace SqlVitals.Engine.Tests.ConfigChecks;

public class ConfigurationChecksTests
{
    // 16 processors in one NUMA node, 64 GB; the settings a new install has.
    private static readonly ServerHardware Box = new(16, 1, 16, 64 * 1024);

    private static ServerConfiguration Server(
        long maxDop = 0, long costThreshold = 5, long maxMemoryMb = ConfigurationChecks.UnlimitedServerMemoryMb,
        ServerHardware? hardware = null, bool noHardware = false,
        IReadOnlyList<DatabaseOptions>? databases = null, IReadOnlyList<TempDbDataFile>? tempDb = null,
        string version = "16.0.4135.4", int edition = 3) => new(
        edition,
        version,
        new ConfigOption(maxDop, maxDop),
        new ConfigOption(costThreshold, costThreshold),
        new ConfigOption(maxMemoryMb, maxMemoryMb),
        noHardware ? null : hardware ?? Box,
        databases ?? [new("master", 160, false, "CHECKSUM"), new("Sales", 160, false, "CHECKSUM")],
        tempDb ?? Files(8, 1024));

    private static IReadOnlyList<TempDbDataFile> Files(int count, long sizeMb, long growthMb = 64) =>
        Enumerable.Range(1, count)
            .Select(i => new TempDbDataFile(i == 1 ? "tempdev" : $"temp{i}", $@"T:\TempDB\temp{i}.ndf", sizeMb, false, growthMb))
            .ToList();

    private static ConfigCheck Only(ConfigCheckList list, ConfigCheckKind kind) => Assert.Single(list.Checks, c => c.Kind == kind);

    // ── MAXDOP ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1,  1, 1,  1)]
    [InlineData(4,  1, 4,  4)]      // one node, up to 8: the processors
    [InlineData(8,  1, 8,  8)]
    [InlineData(16, 1, 16, 8)]      // one node, more than 8: 8
    [InlineData(64, 1, 64, 8)]
    [InlineData(16, 2, 8,  8)]      // several nodes of up to 16: per node
    [InlineData(24, 2, 12, 12)]
    [InlineData(32, 2, 16, 16)]
    [InlineData(40, 2, 20, 10)]     // more than 16 per node: half
    [InlineData(80, 2, 40, 16)]     // …at most 16
    public void RecommendedMaxDop_follows_Microsofts_guidance(int processors, int nodes, int perNode, int expected) =>
        Assert.Equal(expected, ConfigurationChecks.RecommendedMaxDop(new ServerHardware(processors, nodes, perNode, 8192)));

    [Fact]
    public void MaxDop_0_on_more_than_8_processors_warns_with_a_script()
    {
        var check = Only(ConfigurationChecks.Run(Server(maxDop: 0)), ConfigCheckKind.MaxDop);

        Assert.Equal(ConfigCheckStatus.Warn, check.Status);
        Assert.Equal("Server", check.Scope);
        Assert.Equal("0 (all 16)", check.Current);
        Assert.Equal("8 or lower", check.Recommended);
        Assert.Contains("all 16 processors", check.Explanation);
        Assert.Contains("N'max degree of parallelism', 8;", check.FixScript);
        Assert.Contains("RECONFIGURE", check.FixScript);
    }

    [Theory]
    [InlineData(0, 4, ConfigCheckStatus.Pass)]      // 0 is all 4: within "no more than the processors"
    [InlineData(0, 8, ConfigCheckStatus.Pass)]
    [InlineData(0, 12, ConfigCheckStatus.Warn)]
    [InlineData(8, 16, ConfigCheckStatus.Pass)]
    [InlineData(4, 16, ConfigCheckStatus.Pass)]     // lower is fine
    [InlineData(1, 16, ConfigCheckStatus.Pass)]
    [InlineData(12, 16, ConfigCheckStatus.Warn)]
    [InlineData(16, 4, ConfigCheckStatus.Pass)]     // capped by the 4 processors there are
    public void MaxDop_passes_at_or_below_the_recommendation(long maxDop, int processors, ConfigCheckStatus expected)
    {
        var hw = new ServerHardware(processors, 1, processors, 16384);
        Assert.Equal(expected, Only(ConfigurationChecks.Run(Server(maxDop: maxDop, hardware: hw)), ConfigCheckKind.MaxDop).Status);
    }

    [Fact]
    public void MaxDop_0_over_several_NUMA_nodes_warns()
    {
        var check = Only(ConfigurationChecks.Run(Server(maxDop: 0, hardware: new(32, 2, 16, 131072))), ConfigCheckKind.MaxDop);
        Assert.Equal(ConfigCheckStatus.Warn, check.Status);
        Assert.Equal("16 or lower", check.Recommended);
        Assert.Contains("across NUMA nodes", check.Explanation);
    }

    [Fact]
    public void MaxDop_1_passes_but_says_nothing_runs_in_parallel()
    {
        var check = Only(ConfigurationChecks.Run(Server(maxDop: 1)), ConfigCheckKind.MaxDop);
        Assert.Equal(ConfigCheckStatus.Pass, check.Status);
        Assert.Contains("no query runs in parallel", check.Explanation);
        Assert.Null(check.FixScript);
    }

    [Fact]
    public void MaxDop_on_a_small_server_recommends_a_range()
    {
        var check = Only(ConfigurationChecks.Run(Server(maxDop: 0, hardware: new(4, 1, 4, 16384))), ConfigCheckKind.MaxDop);
        Assert.Equal("0, or 1 to 4", check.Recommended);
    }

    [Fact]
    public void MaxDop_without_the_processors_is_not_checked_unless_it_is_1()
    {
        Assert.Equal(ConfigCheckStatus.NotChecked,
            Only(ConfigurationChecks.Run(Server(maxDop: 0, noHardware: true)), ConfigCheckKind.MaxDop).Status);
        Assert.Equal(ConfigCheckStatus.Pass,
            Only(ConfigurationChecks.Run(Server(maxDop: 1, noHardware: true)), ConfigCheckKind.MaxDop).Status);
    }

    [Fact]
    public void A_value_not_yet_in_use_is_mentioned()
    {
        var config = Server(maxDop: 0) with { MaxDop = new ConfigOption(8, 0) };
        var check = Only(ConfigurationChecks.Run(config), ConfigCheckKind.MaxDop);

        Assert.Equal(ConfigCheckStatus.Warn, check.Status);          // judged on what's in use
        Assert.Contains("set to 8", check.Explanation);
        Assert.Contains("RECONFIGURE", check.Explanation);
    }

    // ── Cost threshold for parallelism ────────────────────────────────────────

    [Theory]
    [InlineData(5,   ConfigCheckStatus.Warn)]
    [InlineData(24,  ConfigCheckStatus.Warn)]
    [InlineData(25,  ConfigCheckStatus.Pass)]
    [InlineData(50,  ConfigCheckStatus.Pass)]
    [InlineData(150, ConfigCheckStatus.Pass)]
    public void CostThreshold_warns_under_25(long value, ConfigCheckStatus expected)
    {
        var check = Only(ConfigurationChecks.Run(Server(maxDop: 8, costThreshold: value)), ConfigCheckKind.CostThreshold);
        Assert.Equal(expected, check.Status);
        Assert.Equal("50 to start (25 or more)", check.Recommended);
    }

    [Fact]
    public void CostThreshold_default_is_named_and_scripted()
    {
        var check = Only(ConfigurationChecks.Run(Server(costThreshold: 5)), ConfigCheckKind.CostThreshold);
        Assert.Equal("5 (the default)", check.Current);
        Assert.Contains("N'cost threshold for parallelism', 50;", check.FixScript);
    }

    [Fact]
    public void CostThreshold_doesnt_matter_when_nothing_runs_in_parallel()
    {
        var withMaxDop1 = Only(ConfigurationChecks.Run(Server(maxDop: 1, costThreshold: 5)), ConfigCheckKind.CostThreshold);
        var oneCpu      = Only(ConfigurationChecks.Run(Server(costThreshold: 5, hardware: new(1, 1, 1, 4096))), ConfigCheckKind.CostThreshold);

        Assert.Equal(ConfigCheckStatus.Pass, withMaxDop1.Status);
        Assert.Contains("MAXDOP at 1", withMaxDop1.Explanation);
        Assert.Equal(ConfigCheckStatus.Pass, oneCpu.Status);
        Assert.Contains("one processor", oneCpu.Explanation);
    }

    // ── Max server memory ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(2,   1)]        // never less than half
    [InlineData(4,   3)]        // 1 GB for Windows
    [InlineData(8,   6)]        // + 1 per 4 GB from 4 to 16
    [InlineData(16,  12)]
    [InlineData(32,  26)]       // + 1 per 8 GB above 16
    [InlineData(64,  54)]
    [InlineData(256, 222)]
    public void RecommendedMaxServerMemory_leaves_Windows_its_share(int physicalGb, int expectedGb) =>
        Assert.Equal(expectedGb * 1024L, ConfigurationChecks.RecommendedMaxServerMemoryMb(physicalGb * 1024L));

    [Fact]
    public void Max_server_memory_left_unlimited_warns()
    {
        var check = Only(ConfigurationChecks.Run(Server()), ConfigCheckKind.MaxServerMemory);

        Assert.Equal(ConfigCheckStatus.Warn, check.Status);
        Assert.StartsWith("Not set", check.Current);
        Assert.Equal("55,296 MB or less", check.Recommended);
        Assert.Contains("N'max server memory (MB)', 55296;", check.FixScript);
    }

    [Theory]
    [InlineData(55296,  ConfigCheckStatus.Pass)]
    [InlineData(32768,  ConfigCheckStatus.Pass)]        // lower is a choice (other instances, say)
    [InlineData(60000,  ConfigCheckStatus.Warn)]
    [InlineData(131072, ConfigCheckStatus.Warn)]        // more than the server has
    public void Max_server_memory_passes_at_or_below_the_recommendation(long valueMb, ConfigCheckStatus expected) =>
        Assert.Equal(expected, Only(ConfigurationChecks.Run(Server(maxMemoryMb: valueMb)), ConfigCheckKind.MaxServerMemory).Status);

    [Fact]
    public void Max_server_memory_over_physical_memory_says_its_no_limit()
    {
        var check = Only(ConfigurationChecks.Run(Server(maxMemoryMb: 131072)), ConfigCheckKind.MaxServerMemory);
        Assert.Contains("no limit at all", check.Explanation);
    }

    [Fact]
    public void Max_server_memory_without_the_memory_still_warns_when_unlimited()
    {
        var unlimited = Only(ConfigurationChecks.Run(Server(noHardware: true)), ConfigCheckKind.MaxServerMemory);
        var set       = Only(ConfigurationChecks.Run(Server(maxMemoryMb: 28000, noHardware: true)), ConfigCheckKind.MaxServerMemory);

        Assert.Equal(ConfigCheckStatus.Warn, unlimited.Status);
        Assert.Null(unlimited.FixScript);                     // no number to set it to
        Assert.Equal(ConfigCheckStatus.NotChecked, set.Status);
    }

    // ── TempDB ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 4,  ConfigCheckStatus.Warn)]
    [InlineData(4, 4,  ConfigCheckStatus.Pass)]
    [InlineData(4, 16, ConfigCheckStatus.Warn)]
    [InlineData(8, 16, ConfigCheckStatus.Pass)]
    [InlineData(12, 16, ConfigCheckStatus.Pass)]    // more than 8 is added for contention
    [InlineData(2, 1,  ConfigCheckStatus.Pass)]
    public void TempDb_needs_one_file_per_processor_up_to_8(int files, int processors, ConfigCheckStatus expected)
    {
        var config = Server(hardware: new(processors, 1, processors, 16384), tempDb: Files(files, 1024));
        Assert.Equal(expected, Only(ConfigurationChecks.Run(config), ConfigCheckKind.TempDbFileCount).Status);
    }

    [Fact]
    public void TempDb_with_too_few_files_scripts_the_missing_ones_like_the_largest()
    {
        var files = new List<TempDbDataFile>
        {
            new("tempdev", @"T:\TempDB\tempdb.mdf",  4096, false, 256),
            new("temp2",   @"T:\TempDB\tempdb2.ndf", 2048, false, 64),
        };
        var check = Only(ConfigurationChecks.Run(Server(tempDb: files)), ConfigCheckKind.TempDbFileCount);

        Assert.Equal(ConfigCheckStatus.Warn, check.Status);
        Assert.Equal("2 data files", check.Current);
        Assert.Equal("8 (then 4 more at a time if needed)", check.Recommended);
        var adds = check.FixScript!.Split('\n').Where(l => l.StartsWith("ALTER DATABASE tempdb ADD FILE")).ToList();
        Assert.Equal(6, adds.Count);
        Assert.Contains(@"NAME = N'tempdev2', FILENAME = N'T:\TempDB\tempdev2.ndf', SIZE = 4096MB, FILEGROWTH = 256MB", adds[0]);
    }

    [Fact]
    public void Added_file_names_dont_clash_with_existing_ones()
    {
        var files = new List<TempDbDataFile>
        {
            new("tempdev",  "/var/opt/mssql/data/tempdb.mdf",   1024, false, 64),
            new("tempdev2", "/var/opt/mssql/data/tempdev2.ndf", 1024, false, 64),
        };
        var script = ConfigurationChecks.AddTempDbFilesScript(files, 4);

        Assert.DoesNotContain("N'tempdev2'", script);
        Assert.Contains("N'tempdev3', FILENAME = N'/var/opt/mssql/data/tempdev3.ndf'", script);
        Assert.Contains("N'tempdev4'", script);
    }

    [Fact]
    public void TempDb_file_count_without_the_processors()
    {
        Assert.Equal(ConfigCheckStatus.Pass,
            Only(ConfigurationChecks.Run(Server(noHardware: true, tempDb: Files(8, 1024))), ConfigCheckKind.TempDbFileCount).Status);
        Assert.Equal(ConfigCheckStatus.NotChecked,
            Only(ConfigurationChecks.Run(Server(noHardware: true, tempDb: Files(2, 1024))), ConfigCheckKind.TempDbFileCount).Status);
    }

    [Fact]
    public void TempDb_files_of_the_same_size_and_growth_pass()
    {
        var check = Only(ConfigurationChecks.Run(Server(tempDb: Files(8, 1024))), ConfigCheckKind.TempDbFileSizes);
        Assert.Equal(ConfigCheckStatus.Pass, check.Status);
        Assert.Equal("8 × 1,024 MB, growth 64 MB", check.Current);
    }

    [Fact]
    public void TempDb_files_of_different_sizes_warn_and_are_grown_to_the_largest()
    {
        var files = Files(4, 1024).ToList();
        files[2] = files[2] with { SizeMb = 8192 };
        var check = Only(ConfigurationChecks.Run(Server(tempDb: files)), ConfigCheckKind.TempDbFileSizes);

        Assert.Equal(ConfigCheckStatus.Warn, check.Status);
        Assert.Contains("1,024 to 8,192 MB", check.Current);
        Assert.Contains("MODIFY FILE (NAME = N'tempdev', SIZE = 8192MB)", check.FixScript);
        Assert.DoesNotContain("N'temp3'", check.FixScript);   // already the largest
    }

    [Fact]
    public void TempDb_sizes_within_5_percent_count_as_the_same()
    {
        var files = Files(2, 1000).ToList();
        files[1] = files[1] with { SizeMb = 1040 };
        Assert.Equal(ConfigCheckStatus.Pass,
            Only(ConfigurationChecks.Run(Server(tempDb: files)), ConfigCheckKind.TempDbFileSizes).Status);
    }

    [Fact]
    public void TempDb_files_growing_differently_or_by_percent_warn()
    {
        var mixed = Files(2, 1024).ToList();
        mixed[1] = mixed[1] with { Growth = 256 };
        var percent = Files(2, 1024).Select(f => f with { IsPercentGrowth = true, Growth = 10 }).ToList();

        var mixedCheck   = Only(ConfigurationChecks.Run(Server(tempDb: mixed)), ConfigCheckKind.TempDbFileSizes);
        var percentCheck = Only(ConfigurationChecks.Run(Server(tempDb: percent)), ConfigCheckKind.TempDbFileSizes);

        Assert.Equal(ConfigCheckStatus.Warn, mixedCheck.Status);
        Assert.Contains("different amounts", mixedCheck.Explanation);
        Assert.Contains("FILEGROWTH = 256MB", mixedCheck.FixScript);
        Assert.Equal(ConfigCheckStatus.Warn, percentCheck.Status);
        Assert.Contains("percentage", percentCheck.Explanation);
        Assert.Contains("FILEGROWTH = 64MB", percentCheck.FixScript);
    }

    [Fact]
    public void TempDb_that_cant_be_read_isnt_checked()
    {
        var config = Server() with { TempDbFiles = null };
        var list = ConfigurationChecks.Run(config);
        Assert.Equal(ConfigCheckStatus.NotChecked, Only(list, ConfigCheckKind.TempDbFileCount).Status);
        Assert.Equal(ConfigCheckStatus.NotChecked, Only(list, ConfigCheckKind.TempDbFileSizes).Status);
    }

    // ── Databases ─────────────────────────────────────────────────────────────

    [Fact]
    public void Each_database_with_auto_shrink_on_warns_and_the_rest_pass_together()
    {
        var list = ConfigurationChecks.Run(Server(databases:
        [
            new("master", 160, false, "CHECKSUM"),
            new("Sales",  160, true,  "CHECKSUM"),
            new("hr]db",  160, true,  "CHECKSUM"),
            new("Stock",  160, false, "CHECKSUM"),
        ]));
        var checks = list.Checks.Where(c => c.Kind == ConfigCheckKind.AutoShrink).ToList();

        Assert.Equal(3, checks.Count);
        Assert.Equal(new[] { "hr]db", "Sales" }, checks.Where(c => c.IsWarning).Select(c => c.Scope));
        Assert.All(checks.Where(c => c.IsWarning), c => Assert.Equal(("ON", "OFF"), (c.Current, c.Recommended)));
        Assert.Equal("ALTER DATABASE [hr]]db] SET AUTO_SHRINK OFF WITH NO_WAIT;", checks[0].FixScript);

        var pass = Assert.Single(checks, c => c.Status == ConfigCheckStatus.Pass);
        Assert.Equal("2 other databases", pass.Scope);
        Assert.Equal("OFF", pass.Current);
    }

    [Fact]
    public void Databases_that_all_pass_are_one_row()
    {
        var pass = Only(ConfigurationChecks.Run(Server()), ConfigCheckKind.PageVerify);
        Assert.Equal(ConfigCheckStatus.Pass, pass.Status);
        Assert.Equal("All 2 databases", pass.Scope);
    }

    [Theory]
    [InlineData("NONE",                "nothing is checked")]
    [InlineData("TORN_PAGE_DETECTION", "partly written")]
    public void Page_verify_other_than_checksum_warns(string option, string why)
    {
        var list = ConfigurationChecks.Run(Server(databases: [new("Old", 160, false, option)]));
        var check = Only(list, ConfigCheckKind.PageVerify);

        Assert.Equal(ConfigCheckStatus.Warn, check.Status);
        Assert.Equal(option, check.Current);
        Assert.Equal("CHECKSUM", check.Recommended);
        Assert.Contains(why, check.Explanation);
        Assert.Equal("ALTER DATABASE [Old] SET PAGE_VERIFY CHECKSUM WITH NO_WAIT;", check.FixScript);
    }

    [Theory]
    [InlineData("16.0.4135.4", 160)]
    [InlineData("15.0.2000.5", 150)]
    [InlineData("11.0.7001.0", 110)]
    [InlineData("",            null)]
    public void Newest_compatibility_level_is_the_servers_version(string version, int? expected)
    {
        var config = Server(version: version, databases: []);
        Assert.Equal(expected, ConfigurationChecks.NewestCompatibilityLevel(config));
    }

    [Fact]
    public void Databases_below_the_servers_level_warn()
    {
        var list = ConfigurationChecks.Run(Server(databases:
        [
            new("master", 160, false, "CHECKSUM"),
            new("model",  150, false, "CHECKSUM"),
            new("Legacy", 120, false, "CHECKSUM"),
        ]));
        var warnings = list.Checks.Where(c => c.Kind == ConfigCheckKind.CompatibilityLevel && c.IsWarning).ToList();

        Assert.Equal(new[] { "Legacy", "model" }, warnings.Select(c => c.Scope));
        Assert.Equal("120 (SQL Server 2014)", warnings[0].Current);
        Assert.Equal("160 (SQL Server 2022)", warnings[0].Recommended);
        Assert.Contains("Query Store", warnings[0].Explanation);
        Assert.EndsWith("ALTER DATABASE [Legacy] SET COMPATIBILITY_LEVEL = 160;", warnings[0].FixScript);
        Assert.Contains("new databases get", warnings[1].Explanation);
    }

    [Fact]
    public void Managed_Instance_is_held_to_the_highest_level_in_use()
    {
        var config = Server(edition: ServerConfiguration.ManagedInstanceEdition, version: "12.0.2000.8", databases:
        [
            new("master", 160, false, "CHECKSUM"),
            new("App",    140, false, "CHECKSUM"),
        ]);

        Assert.Equal(160, ConfigurationChecks.NewestCompatibilityLevel(config));
        var warning = Assert.Single(ConfigurationChecks.Run(config).Checks, c => c.Kind == ConfigCheckKind.CompatibilityLevel && c.IsWarning);
        Assert.Equal("App", warning.Scope);
        Assert.Contains("reports no version", warning.Explanation);
    }

    // ── Azure SQL Database ────────────────────────────────────────────────────

    private static ServerConfiguration Azure(int? maxDop) => new(
        ServerConfiguration.AzureSqlDatabaseEdition, "12.0.2000.8", null, null, null, null,
        [new("master", 160, false, "CHECKSUM"), new("appdb", 150, true, "CHECKSUM", IsCurrent: true)],
        null, maxDop);

    [Fact]
    public void Azure_SQL_Database_checks_only_its_own_database()
    {
        var list = ConfigurationChecks.Run(Azure(8));

        Assert.NotNull(list.Problem);
        Assert.Equal(ConfigCheckStatus.NotChecked, Only(list, ConfigCheckKind.CostThreshold).Status);
        Assert.Equal(ConfigCheckStatus.NotChecked, Only(list, ConfigCheckKind.MaxServerMemory).Status);
        Assert.Equal(ConfigCheckStatus.NotChecked, Only(list, ConfigCheckKind.TempDbFileCount).Status);
        Assert.All(list.Checks.Where(c => c.IsDatabaseCheck), c => Assert.Equal("appdb", c.Scope));

        Assert.Equal(ConfigCheckStatus.Warn, Only(list, ConfigCheckKind.AutoShrink).Status);
        Assert.Equal(ConfigCheckStatus.Warn, Only(list, ConfigCheckKind.CompatibilityLevel).Status);   // below master's 160
    }

    [Theory]
    [InlineData(8,    ConfigCheckStatus.Pass)]
    [InlineData(4,    ConfigCheckStatus.Pass)]
    [InlineData(0,    ConfigCheckStatus.Warn)]
    [InlineData(16,   ConfigCheckStatus.Warn)]
    [InlineData(null, ConfigCheckStatus.NotChecked)]
    public void Azure_SQL_Database_MAXDOP_is_the_databases_own(int? maxDop, ConfigCheckStatus expected)
    {
        var check = Only(ConfigurationChecks.Run(Azure(maxDop)), ConfigCheckKind.MaxDop);
        Assert.Equal(expected, check.Status);
        Assert.Equal("appdb", check.Scope);
        if (expected == ConfigCheckStatus.Warn)
            Assert.Equal("ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 8;", check.FixScript);
    }

    // ── The list ──────────────────────────────────────────────────────────────

    [Fact]
    public void Every_check_is_there_warnings_first()
    {
        var list = ConfigurationChecks.Run(Server(maxDop: 8, costThreshold: 50, maxMemoryMb: 40000,
            databases: [new("Sales", 160, true, "CHECKSUM")]));

        Assert.Equal(Enum.GetValues<ConfigCheckKind>().ToHashSet(), list.Checks.Select(c => c.Kind).ToHashSet());
        Assert.Equal(ConfigCheckKind.AutoShrink, list.Checks[0].Kind);
        Assert.Equal(1, list.WarnCount);
        Assert.Equal(list.Checks.Count - 1, list.PassCount);
        Assert.Null(list.Problem);
    }

    [Fact]
    public void A_new_install_warns_about_MAXDOP_cost_threshold_and_memory()
    {
        var list = ConfigurationChecks.Run(Server());
        Assert.Equal(
            new[] { ConfigCheckKind.MaxDop, ConfigCheckKind.CostThreshold, ConfigCheckKind.MaxServerMemory },
            list.Checks.Where(c => c.IsWarning).Select(c => c.Kind));
    }

    [Fact]
    public void Without_VIEW_SERVER_STATE_the_list_says_so()
    {
        var list = ConfigurationChecks.Run(Server(noHardware: true));
        Assert.Contains("VIEW SERVER STATE", list.Problem);
    }

    [Fact]
    public void The_fix_script_has_every_warning_and_nothing_else()
    {
        var list = ConfigurationChecks.Run(Server(databases: [new("Sales", 160, true, "NONE"), new("Ok", 160, false, "CHECKSUM")]));
        var script = list.FixScript("PROD01", new DateTime(2026, 9, 26, 10, 30, 0));

        Assert.NotNull(script);
        Assert.StartsWith("-- Configuration fixes for PROD01, generated by SqlVitals on 2026-09-26 10:30.", script);
        Assert.Contains("SqlVitals does not run this script", script);
        Assert.Contains("N'max degree of parallelism'", script);
        Assert.Contains("AUTO_SHRINK OFF", script);
        Assert.Contains("PAGE_VERIFY CHECKSUM", script);
        Assert.DoesNotContain("[Ok]", script);
        Assert.Equal(list.Checks.Count(c => c.FixScript is not null), script!.Split('\n').Count(l => l.TrimEnd() == "GO"));
    }

    [Fact]
    public void No_fix_script_when_everything_passes()
    {
        var list = ConfigurationChecks.Run(Server(maxDop: 8, costThreshold: 50, maxMemoryMb: 40000));
        Assert.Equal(0, list.WarnCount);
        Assert.Null(list.FixScript("PROD01", DateTime.Now));
    }
}
