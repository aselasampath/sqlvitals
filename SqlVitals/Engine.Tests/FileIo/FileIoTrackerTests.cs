using SqlVitals.Engine.FileIo;
using static SqlVitals.Engine.Tests.FileIo.FileLatencyTests;

namespace SqlVitals.Engine.Tests.FileIo;

public class FileIoTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0);

    private static FileIoReading At(int seconds, long reads, long readStall) =>
        Reading(T0.AddSeconds(seconds), File(reads: reads, readStall: readStall));

    private static double? ReadMs(FileIoTracker tracker, FileIoWindow window) =>
        tracker.Latencies(window, FileLatencyThresholds.Default)!.Files.Single().ReadLatencyMs;

    [Fact]
    public void Nothing_to_show_before_the_first_reading()
    {
        var tracker = new FileIoTracker();

        Assert.Null(tracker.Latencies(FileIoWindow.LastInterval, FileLatencyThresholds.Default));
    }

    [Fact]
    public void The_first_reading_lists_the_files_waiting_for_a_second()
    {
        var tracker = new FileIoTracker();
        tracker.Add(At(0, 100, 1_000));

        foreach (var window in new[] { FileIoWindow.LastInterval, FileIoWindow.SinceFirstReading })
        {
            var list = tracker.Latencies(window, FileLatencyThresholds.Default)!;
            Assert.True(list.Measuring);
            Assert.Equal(FileLatencyStatus.Waiting, Assert.Single(list.Files).Status);
        }
    }

    [Fact]
    public void Last_interval_compares_the_last_two_readings_and_since_first_the_first_and_last()
    {
        var tracker = new FileIoTracker();
        tracker.Add(At(0,  100, 100));      // first
        tracker.Add(At(10, 200, 200));      // 100 reads, 1 ms each
        tracker.Add(At(20, 300, 5_200));    // 100 reads, 50 ms each

        Assert.Equal(50.0, ReadMs(tracker, FileIoWindow.LastInterval));
        Assert.Equal(25.5, ReadMs(tracker, FileIoWindow.SinceFirstReading));   // 5,100 ms over 200 reads

        Assert.Equal(10, tracker.Latencies(FileIoWindow.LastInterval, FileLatencyThresholds.Default)!.Seconds);
        Assert.Equal(20, tracker.Latencies(FileIoWindow.SinceFirstReading, FileLatencyThresholds.Default)!.Seconds);
    }

    [Fact]
    public void A_server_restart_starts_the_readings_again_and_says_why()
    {
        var tracker = new FileIoTracker();
        tracker.Add(At(0, 1_000, 1_000));
        tracker.Add(At(10, 2_000, 2_000));

        var restartedAt = T0.AddSeconds(15);
        tracker.Add(new FileIoReading(T0.AddSeconds(20), restartedAt, [File(reads: 50, readStall: 500)]));

        var list = tracker.Latencies(FileIoWindow.SinceFirstReading, FileLatencyThresholds.Default)!;
        Assert.True(list.Measuring);
        Assert.Contains("restarted", list.Note);
        Assert.Equal(FileLatencyStatus.Waiting, list.Files.Single().Status);

        // The next reading is compared with the one after the restart, and the note goes.
        tracker.Add(new FileIoReading(T0.AddSeconds(30), restartedAt, [File(reads: 150, readStall: 700)]));
        list = tracker.Latencies(FileIoWindow.SinceFirstReading, FileLatencyThresholds.Default)!;
        Assert.Equal(2.0, list.Files.Single().ReadLatencyMs);
        Assert.Null(list.Note);
    }

    [Fact]
    public void A_clock_that_went_back_starts_the_readings_again()
    {
        var tracker = new FileIoTracker();
        tracker.Add(At(10, 100, 100));
        tracker.Add(At(5, 200, 200));

        var list = tracker.Latencies(FileIoWindow.LastInterval, FileLatencyThresholds.Default)!;
        Assert.True(list.Measuring);
        Assert.Contains("clock went back", list.Note);
    }

    [Fact]
    public void Start_over_forgets_every_reading()
    {
        var tracker = new FileIoTracker();
        tracker.Add(At(0, 100, 100));
        tracker.Add(At(10, 200, 200));

        tracker.StartOver();
        Assert.Null(tracker.Latencies(FileIoWindow.LastInterval, FileLatencyThresholds.Default));

        tracker.Add(At(20, 300, 5_200));
        tracker.Add(At(30, 400, 5_300));
        Assert.Equal(1.0, ReadMs(tracker, FileIoWindow.SinceFirstReading));
    }

    [Fact]
    public void A_reading_that_couldnt_be_taken_is_ignored()
    {
        var tracker = new FileIoTracker();
        tracker.Add(At(0, 100, 100));
        tracker.Add(FileIoReading.Unavailable("No permission."));
        tracker.Add(At(10, 200, 300));

        Assert.Equal(2.0, ReadMs(tracker, FileIoWindow.LastInterval));
    }
}
