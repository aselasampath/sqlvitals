using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.FileIo;

public class FileLatencyTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 6, 0, 0);
    private static readonly DateTime T0    = new(2026, 9, 26, 12, 0, 0);

    internal static FileIoCounters File(
        string db = "Sales", int fileId = 1, string type = "ROWS", string volume = @"D:\", int dbId = 5,
        long reads = 0, long readStall = 0, long bytesRead = 0,
        long writes = 0, long writeStall = 0, long bytesWritten = 0) =>
        new(dbId, db, fileId, $"{db}_{fileId}", type, $@"{volume}{db}_{fileId}.mdf", volume,
            reads, bytesRead, readStall, writes, bytesWritten, writeStall, 8L << 20);

    internal static FileIoReading Reading(DateTime at, params FileIoCounters[] files) => new(at, Start, files);

    private static FileLatencyList Between(FileIoCounters before, FileIoCounters now, double seconds = 10,
                                           FileLatencyThresholds? thresholds = null) =>
        FileLatencyList.Between(Reading(T0, before), Reading(T0.AddSeconds(seconds), now), thresholds ?? FileLatencyThresholds.Default);

    // ── Latency is the change between two readings ─────────────────────────

    [Fact]
    public void Latency_is_the_change_in_stall_over_the_change_in_reads_and_writes_not_the_totals()
    {
        // Since startup: 1,000,000 reads that stalled 50,000,000 ms (50 ms each). In the last
        // 10 s: 1,000 reads that stalled 2,000 ms (2 ms each).
        var before = File(reads: 1_000_000, readStall: 50_000_000, writes: 500, writeStall: 500);
        var now    = File(reads: 1_001_000, readStall: 50_002_000, writes: 600, writeStall: 900);

        var file = Assert.Single(Between(before, now).Files);

        Assert.Equal(2.0, file.ReadLatencyMs);
        Assert.Equal(4.0, file.WriteLatencyMs);
        Assert.Equal(1_000, file.Reads);
        Assert.Equal(100, file.Writes);
        Assert.Equal(FileLatencyStatus.Ok, file.Status);

        // The totals since startup are there to compare with, not what's judged.
        Assert.Equal(50.0, file.StartupReadLatencyMs!.Value, 1);
    }

    [Fact]
    public void Rates_and_sizes_are_over_the_time_between_the_readings()
    {
        var before = File(reads: 0, bytesRead: 0, writes: 0, bytesWritten: 0);
        var now    = File(reads: 500, readStall: 500, bytesRead: 500 * 65_536L, writes: 100, writeStall: 100, bytesWritten: 100 * 8_192L);

        var file = Assert.Single(Between(before, now, seconds: 20).Files);

        Assert.Equal(25, file.ReadsPerSec);
        Assert.Equal(5, file.WritesPerSec);
        Assert.Equal(500 * 65_536 / 20 / 1e6, file.ReadMBps!.Value, 6);
        Assert.Equal(64, file.AvgReadKB);
        Assert.Equal(8, file.AvgWriteKB);
    }

    [Fact]
    public void Only_one_reading_means_no_latency_yet()
    {
        var list = FileLatencyList.Between(null, Reading(T0, File(reads: 100, readStall: 10_000)), FileLatencyThresholds.Default);

        var file = Assert.Single(list.Files);
        Assert.True(list.Measuring);
        Assert.Equal(FileLatencyStatus.Waiting, file.Status);
        Assert.Null(file.ReadLatencyMs);
        Assert.Equal("Measuring…", file.StatusText);
        Assert.Equal(100.0, file.StartupReadLatencyMs);   // but the totals so far can be shown
        Assert.False(file.IsSlow);
    }

    [Fact]
    public void A_file_without_reads_or_writes_between_the_readings_is_idle()
    {
        var file = Assert.Single(Between(File(reads: 10, readStall: 900), File(reads: 10, readStall: 900)).Files);

        Assert.Equal(FileLatencyStatus.Idle, file.Status);
        Assert.Null(file.ReadLatencyMs);
        Assert.Equal(string.Empty, file.ReadLatencyText);
        Assert.Equal("No I/O", file.StatusText);
    }

    [Fact]
    public void Totals_that_went_down_mean_the_counters_were_reset_and_give_no_figure()
    {
        // The database was taken offline and brought back: its totals start from zero.
        var file = Assert.Single(Between(File(reads: 5_000, readStall: 9_000), File(reads: 20, readStall: 40)).Files);

        Assert.Equal(FileLatencyStatus.Restarted, file.Status);
        Assert.True(file.Restarted);
        Assert.Null(file.ReadLatencyMs);
        Assert.Contains("offline", file.Explanation);
    }

    [Fact]
    public void A_file_missing_from_the_earlier_reading_came_online_since_so_all_its_io_counts()
    {
        var list = FileLatencyList.Between(
            Reading(T0, File(db: "Sales")),
            Reading(T0.AddSeconds(10), File(db: "Sales"), File(db: "Archive", dbId: 9, reads: 40, readStall: 120)),
            FileLatencyThresholds.Default);

        var archive = list.Files.Single(f => f.DatabaseName == "Archive");
        Assert.Equal(3.0, archive.ReadLatencyMs);
        Assert.Equal(40, archive.Reads);
    }

    [Fact]
    public void A_database_id_given_to_another_database_is_not_compared_with_the_old_one()
    {
        var list = FileLatencyList.Between(
            Reading(T0, File(db: "Old", dbId: 7, reads: 10, readStall: 10)),
            Reading(T0.AddSeconds(10), File(db: "New", dbId: 7, reads: 20, readStall: 100)),
            FileLatencyThresholds.Default);

        var file = Assert.Single(list.Files);
        Assert.Equal(5.0, file.ReadLatencyMs);   // all 20 reads since New came online
    }

    // ── Thresholds ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ROWS", 20.0, FileLatencyStatus.Ok)]         // not over: equal
    [InlineData("ROWS", 20.5, FileLatencyStatus.Slow)]
    [InlineData("ROWS", 99.0, FileLatencyStatus.Slow)]
    [InlineData("ROWS", 100.0, FileLatencyStatus.Critical)]  // 5 × 20 ms
    [InlineData("LOG",  10.5, FileLatencyStatus.Slow)]       // the log threshold is 10 ms
    [InlineData("LOG",  50.0, FileLatencyStatus.Critical)]
    [InlineData("LOG",  9.5,  FileLatencyStatus.Ok)]
    [InlineData("FILESTREAM", 15.0, FileLatencyStatus.Ok)]   // held to the data threshold
    [InlineData("",     21.0, FileLatencyStatus.Slow)]       // type unknown: data
    public void Files_above_their_threshold_are_slow_and_five_times_it_critical(string type, double readMs, FileLatencyStatus expected)
    {
        var file = Assert.Single(Between(
            File(type: type, reads: 0, readStall: 0),
            File(type: type, reads: 200, readStall: (long)(readMs * 200))).Files);

        Assert.Equal(expected, file.Status);
        Assert.Equal(expected is FileLatencyStatus.Slow or FileLatencyStatus.Critical, file.IsSlow);
    }

    [Fact]
    public void Reads_and_writes_are_judged_separately_and_the_worse_one_decides()
    {
        using var _ = new CultureScope("en-US");
        var file = Assert.Single(Between(
            File(type: "LOG"),
            File(type: "LOG", reads: 10, readStall: 10, writes: 1_000, writeStall: 30_000)).Files);

        Assert.False(file.ReadIsSlow);
        Assert.True(file.WriteIsSlow);
        Assert.False(file.WriteIsCritical);
        Assert.Equal(FileLatencyStatus.Slow, file.Status);
        Assert.Equal(3.0, file.Severity);
        Assert.Contains("Writes averaged 30.0 ms over 1,000 writes", file.Explanation);
        Assert.Contains("10 ms set for log files", file.Explanation);
        Assert.DoesNotContain("Reads averaged", file.Explanation);
    }

    [Fact]
    public void Custom_thresholds_are_used()
    {
        var thresholds = new FileLatencyThresholds(5, 1);
        var file = Assert.Single(Between(File(), File(reads: 100, readStall: 800), thresholds: thresholds).Files);

        Assert.Equal(FileLatencyStatus.Slow, file.Status);
        Assert.Equal(5, file.ThresholdMs);
    }

    [Fact]
    public void A_slow_average_from_a_few_ios_says_so()
    {
        var few  = Assert.Single(Between(File(), File(reads: 2, readStall: 400)).Files);
        var many = Assert.Single(Between(File(), File(reads: 200, readStall: 40_000)).Files);

        Assert.Contains("Only a few", few.Explanation);
        Assert.DoesNotContain("Only a few", many.Explanation);
    }

    // ── The list ───────────────────────────────────────────────────────────

    [Fact]
    public void Slowest_files_come_first_then_the_rest_by_status_then_name()
    {
        var before = Reading(T0,
            File("B_ok", dbId: 1), File("A_idle", dbId: 2), File("C_slow", dbId: 3), File("D_critical", dbId: 4),
            File("E_slower", dbId: 6), File("F_reset", dbId: 7, reads: 99, readStall: 99));
        var now = Reading(T0.AddSeconds(10),
            File("B_ok", dbId: 1, reads: 10, readStall: 10),
            File("A_idle", dbId: 2),
            File("C_slow", dbId: 3, reads: 10, readStall: 300),
            File("D_critical", dbId: 4, reads: 10, readStall: 5_000),
            File("E_slower", dbId: 6, reads: 10, readStall: 600),
            File("F_reset", dbId: 7, reads: 1, readStall: 1));

        var list = FileLatencyList.Between(before, now, FileLatencyThresholds.Default);

        Assert.Equal(["D_critical", "E_slower", "C_slow", "B_ok", "A_idle", "F_reset"], list.Files.Select(f => f.DatabaseName));
        Assert.Equal(3, list.SlowCount);
        Assert.Equal(1, list.CriticalCount);
        Assert.Equal(1, list.IdleCount);
        Assert.Equal(1, list.RestartedCount);
        Assert.Equal("D_critical", list.Slowest!.DatabaseName);
        Assert.Equal(10, list.Seconds);
        Assert.False(list.Measuring);
    }

    [Fact]
    public void Nothing_slow_means_no_slowest()
    {
        var list = Between(File(), File(reads: 10, readStall: 10));

        Assert.Equal(0, list.SlowCount);
        Assert.Null(list.Slowest);
    }

    [Fact]
    public void Notes_from_the_reading_and_the_caller_are_both_kept()
    {
        var reading = new FileIoReading(T0, Start, [File()], Note: "Only this database.");
        var list = FileLatencyList.Between(null, reading, FileLatencyThresholds.Default, note: "Restarted.");

        Assert.Equal("Restarted. Only this database.", list.Note);
    }

    [Fact]
    public void An_unavailable_list_carries_the_reason()
    {
        var list = FileLatencyList.Unavailable("No permission.", FileLatencyThresholds.Default);

        Assert.False(list.Available);
        Assert.Equal("No permission.", list.Problem);
        Assert.Empty(list.Files);
    }

    // ── Volumes ────────────────────────────────────────────────────────────

    [Fact]
    public void A_volume_is_its_files_together_weighted_by_how_busy_each_was()
    {
        var before = Reading(T0,
            File("Sales", dbId: 5, volume: @"D:\"), File("Sales", dbId: 5, fileId: 2, type: "LOG", volume: @"L:\"),
            File("Stock", dbId: 6, volume: @"D:\"));
        var now = Reading(T0.AddSeconds(10),
            File("Sales", dbId: 5, volume: @"D:\", reads: 900, readStall: 900),      // 1 ms
            File("Sales", dbId: 5, fileId: 2, type: "LOG", volume: @"L:\", writes: 100, writeStall: 1_500),
            File("Stock", dbId: 6, volume: @"d:\", reads: 100, readStall: 5_100));   // 51 ms, same volume

        var list = FileLatencyList.Between(before, now, FileLatencyThresholds.Default);

        Assert.Equal(2, list.Volumes.Count);
        var d = list.Volumes.Single(v => v.Volume.Equals(@"D:\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, d.FileCount);
        Assert.Equal(1, d.SlowFileCount);
        Assert.Equal(6.0, d.ReadLatencyMs);                  // 6,000 ms over 1,000 reads, not the 26 ms mean of the files
        Assert.Equal(FileLatencyStatus.Ok, d.Status);
        Assert.Equal("2 (1 slow)", d.FilesText);

        // Only log files there, so the log threshold: 15 ms is over 10.
        var l = list.Volumes.Single(v => v.Volume == @"L:\");
        Assert.Equal(10, l.ThresholdMs);
        Assert.Equal(FileLatencyStatus.Slow, l.Status);
        Assert.Same(l, list.Volumes[0]);                      // slow first
    }

    [Fact]
    public void A_volume_waits_with_its_files()
    {
        var list = FileLatencyList.Between(null, Reading(T0, File(), File(fileId: 2)), FileLatencyThresholds.Default);

        var volume = Assert.Single(list.Volumes);
        Assert.Equal(FileLatencyStatus.Waiting, volume.Status);
        Assert.Null(volume.Change);
    }

    [Fact]
    public void A_volume_whose_files_all_had_their_counters_reset_says_so_rather_than_measuring()
    {
        var list = FileLatencyList.Between(
            Reading(T0, File(volume: @"F:\", reads: 3_000, readStall: 3_000), File(db: "Other", dbId: 9, volume: @"G:\")),
            Reading(T0.AddSeconds(10), File(volume: @"F:\", reads: 10, readStall: 20), File(db: "Other", dbId: 9, volume: @"G:\")),
            FileLatencyThresholds.Default);

        Assert.Equal(FileLatencyStatus.Restarted, list.Volumes.Single(v => v.Volume == @"F:\").Status);
        Assert.Equal(FileLatencyStatus.Idle, list.Volumes.Single(v => v.Volume == @"G:\").Status);
    }

    [Theory]
    [InlineData(@"D:\Data\Sales.mdf",                                  @"D:\")]
    [InlineData(@"d:\Data\Sales.mdf",                                   @"D:\")]
    [InlineData(@"\\filer\sql\Data\Sales.mdf",                          @"\\filer\sql\")]
    [InlineData("/var/opt/mssql/data/Sales.mdf",                        "/var/opt/mssql/data/")]
    [InlineData("https://acct.blob.core.windows.net/data/Sales.mdf",   "https://acct.blob.core.windows.net/data/")]
    [InlineData("",                                                     "")]
    [InlineData("   ",                                                  "")]
    public void The_volume_is_worked_out_from_the_path_when_the_server_cant_say(string path, string expected) =>
        Assert.Equal(expected, FileIoCounters.VolumeFromPath(path));

    // ── Formatting ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.44,   "0.4")]
    [InlineData(12.36,  "12.4")]
    [InlineData(999.9,  "999.9")]
    [InlineData(1250.4, "1,250")]
    public void Milliseconds_show_one_decimal_until_they_are_large(double ms, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, LatencyFigures.FormatMs(ms));
    }

    // ── SQL ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_reading_takes_the_server_clock_with_the_totals_and_skips_the_resource_database()
    {
        Assert.Contains("SYSDATETIME() AS ServerTime", FileIoRepository.FilesSql);
        Assert.Contains("sys.dm_io_virtual_file_stats(NULL, NULL)", FileIoRepository.FilesSql);
        Assert.Contains("io_stall_read_ms", FileIoRepository.FilesSql);
        Assert.Contains("io_stall_write_ms", FileIoRepository.FilesSql);
        Assert.Contains("vfs.database_id <> 32767", FileIoRepository.FilesSql);
        Assert.Contains("sqlserver_start_time", FileIoRepository.ServerSql);
    }

    [Fact]
    public void Azure_SQL_Database_reads_only_its_own_files_without_sys_master_files()
    {
        Assert.Contains("sys.dm_io_virtual_file_stats(DB_ID(), NULL)", FileIoRepository.FilesAzureSql);
        Assert.DoesNotContain("master_files", FileIoRepository.FilesAzureSql);
        Assert.Contains("SYSDATETIME() AS ServerTime", FileIoRepository.FilesAzureSql);
    }

    [Fact]
    public void Volumes_are_only_asked_about_for_online_databases()
    {
        Assert.Contains("d.state = 0", FileIoRepository.VolumesSql);
        Assert.Contains("sys.dm_os_volume_stats", FileIoRepository.VolumesSql);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
